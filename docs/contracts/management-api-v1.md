# 受保护的管理 API v1 契约

`AddServiceMantleManagementApiV1` 与 `MapServiceMantleManagementApiV1` 把 ServiceMantle 已经
交付的能力——启动 phase gate、管理授权、具名管理速率限制策略，以及强制的安全响应 Header——组
合成一个可选启用的版本化路由组。该入口点是消费服务自行编写的管理 endpoint 的基线。它不是一条
新流水线，也不发布任何属于自己的 endpoint。

本文档描述该基线固定了什么、消费服务仍然拥有什么，以及哪些内容被明确排除在外。

## 最小接线

```csharp
builder.Services
    .AddServiceMantle(serviceId, instanceId)
    .AddSecurityResponseHeaders()
    .AddSensitiveHeaders()
    .AddRateLimiting()
    .AddManagementCookieAuthentication()
    .AddServiceMantleManagementApiV1();

builder.Services.AddSingleton<IServiceHealthSnapshotSource, ProductSnapshotSource>();

var app = builder.Build();
app.UseServiceMantlePipeline();

var management = app.MapServiceMantleManagementApiV1();
management.MapGet("/settings", ReadSettings);
management.MapPost("/settings", UpdateSettings);
```

`AddServiceMantleManagementApiV1` 在版本化根路径上注册 phase gate 与
`ServiceMantle.ManagementAdmin` 策略。它**不**注册 cookie 认证、身份 provider、持久化、健康
endpoint 或任何其他可选能力。消费服务负责注册安全响应 Header、敏感 Header 注册表与速率限制策
略，提供默认的 authenticate、challenge 与 forbid scheme，并用 `UseServiceMantlePipeline` 组合
请求流水线。`AddManagementCookieAuthentication` 是提供这些 scheme 的一种方式；能产生符合约定
的 principal 的外部认证处理器同样可以接受。

## 根路径、版本与注册

| 设置 | 值 |
| --- | --- |
| 默认根路径 | `/management/v1` |
| 可配置 | 任何最后一段恰好为 `v1` 的根路径，例如 `/ops/admin/v1` |
| 规范化 | 由既有 phase gate 负责：转小写、移除结尾斜杠、2–128 个字符、`[a-z0-9_-]` 段、不与 `/health` 重叠 |
| 版本化 | 仅路径。没有查询、Header 或内容协商，也没有第二个版本 |

所配置的根路径同时也是 phase gate 的管理前缀，因此该组与 `MapServiceMantleManagementGroup`
共享同一个命名空间，而不是创建第二个分类器。同时调用 `AddServiceMantlePhaseGate` 的宿主必须使
用规范化后前缀与快照超时完全相同的设置。重复进行等价的
`AddServiceMantleManagementApiV1` 注册是幂等的。既有的 `AddServiceMantle`、
`AddServiceMantlePhaseGate` 与 `MapServiceMantleManagementGroup` 入口点保持各自的默认值与语
义，包括在不使用本入口点的宿主中 `/management` 这一 phase gate 默认值。

## 该组固定的内容

映射进所返回组的每个子级都会收到以下 endpoint 元数据：

- `ManagementSurface.Management` surface，
- `ServiceMantle.ManagementAdmin` 授权策略，
- `servicemantle.management` 速率限制策略，
- 强制的安全响应 Header 基线。

子级可以在基线之上添加**更严格**的授权要求。把子级（或组）设为匿名、禁用或替换其速率限制策略、
移除其安全 Header 标记或基线策略，以及添加第二个管理 surface，都会在宿主启动之前被拒绝。无效
或未版本化的根路径、冲突的注册或 phase gate、缺少必需能力、缺少默认认证 scheme、未组合
`UseServiceMantlePipeline` 的宿主，以及每个宿主多次映射该组，同样会被拒绝。配置错误携带固定的
消息，绝不重复所配置的值。

根路径之下的 `/status`、`/bootstrap` 与 `/setup` 分支仍保留给各自的 surface，不能由本组提供服
务。宿主在组外映射的 endpoint——包括用旧的 phase gate API 映射到同一根路径下的——不会被隐式
接管。

## 请求顺序与响应矩阵

组合后的流水线按此顺序运行路由、安全响应 Header、phase gate、认证、速率限制与授权，因此较早
阶段的拒绝会先得到应答。

| 情形 | 状态 | Body |
| --- | --- | --- |
| phase、migration 或数据库状态不是 `Completed` + `Succeeded` + `Reachable`；快照来源缺失、失败、为 `null`、内部取消或超时 | 503 | `{"errorCode":"service.phase.unavailable"}` |
| 未出示管理会话 | 401 | `{"errorCode":"management.session.unauthenticated"}` |
| 出示的会话无法被接受 | 401 | `{"errorCode":"management.session.expired"}` |
| 已认证的 principal 不带 `Admin` | 403 | `{"errorCode":"management.session.forbidden"}` |
| 速率限制配额耗尽 | 429 | Problem Details，`rate_limit.exceeded` |
| `ManagementApiResults.InvalidRequest()` | 400 | Problem Details，`management.request.invalid` |
| `ManagementApiResults.Conflict()` | 409 | Problem Details，`management.request.conflict` |
| 未映射异常，或内部 `OperationCanceledException` | 500 | Problem Details，`http.internal_server_error` |
| 处理器结果 | 2xx | 消费服务自己的响应 |

上述 401/403 body 适用于原生 ServiceMantle 管理 cookie。外部认证处理器拥有自己的质询与禁止
body；本入口点不会改写它。在响应尚未开始时，上述每个被应答的响应都携带六个强制安全 Header 与
恰好一个 `x-correlation-id` Header。

## 两个固定结果

```csharp
management.MapPost("/settings", (SettingsRequest request) =>
    request.IsValid
        ? Results.NoContent()
        : ManagementApiResults.InvalidRequest());
```

两个结果都是封闭的。它们不接受自由文本、输入值、异常或自定义扩展，其
`application/problem+json` body 始终恰好包含以下五个字段：

| 字段 | `InvalidRequest()` | `Conflict()` |
| --- | --- | --- |
| `type` | `urn:servicemantle:error:management.request.invalid` | `urn:servicemantle:error:management.request.conflict` |
| `title` | `The request is invalid.` | `The request conflicts with the current state.` |
| `status` | `400` | `409` |
| `errorCode` | `management.request.invalid` | `management.request.conflict` |
| `correlationId` | 本请求已解析出的 Correlation ID | 相同 |

Correlation ID 从请求的已解析槽位读取，绝不从原始请求 Header 读取，因此它与响应 Header 一致。
这些 body 在 Development 与 Production 中完全相同。如果处理器已经开始响应，已发出的响应保持
不变，不追加任何内容。其他格式不变：cookie 401/403 与 phase gate 503 保持 `{"errorCode": …}`
JSON，429 与未映射异常保持既有的 Problem Details 形态。任意的框架或消费方 4xx 响应不会被改写
为这些结果。

## 消费方职责

- 提供一个真实的 `IServiceHealthSnapshotSource`。其准确性与新鲜度是消费服务的责任，快照之后的
  状态变化不会撤回已准入的请求。
- 提供默认认证 scheme，以及在适用时提供登录与会话流程。
- 编写 endpoint 本身，包括其输入验证、重放防护与跨站请求防护。
- 让秘密远离 Correlation ID。格式良好的调用方值会被逐字复用。

## 明确不包含与不保证

- 不包含安装前状态读取、Bootstrap 首次创建或安装后更新、Setup Code HTTP 协议、登录、登出或会
  话入口点，也不包含配置、审计或运行时信息 endpoint。这些由各自的任务负责，**不**在此交付。
- 不重新实现 phase gate、Claims 解析器、cookie 处理、转发 Header、速率限制器、脱敏器或组合流
  水线；不新增核心、EF Core 或 provider 依赖；不访问数据库、文件或消费方事务。
- 保证覆盖成功启动前的固定映射、对既有流水线的正确使用，以及响应尚未开始时的上述路径与结果。
  运行时替换路由、绕过该组、替换库拥有的服务或策略、上游短路，以及之后的传输操作，都在保证之
  外。
- 没有跨请求或跨实例的原子 phase 转换，也没有面向业务贡献者的后台健康调度。
- 无秘密输出的断言覆盖此处的固定结果、既有的未映射异常回退、原生 cookie、phase gate 与速率限
  制器错误，以及 ServiceMantle 自身的诊断。消费方处理器自己写出的响应、自定义映射扩展、第三方
  认证或日志、原始请求与进程内存不在覆盖范围内。
- 没有全局 CSRF、CORS 或 TLS 策略，没有 DDoS 或防火墙防护，没有分布式速率限制，没有多版本兼
  容性，也没有对任意模型绑定错误的自动转换。
