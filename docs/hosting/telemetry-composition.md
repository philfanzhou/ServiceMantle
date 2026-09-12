# HTTP 管道上的 OpenTelemetry 插桩

在 `Build` 之前使用现有的 `ServiceMantleBuilder.AddOpenTelemetryInstrumentation` 扩展，
然后用 `UseServiceMantlePipeline` 组合必需的 HTTP middleware。不需要额外的 Host builder、
自动包注册、exporter、认证或日志 Host。

## 选择矩阵

A 是 `EnableAspNetCoreTracing`，H 是 `EnableHttpClientTracing`，R 是 `EnableRuntimeMetrics`。
调用该扩展时，三个选择器和 `Enabled` 默认为 `true`；完全不调用则不注册任何插桩。禁用注册
会把所有选择器归一化为 false。

| 注册 | A | H | R | TracerProvider | MeterProvider | 结果 |
| --- | --- | --- | --- | --- | --- | --- |
| 缺席 | — | — | — | None | None | HTTP 200、Stop、Dispose |
| Enabled=false | 0 | 0 | 0 | None | None | HTTP 200、Stop、Dispose |
| Enabled=false | 1 | 1 | 1 | None | None | HTTP 200、Stop、Dispose |
| Enabled=true | 1 | 0 | 0 | One | None | 入站 tracing |
| Enabled=true | 0 | 1 | 0 | One | None | 出站 HttpClient tracing |
| Enabled=true | 0 | 0 | 1 | None | One | 运行时指标 |
| Enabled=true | 1 | 1 | 0 | One | None | 两种 tracing 源 |
| Enabled=true | 1 | 0 | 1 | One | One | 入站 tracing 和运行时指标 |
| Enabled=true | 0 | 1 | 1 | One | One | 出站 tracing 和运行时指标 |
| Enabled=true | 1 | 1 | 1 | One | One | 全部三种信号 |
| Enabled=true | 0 | 0 | 0 | None | None | Start 失败 |
| 等价重复 | 相同的有效选择 | | | 至多一个 | 至多一个 | 无重复请求 span 或插桩所有权 |
| 冲突重复 | 不同的有效选择 | | | 不得激活 | 不得激活 | 插桩激活前 Start 失败 |

禁用到启用以及启用到禁用的重复属于冲突。两个选择器不同的禁用注册是等价的。对于成功的行，
provider 创建由信号决定：A 或 H 需要 tracing；R 需要 metrics。插桩不创建任何导出目标。
运行时采集/导出配置是独立的；测试仅对 R 行使用手动的内存读取器。

## 可执行的本地接线

在 ASP.NET Core 应用中引用 `ServiceMantle.OpenTelemetry`（它会带来 AspNetCore 集成）。
本本地示例使用显式固定的 Ready 快照来隔离组合。真实服务必须提供自己的、可取消感知的
`IServiceHealthSnapshotSource`，反映其权威的安装、migration 和数据库状态；注册插桩不会
提供或持久化该状态。

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Health;
using ServiceMantle.Installation;

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5081");
var mantle = builder.Services.AddServiceMantle(
    ServiceId.Parse("telemetry-composition"), InstanceId.Parse("telemetry-01"), serviceVersion: "2.3.4")
    .AddSecurityResponseHeaders()
    .AddSensitiveHeaders()
    .AddRateLimiting()
    .AddServiceMantlePhaseGate();
builder.Services.AddSingleton<IServiceHealthSnapshotSource, DemoSnapshotSource>();

// Omit this call entirely for the absent row. Set Enabled=false for disabled rows.
mantle.AddOpenTelemetryInstrumentation(options =>
{
    options.Enabled = true;
    options.EnableAspNetCoreTracing = true;
    options.EnableHttpClientTracing = true;
    options.EnableRuntimeMetrics = true;
});

await using var app = builder.Build();
app.UseServiceMantlePipeline();
app.MapGet("/ok", () => Results.Ok()).RequireServiceMantleSecurityResponseHeaders();
await app.StartAsync();
try
{
    using var client = new HttpClient();
    using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:5081/ok");
    request.Headers.Add("x-correlation-id", "telemetry-request-correlation");
    using var response = await client.SendAsync(request);
    response.EnsureSuccessStatusCode();
    // A long-running service can await app.WaitForShutdownAsync() here.
}
finally
{
    await app.StopAsync();
}
// await using disposes the Host-owned providers and their instrumentation.

sealed class DemoSnapshotSource : IServiceHealthSnapshotSource
{
    public ValueTask<ServiceHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ServiceHealthSnapshot(ServiceStartupPhase.Completed,
            ServiceMigrationReadinessState.Succeeded, ServiceDatabaseReadinessState.Reachable));
    }
}
```

在 Build 之前配置所有 options。管道只调用一次；不要再单独插入其中的各个 middleware。
可选的转发 Header 信任、endpoint 限流策略和安全/授权元数据仍然保持显式。本示例不安装任何
健康 endpoint、Cookie 方案、Serilog Host 或 exporter。它唯一的 HTTP 连接就是本地测试请求。

## HTTP 行为与身份

管道顺序不变：已配置的转发、关联、Problem Details、路由、安全 Header、阶段门、可选认证、
限流、可选授权，然后是消费方处理器。Telemetry 不绕过 Gate：未就绪的快照仍返回既有的
503 JSON，而响应开始前的未处理异常返回 500 Problem Details。已标记的 endpoint 保留安全
Header 基线，且 `x-correlation-id` 在成功、异常和 Gate 响应上都得以保留。调用方中止的请求
仍然是取消，而不是 telemetry 配置错误。测试的源入口/处理器入口 barrier 显式地将调用方取消
绑定到服务器请求 token；它们不承诺 TCP 断开通知的时延。

两个 provider 都恰好携带 ServiceMantle 自有的 Resource 字段 `service.name`、
`service.version` 和 `service.instance.id`，与 `ServiceLogContext` 一致。使用非机密的 Host
身份元数据。Correlation ID 仍与 W3C trace 身份分离；插桩不会把它改写为 Trace ID。上游
HTTP/运行时插桩拥有其 span 和 metric 属性：Resource 白名单不是任意属性的脱敏或基数保证。

## 所有权与证据

`TelemetryPipelineTests` 演练 Build、管道组合、映射、Start、实际回环 HTTP、Stop 和
Dispose。对于每个选定的 tracing 信号，它在内存中观测已结束的请求 span；对于 R，它附加一个
手动读取器并记录一个受控的 `System.Runtime` 计数器。收集器只在 ServiceMantle 注册已经声明
provider 的地方配置；缺席和禁用行不会通过测试设置获得 provider。另有一行完整插桩的测试在
没有测试读取器、processor 或 exporter 的情况下运行。

正常 Stop 加 Dispose 会移除观测到的 ActivitySource 和 Meter 监听器，重复 Dispose 不会重复
成功的插桩处置。预先取消的 Start 不会报告 `ApplicationStarted`。插桩的 Dispose 异常仍然
可见。出现此类异常后，fixture 会显式清理它仍拥有的句柄；任意失败不必释放所有剩余的 SDK
资源，重试失败的处置也不必只调用插桩一次。测试对全局监听器和手动指标采集使用程序集级串行化，
而不是基于 sleep 的周期性导出检查。

新测试还会检查 Core/AspNetCore 还原后的依赖图中是否有传递性 telemetry 引用：两者都不得
触及 Exporter 或 Prometheus 驱动。`ServiceMantle.OpenTelemetry` 本身自带 OTLP 和
Prometheus exporter，因此它守住的边界是行为性的而非传递性的——安装它不会激活任何
exporter，直到进行匹配的注册调用。本组合不添加包、框架引用或 `eng/packages.json` 条目。

## 限制

- 缺席声明覆盖 ServiceMantle provider/监听器创建和受控的导出工作计数器，不覆盖每个
  .NET 进程线程、定时器或网络连接，也不覆盖消费方自有的 provider。
- 不添加 OTLP、Prometheus、固定的 ServiceMantle 指标、Consul、远程后端、数据库、认证或
  日志 Host。它们各自独立的任务和包不在本矩阵范围内。
- 对导出成功、采样、吞吐量、跨进程传播、丢包、强制终止清理，或中断忽略取消的第三方代码，
  不作任何保证。
- 断言不相关的配置密钥不会出现在捕获的测试日志和安全诊断中。这不保证对 URL/Header/SQL/
  endpoint 或第三方属性的普遍脱敏。
- 不把任何产品身份、事务、migration、持久化或跨请求状态所有权转移给库，此处也不支持
  并发的 DI/options/endpoint 变更。

无效和冲突的注册在任何 ServiceMantle 插桩激活之前被拒绝，包括框架 DI 在托管启动验证器
运行之前解析 meter 或 tracer provider 的情况。冲突行断言 provider 工厂从未被调用；不要
通过绕过框架 metrics 或跳过断言来削弱它们。
