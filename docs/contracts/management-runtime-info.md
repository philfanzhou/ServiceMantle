# Management 运行时信息 endpoint 契约

`MapServiceMantleRuntimeInfo` 向受保护的 management API v1 组添加一个只读 endpoint：
`GET /runtime`。它应答是谁在服务这个请求——已注册的服务名、服务版本和实例身份——外加请求被
放行时所处的启动阶段。它是尽可能最小的身份 endpoint：不读取任何东西、不缓存任何东西，也不
暴露任何诊断、配置或环境细节。

本文档描述该 endpoint 固定了什么、消费服务仍然拥有什么，以及明确不覆盖的内容。周边基线在
[受保护的 management API v1 契约](management-api-v1.md) 中描述。

## 接线

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
management.MapServiceMantleRuntimeInfo();
```

该 endpoint 是选择性启用的。`AddServiceMantleManagementApiV1` 和
`MapServiceMantleManagementApiV1` 从不添加它，因此没有调用 `MapServiceMantleRuntimeInfo` 的
宿主在这里不会暴露任何内容。没有单独的 `Add…` 注册：该 endpoint 投射的身份就是 `AddServiceMantle`
已经注册的那个。

| 设置 | 值 |
| --- | --- |
| 路由 | `GET /runtime`，相对于版本化 management 根 |
| 默认完整路径 | `/management/v1/runtime` |
| 自定义根 | 随路由组移动，例如 `/ops/admin/v1/runtime` |
| 参数 | 没有。该 endpoint 不声明任何路由、查询、Header 或请求体参数 |

每个宿主把该 endpoint 映射多次，或者把它映射到 `MapServiceMantleManagementApiV1` 返回的路由组
之外的任何路由组，都会在宿主启动前失败，失败消息固定为一条且绝不重复任何已配置的值。management
组之下的嵌套组会以同样方式被拒绝，因此完整路径保持恰好是版本化根加 `/runtime`。

## 响应

```json
{
  "serviceName": "catalog",
  "serviceVersion": "1.4.2",
  "instanceId": "catalog-01",
  "phase": "Completed"
}
```

`200 application/json`，恰好包含这四个字段、按此顺序。

| 字段 | 来源 |
| --- | --- |
| `serviceName` | `ServiceLogContext.ServiceName`，由 `AddServiceMantle` 固定 |
| `serviceVersion` | `ServiceLogContext.ServiceVersion`，由 `AddServiceMantle` 固定 |
| `instanceId` | `ServiceLogContext.InstanceId`，由 `AddServiceMantle` 固定 |
| `phase` | 常量 `Completed` |

三个身份值来自不可变的 `ServiceLogContext`；响应体在 endpoint 映射时构建一次，因此任何请求、
配置、Bootstrap、环境、异常或日志对象都绝不会被传入序列化器。不存在可以添加字段的扩展点。

`phase` 不是对健康状态的第二次读取。阶段门只在 `Completed` + `Succeeded` + `Reachable` 时放行
正常的 management 请求，因此被放行的请求已经观察过该阶段，处理程序将其作为常量报告，而不是再次
查询快照源、数据库或缓存。因此每个请求恰好读取一次快照源，就在阶段门里。

## 拒绝

每个拒绝都是既有的组基线，保持不变，且其中任何一个都不会运行投影。

| 情况 | 状态码 | 响应体 |
| --- | --- | --- |
| 阶段、迁移或数据库状态不是 `Completed` + `Succeeded` + `Reachable`；快照源缺失、失败、为 `null`、内部取消或超时 | 503 | `{"errorCode":"service.phase.unavailable"}` |
| 在 runtime 路径上使用 `GET` 以外的方法 | 503 | `{"errorCode":"service.phase.unavailable"}` |
| 未出示 management 会话 | 401 | `{"errorCode":"management.session.unauthenticated"}` |
| 出示的会话无法被接受 | 401 | `{"errorCode":"management.session.expired"}` |
| 已认证主体没有 `Admin` | 403 | `{"errorCode":"management.session.forbidden"}` |
| 速率限制配额耗尽 | 429 | Problem Details，`rate_limit.exceeded` |

应答携带六个强制安全响应 Header，包括 `Cache-Control: no-store`，以及恰好一个
`x-correlation-id`。该 endpoint 不添加任何自己的缓存、`ETag`、后台轮询或 I/O。响应体在
Development 和 Production 中完全一致。

调用方取消始终是调用方取消：已取消的请求从不读取快照源，阶段门观察过程中的取消表现为调用方
自己的取消，而不是 503 或 500。并发请求既不共享 Correlation ID，也不共享取消。

## 消费方职责

- 服务名、版本和实例身份由消费服务提供，可以是自由文本。它们会原样发布给任何 `Admin` 操作员，
  因此绝不能包含机密。
- 提供一个真实的 `IServiceHealthSnapshotSource`、默认认证方案和登录流程，完全按照 management
  API v1 基线的要求。
- 让 Correlation ID 中不出现机密。格式良好的调用方值会被原样复用。

## 不包含与不保证

- 没有安装前或失败阶段的诊断、配置值、连接信息、凭据、运行时环境清单、扩展字段或产品诊断。
  在安装完成之前读取状态由它自己的任务拥有，**不**在这里交付。
- `phase` 描述的是请求被放行的那一刻。它不是对响应写出那一刻、对另一个请求或对另一个实例的
  承诺。放行之后的状态变化不会撤回已放行的请求，也不存在跨请求或跨实例的原子观察。不会执行
  任何业务就绪性贡献者。
- 无机密输出的断言范围是本响应体、上面列出的固定拒绝，以及这个 endpoint 自己的诊断。消费方
  放入公开声明的身份、版本或格式良好的 Correlation ID 中的值不会被检测或移除；被替换的服务或
  策略、第三方日志、进程内存，以及响应开始写出之后的任何事情，也不在范围内。
- 该 endpoint 不引入任何新的缓存、`ETag`、后台刷新、I/O、数据库访问、SQL、包依赖或持久化
  关注点。
