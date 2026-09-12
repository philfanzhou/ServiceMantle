# 参考服务基础遥测验收

`ReferenceTelemetryTests` 通过示例自己的 `ReferenceApplication.CreateBuilder` / `Build`
接缝，在一个真实的回环 Kestrel 宿主上验收参考示例可选启用的基础 instrumentation。这个组合
没有测试替身：开关、provider、resource 和生命周期都来自示例实际运行的代码。

## 接入了什么

`ReferenceService:Telemetry:Enabled` 默认为 `false`。只有能解析为 `true` 的值才会注册任何
东西。当它注册时，示例在既有的 `ServiceMantle.OpenTelemetry` 包上以其默认配置调用
`AddOpenTelemetryInstrumentation`——ASP.NET Core tracing、`HttpClient` tracing 和 .NET
运行时指标——并且不添加自己的 options 系统。

OpenTelemetry resource 是被复用的，而不是新建的：它就是 `AddServiceMantle` 和
`ServiceLogContext` 已经建立的那组 `service.name`、`service.version` 和
`service.instance.id`。

## 没有接入、也不被暗示的内容

没有 OTLP exporter，没有 Prometheus endpoint 或授权，没有 `ServiceMetrics`，没有健康
endpoint，没有健康 source，没有 Consul，也没有固定的服务或安装阶段指标。这个开关不制造任何
阶段，也不会把示例变成一个报告就绪的服务。无论开关是开还是关，`/metrics`、`/health` 和
`/management` 都返回 404。这些能力仍然留在
[#158](https://github.com/philfanzhou/ServiceMantle/issues/158)、
[#156](https://github.com/philfanzhou/ServiceMantle/issues/156) 和
[#109](https://github.com/philfanzhou/ServiceMantle/issues/109) 中；这里的内容不能作为
其中任何一个已完成的证据。

既有的日志开关未被触动。日志开启时，关联和 Problem Details 的行为与之前完全一致，遥测不会
引入第二套认证策略，也不会引入伪造的 `Ready`。

## 矩阵

| 用例 | 显式输入 | 证据 |
| --- | --- | --- |
| 未授权的开关 | 缺失、`false`、空、`yes`、`1`、`not-a-boolean` | 没有 `TracerProvider`，没有 `MeterProvider`，没有示例注册标记；`Microsoft.AspNetCore` 和 `System.Net.Http` 没有监听器；`System.Runtime` instrument 未被启用；`GET /` 仍返回 200；`/metrics`、`/health`、`/management`、`/setup` 仍返回 404；目录保持为空，因此没有创建数据库文件 |
| 启用，带测试收集器 | `true` | 恰好一个 `TracerProvider` 和一个 `MeterProvider`；一次真实的回环请求产生一个服务端 span 和一个客户端 span；测试自己拥有的 reader 收集到一个 `System.Runtime` 指标信号 |
| 启用，无测试收集器 | `true` | 两个 provider 都存在，测试不安装任何 reader、processor 或 exporter，请求被正常服务，且未观察到任何 span 或导出——上面的指标证据确实来自测试自己的 reader |
| Resource 身份 | `true` | 两个 provider 都恰好携带 `service.name`、`service.version`、`service.instance.id`，别无其他；一个无关的配置秘密值不在其中任何一个里 |
| 开关组合 | 日志 × 遥测，全部四种组合 | 每种组合都能启动、服务 `GET /` 并停止；关联 Header 只在日志开启时出现；provider 只在遥测开启时出现 |
| 预先取消的启动 | `true`，已取消的 token | `StartAsync` 抛出取消异常；`ApplicationStarted` 从不被触发；没有 span，没有导出 |
| 被取消的请求 | `true` | 调用方的取消保持为取消而不是变成传输错误，且宿主继续服务下一个请求 |
| 受控的 dispose 失败 | `true`，一个在 dispose 时抛出一次异常的 instrumentation | 失败到达调用方而不是被吞掉；随后 fixture 释放它拥有的句柄，进程级监听器被解除挂接 |
| 等价的重复注册 | `true`，注册两次 | 仍然只有一个 `TracerProvider`、一个 `MeterProvider`、一个 instrumentation 实例，以及每个请求一个服务端 span |
| 冲突的额外注册 | `true` 加上一个禁用运行时指标的注册 | 公开包自己的启动校验拒绝它；`ApplicationStarted` 从不被触发，也没有监听器被挂接。示例不绕过也不弱化该校验 |
| 还原后的依赖图 | - | 示例的 `project.assets.json` 包含基础 instrumentation 包，也包含 `ServiceMantle.OpenTelemetry` 现在随一个包一起提供的 OTLP 和 Prometheus exporter 驱动 |
| 启用时的组合 | `true` | 示例的容器中没有任何属于 `ServiceMantle.OpenTelemetry.Otlp`、`ServiceMantle.OpenTelemetry.Prometheus` 或 `OpenTelemetry.Exporter` 的服务，因此依赖图中存在驱动仍不等于激活了 exporter |

## 隔离

`ActivitySource` 和 `Meter` 的监听器状态是进程级的，不限于单个宿主，因此这些测试运行在自己
的非并行 xUnit collection 中。该 collection 声明在测试类上而不是程序集上，因此
`tests/ServiceMantle.ReferenceService.Tests` 的其余部分保持其原有的并行度。

每个用例拥有自己的临时目录、自己的 `HttpClient`、自己的 `ActivitySource` 和 `Meter` 句柄，
并在 dispose 时移除它们——包括在一次故意失败的 dispose 之后。

## 不保证什么

- **“关闭”意味着 ServiceMantle 拥有的 provider、监听器和受控导出注册未被激活。** 它不意味着
  静态依赖从发布产物中消失，也不是关于整个 .NET 进程不运行任何线程、定时器或 socket 的声明。
- resource 允许列表覆盖三个既有的非秘密身份字段。它**不是**对每个 span 和指标属性的脱敏或
  低基数保证。示例不收集、转换或添加任何 Header、body、query 或连接字段，但由库自身产生的
  instrumentation 属性归库所有。
- 秘密值的否定断言覆盖的是：一个与调用路径无关的合成配置值不会出现在这套接线的 resource、
  它自己的诊断和测试捕获的输出中。它不承诺嵌入任意 URL 或第三方属性中的秘密会被清除。
- 这里的内容不承诺采样率、吞吐量数字、特定的运行时计数器值、成功的导出，或突发终止时的强制
  释放。dispose 检查覆盖的是这套接线实际拥有的资源，在正常停止和 dispose 的情况下。
- 调用方的取消与内部失败保持区分。不承诺强制停止一个不合作的第三方 instrumentation。
- 基础 instrumentation 不创建任何远程导出目标。

## 运行测试

```bash
dotnet build ServiceMantle.slnx -c Release
dotnet test --project tests/ServiceMantle.ReferenceService.Tests -c Release --no-build --no-restore
```

不需要容器、不需要环境变量、除回环外不需要网络，且这些测试从不被跳过。

单独运行本验收：

```bash
dotnet test --project tests/ServiceMantle.ReferenceService.Tests -c Release \
  --filter-class "ServiceMantle.ReferenceService.Tests.ReferenceTelemetryTests"
```

## 相关

- [`samples/ServiceMantle.ReferenceService/README.md`](../../samples/ServiceMantle.ReferenceService/README.md) -
  示例自己对这个开关的描述。
- [`reference-sqlite-deployment.md`](reference-sqlite-deployment.md) - 示例另一个显式的启动
  开关。
