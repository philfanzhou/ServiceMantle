# HTTP 管道上的核心可选能力

在 `Build` 之前显式配置管理 Cookie 认证（A）、健康 endpoint（H）和 Serilog Console（L）。
现有的 `ServiceMantleBuilder` 就是注册入口；不存在额外的组合门面或包扫描。无论哪种组合，
必需的 HTTP 管道都保持启用。

| A | H | L | 可选行为 |
| --- | --- | --- | --- |
| 0 | 0 | 0 | 没有管理 Cookie 方案、健康路由或 ServiceMantle Serilog 生命周期 |
| 0 | 0 | 1 | 仅 Serilog Console |
| 0 | 1 | 0 | 仅健康路由 |
| 0 | 1 | 1 | 健康路由和 Serilog Console |
| 1 | 0 | 0 | 仅管理 Cookie 认证 |
| 1 | 0 | 1 | 管理 Cookie 认证和 Serilog Console |
| 1 | 1 | 0 | 管理 Cookie 认证和健康路由 |
| 1 | 1 | 1 | 全部三项能力 |

## 可执行的本地接线

本示例使用 `ServiceMantle.AspNetCore` 和 `ServiceMantle.Serilog`。修改三个布尔值即可运行
表格中的每一行。固定的 Ready 状态源和临时（ephemeral）Data Protection provider 只是本地
演示/测试选择：对于部署的服务，应替换为消费方自有的状态观测和合适的密钥存储策略。本示例
不实现登录或身份提供方。

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle;
using ServiceMantle.AspNetCore;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Health;
using ServiceMantle.Installation;

var authentication = true;
var health = true;
var logging = true;
var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5080");
var mantle = builder.Services.AddServiceMantle(
    ServiceId.Parse("composition"), InstanceId.Parse("composition-01"), serviceVersion: "1.2.3")
    .AddSecurityResponseHeaders()
    .AddSensitiveHeaders(options => options.DeniedHeaderNames = ["X-Composition-Secret"])
    .AddRateLimiting()
    .AddServiceMantlePhaseGate();

// The required Gate needs state even when optional health endpoints are absent.
builder.Services.AddSingleton<IServiceHealthSnapshotSource, DemoSnapshotSource>();
if (authentication)
{
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
    mantle.AddManagementCookieAuthentication();
}
if (health) mantle.AddServiceMantleHealthEndpoints();
if (logging) builder.AddServiceMantleSerilog();

await using var app = builder.Build();
app.UseServiceMantlePipeline();
app.MapGet("/ok", (HttpContext context) =>
{
    var safeHeaders = context.RequestServices
        .GetRequiredService<RequestHeaderDiagnosticProjector>()
        .Project(context.Request.Headers);
    context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Composition")
        .LogInformation("Composition handled {@Headers}", safeHeaders);
    return Results.Ok();
}).RequireServiceMantleSecurityResponseHeaders();
if (authentication)
{
    app.MapServiceMantleManagementGroup().MapGet("/protected", () => Results.Ok())
        .WithServiceMantleManagementSurface(ManagementSurface.Management)
        .RequireServiceMantleManagementAdmin()
        .RequireServiceMantleSecurityResponseHeaders();
}
if (health) app.MapServiceMantleHealthEndpoints();

await app.StartAsync();
try
{
    using var client = new HttpClient();
    using var response = await client.GetAsync("http://127.0.0.1:5080/ok");
    response.EnsureSuccessStatusCode();
    // A long-running service can await app.WaitForShutdownAsync() here.
}
finally
{
    await app.StopAsync();
}
// await using disposes the Host and its owned services after Stop.

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

生产环境的管理 Cookie 要求 HTTPS（`SecurePolicy.Always`）。测试直接创建受保护 ticket
并手动附加到回环请求上；这不是浏览器登录流程，也不会削弱 Cookie 传输策略。临时密钥在进程
退出时消失。共享密钥、状态持久化、身份验证、登录/登出、migration、事务以及 `DbContext`
保存仍是消费方的责任。

## 请求与生命周期行为

`UseServiceMantlePipeline` 需要上文所示的五项注册，并按既有的相对顺序运行已配置的转发、
关联、Problem Details、路由、安全 Header、阶段门（phase gate）、可选认证、限流和可选授权。
在消费方处理器之前调用它一次，并在启用健康时映射一次健康 endpoint。不要再单独插入其中的
各个 middleware。转发需要自己的显式信任配置；endpoint 授权、安全 Header 和具名限流元数据
仍然保持显式。

启用 A 且状态为 `Completed + Succeeded + Reachable` 时，缺失 Cookie、有效的非 Admin 身份和
有效的 Admin 身份在受保护处理器处分别得到 401、403 和 200。阶段门先运行：有效的 Admin
Cookie 无法绕过未就绪阶段的 503。未启用 A 时，本示例既不注册 Cookie 方案也不映射受保护
处理器；必需的管道仍会处理 `/ok`。

启用 H 时，`/health/live` 返回 200，且不解析快照源。`/health/ready` 和 `/health` 每个请求
读取一次快照，仅在 `Completed + Succeeded + Reachable` 时返回 200；所有其他已定义的状态组合
返回 503。这些库健康 endpoint 绕过 Gate 采样。缺失或抛出异常的源产生 `health.probe_failed`；
内部探测超时产生 `health.probe_timeout`。调用方中止的请求仍然是取消，处理器在离开该取消出口
之前会取消它交给协作式快照源的 token。未启用 H 时，这三条路由都不映射，也不添加任何健康
轮询。Gate 仍会为普通 endpoint 读取状态。不存在共享的 Gate/健康缓存，也没有跨请求/跨实例
的一致性保证。

启用 L 时，关联 middleware 的请求 scope 会到达 Serilog logger，携带 `ServiceName`、
`ServiceVersion`、`InstanceId` 和 `CorrelationId`。经过请求投影器的结构化 `Password` 值和
被拒绝的 `X-Composition-Secret` 值会变成 `[REDACTED]`。`AddSensitiveHeaders` 和
`AddServiceMantleSerilog` 可以以任意顺序出现。没有任何 Header 会被隐式记录。Serilog 入口
会替换已存在的 Microsoft 日志 provider；任何有意保留的额外 provider 需要在之后显式注册。

等价地重复 A/H/L 注册产生一个有效的 Cookie 方案和 Serilog 生命周期；健康仍然需要恰好一次
映射调用。不安全的 Cookie 设置、超出范围的健康超时、无效的 Serilog 级别或相互冲突的重复
设置会在 Host 成功启动之前失败。预先取消的启动不会发出 `ApplicationStarted` 信号。

Stop 和 Dispose 发起一次 Serilog flush。Sink 处置异常是尽力而为的，不能取代关闭；被阻塞的
sink 无法把关闭拖过配置的 flush 等待时间（默认两秒）。超时不会终止 sink：它可能稍后才完成，
测试自有的被阻塞 sink 必须由 fixture 释放并等待。强制终止无法保证 flush。

## 证据与限制

`CoreOptionalCompositionTests` 对全部八行运行 Build、管道组合、映射、Start、真实回环
HTTP、Stop 和 Dispose。它还额外覆盖所有已定义的健康快照、安全失败响应、由 barrier 触发的
取消、两种注册顺序下的 Console 输出、重复/冲突注册、预先取消的启动，以及受控的 sink 处置。
取消 fixture 在源入口 barrier 之后显式地将调用方取消绑定到服务器请求 token；TCP 半关闭通知
时序不属于断言的一部分。该 fixture 还会显式释放并等待它自己的源读取。协作式源自身的 token
是通过其取消注册直接观测的，而不是从调用方的取消结果推断出来的。现有的
AspNetCore 和 Serilog 依赖测试验证已注册的包边界：Core 和
AspNetCore 不会获得 Serilog、EF、数据库驱动、telemetry 或远程 sink 依赖。

缺失断言关注所选能力的 DI 对象、方案、endpoint、快照读取和受控生命周期，而不是零进程
线程/定时器，也不是所有可选程序集都不存在。回环 HTTP 是 fixture 流量。其他消费方注册的
日志或后台服务不在这些观测范围内。不涉及任何数据库、远程 sink、exporter、Consul 或产品
管理 API。配置在 Build 之前固定；此处不支持并发的 DI/options/映射变更。

密钥断言覆盖具名的结构化字段、投影器输出、受控的脱敏日志，以及现有的安全错误响应。它们
不覆盖任意框架事件、调用方插值的消息模板、自由文本，或绕过脱敏器的路径。参见
[结构化日志安全契约](../../LOGGING_SECURITY.md)。本组合不改变
Cookie JSON、健康响应、Gate 路径、强制 middleware 顺序，或未标记 endpoint 和已开始响应
的安全保证。它不承诺取消同步的第三方回调、远程投递，或在任意 Dispose 失败之后完整清理
资源。
