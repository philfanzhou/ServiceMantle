# 共享 management 条目契约

`AddServiceMantleManagementEntries` 和 `MapServiceMantleManagementEntry` 提供选择性启用的条目
约定，[管理入口授权决策](management-entry-authorization.md) 中定义的
每个 management 条目都共享它：每种条目一个固定的路径和方法、方法感知的阶段分类、固定的匿名或
策略认证规则、固定的具名速率限制策略、强制的安全响应 Header，以及不安全请求 Header 守卫。

该约定不映射任何业务处理程序。消费方提供每个条目背后的处理程序；status、Bootstrap、Setup 和
session 行为本身由各自的 issue 拥有。

## 最小接线

```csharp
builder.Services
    .AddServiceMantle(serviceId, instanceId)
    .AddSecurityResponseHeaders()
    .AddSensitiveHeaders()
    .AddRateLimiting()
    .AddManagementCookieAuthentication()
    .AddServiceMantleManagementApiV1()
    .AddServiceMantleManagementEntries();

builder.Services.AddSingleton<IServiceHealthSnapshotSource, ProductSnapshotSource>();

var app = builder.Build();
app.UseServiceMantlePipeline();

app.MapServiceMantleManagementEntry(
    ManagementEntryKind.InstallationStatus,
    ReadInstallationStatus);
app.MapServiceMantleManagementEntry(
    ManagementEntryKind.SessionLogin,
    SignIn);
```

条目映射在 application 上，位于 `MapServiceMantleManagementApiV1` 返回的受保护组旁边，绝不
在其内部。它们复用该入口点配置好的版本化根，因此下文的 `{v1}` 就是
`AddServiceMantleManagementApiV1` 规范化后的结果。宿主只映射它实际服务的条目；未映射的种类
不暴露任何内容。

## 条目表

| 种类 | 方法与路径 | 允许的阶段 | 认证 | 速率限制 |
| --- | --- | --- | --- | --- |
| `InstallationStatus` | `GET`、`HEAD {v1}/status` | 每个阶段；阶段门不读取快照 | 匿名 | Management 策略，匿名客户端分区 |
| `BootstrapCreate` | `POST {v1}/bootstrap` | `BootstrapConfiguration`，迁移 `NotStarted` 或 `Succeeded` | 匿名 | Setup 策略，客户端分区 |
| `BootstrapUpdate` | `PUT {v1}/bootstrap` | `Completed + Succeeded + Reachable` | `ServiceMantle.ManagementAdmin` **且** `ServiceMantle.ManagementSession`，即固定 management cookie 方案 | Management 策略，操作员分区 |
| `SetupStatus` | `GET`、`HEAD {v1}/setup` | `PendingSetup` 或 `Completed`，且 `Succeeded + Reachable` | 匿名 | Setup 策略，客户端分区 |
| `SetupComplete` | `POST {v1}/setup` | 与 `SetupStatus` 相同，因此已完成状态下的重放会到达处理程序 | 匿名 | Setup 策略，客户端分区 |
| `SessionLogin` | `POST {v1}/session/login` | `Completed + Succeeded + Reachable` | 匿名 | Setup 策略，客户端分区 |
| `SessionLogout` | `POST {v1}/session/logout` | `Completed + Succeeded + Reachable` | `ServiceMantle.ManagementSession` | Management 策略，操作员分区 |
| `CurrentSession` | `GET`、`HEAD {v1}/session` | `Completed + Succeeded + Reachable` | `ServiceMantle.ManagementSession` | Management 策略，操作员分区 |

保留分支匹配使用既有的分段感知、大小写不敏感的阶段门规则，因此形似的 `{v1}/bootstrap-other`
不是 Bootstrap 条目，也完全不会被路由。表中未定义的方法绝不会到达处理程序：它被既有的阶段门
规则拦下——该规则针对 management 命名空间内未分类的 endpoint，应答
`503 {"errorCode":"service.phase.unavailable"}`。

由于阶段门在认证、速率限制和授权之前运行，错误的阶段会在不检查任何凭据或 cookie 的情况下被
应答。除 `InstallationStatus` 外，每个被放行的条目恰好观察一次阶段门快照。放行之后的阶段变化
不会召回已经进入处理程序的请求；处理程序保留自己的并发权限。

## 会话授权

`ServiceMantle.ManagementSession` 比 `RequireAuthenticatedUser` 更严格，比
`ServiceMantle.ManagementAdmin` 更宽松：它锁定固定的 management cookie 方案，并要求主体的
ServiceMantle claim 解析为恰好一个合法操作员，但不要求任何权限。没有可接受的 ServiceMantle
claim 的已认证主体会被拒绝。既有的 cookie 结果保持不变——没有 cookie 时是
`401 management.session.unauthenticated`，出示了但不可接受的 cookie 是
`401 management.session.expired`，身份有效但缺少所需权限或携带无效 claim 时是
`403 management.session.forbidden`。该策略只适用于选择性启用的条目；受保护组对消费方提供的
认证方案的通用支持保持不变。

`InstallationStatus` 使用 management 速率限制策略，但被固定在匿名客户端分区，因此出示有效的
management cookie 不能把它移到按操作员的配额上。其他每个 management 条目都保持既有的按操作员
分区。

### Bootstrap 更新锁定认证方案

改写运行中实例的 Bootstrap 文件是本地管理员操作，因此其授权结论必须来自本宿主自己的 management
cookie，而不是消费方恰好配置的某个默认方案。`ServiceMantle.ManagementAdmin` 刻意与认证方式
无关，无法单独表达这一点，所以 `BootstrapUpdate` 同时使用两个策略授权。组合它们会对方案取
并集，而只有 session 策略指名了一个方案，因此该条目的有效方案集合恰好是固定 management cookie
方案，其要求则是 session 策略的合法当前操作员加上管理员权限。

因此，映射 `BootstrapUpdate` 要求已注册固定 management cookie 方案。它的 cookie 结果就是上面
列出的那些：没有 cookie 是 `401 management.session.unauthenticated`，不可接受的 cookie 是
`401 management.session.expired`，合法身份但没有管理员权限是
`403 management.session.forbidden`——没有 cookie 的外部管理员也在其中。没有其他条目和其他策略
发生变化：通用管理员策略和普通的受保护 `MapServiceMantleManagementApiV1` 组仍然接受消费方
自己的认证方案，没有映射 `BootstrapUpdate` 的宿主不会获得任何 cookie 前置条件。

## 不安全请求

每个不安全条目方法都要求恰好一个 `X-ServiceMantle-Request: 1` Header。缺失、为空、重复、以
逗号合并或值不同时，会在处理程序运行之前产生固定的 management 无效请求响应
（`400 application/problem+json`，带 `management.request.invalid`），且响应不携带任何请求值。
`GET` 和 `HEAD` 条目不携带该守卫，守卫对它们也不产生任何副作用。

该 Header、条目处理程序中仅接受 JSON 的解析，以及默认的 `SameSite=Strict` management cookie
共同构成对这组条目的有限浏览器 CSRF 缓解：它们阻止简单的跨站 HTML 表单发出符合要求的请求。它们
不是全局 CSRF 策略，不能替代正确的 CORS 策略、可信来源验证、TLS 或代理配置，也不防范已被攻陷
的同源脚本。放宽 `SameSite` 或允许跨源自定义 Header 的消费方拥有由此产生的策略。

## 启动验证

在至少映射了一个条目的情况下，出现下列情形时宿主启动失败：

- 同一种条目被映射多次；
- 某个条目的路径、方法集合或阶段门面与其固定定义不匹配；
- 某个条目通过受保护的 `MapServiceMantleManagementApiV1` 组映射，或携带第二个 management 面；
- 某个约定降低了基线：受保护条目上的 `AllowAnonymous`、匿名条目上缺失 `AllowAnonymous`、
  `DisableRateLimiting`、被替换的速率限制策略、缺失的安全响应 Header，或缺失的不安全请求守卫；
- 组合好的 ServiceMantle 管道没有在映射条目所用的 builder 上运行；
- 安全响应 Header、具名速率限制策略或阶段门未注册；
- 某个受保护条目的授权策略，或默认的 authenticate、challenge 和 forbid 方案无法解析；或者
  通过 `ServiceMantle.ManagementSession` 解析的条目——session 条目和 `BootstrapUpdate`——在
  没有固定 management cookie 方案的情况下被映射；
- `BootstrapUpdate` 的有效认证方案集合不是恰好等于固定 management cookie 方案。该集合从
  endpoint 的组合授权数据中读取，因此 endpoint 上指名的方案和另一个指名方案的策略都会被拒绝。
  只添加要求而不指名方案的策略不改变集合，会作为更严格的规则被接受，而不是降级。

`MapServiceMantleManagementEntry` 本身会拒绝未定义的种类，也会拒绝没有注册
`AddServiceMantleManagementApiV1` 和 `AddServiceMantleManagementEntries` 的宿主。启动失败
不点名任何操作员、claim、凭据或配置值。

## 明确非保证

- 该约定不实现任何 status 投影、Bootstrap 文件、凭据、Setup 事务、身份 provider 调用，或
  cookie 的签发与清除。这些属于各条目自己的 issue。
- 固定 Header 不是全局 CSRF、CORS、TLS、代理、防火墙或 DDoS 策略。
- 阶段门快照是请求开始时的一次观察，不是对之后阶段变化的锁。
- 运行时路由重新配置不在启动验证范围内；按请求的检查仍会保守地拒绝不匹配的面或方法。
- 方案锁定只覆盖正常启动的宿主的静态 endpoint 与策略组合。它不防范替换固定方案背后实现的
  宿主、恶意的自定义策略 provider、启动后进行的路由或策略变更，或进程内篡改；它对 cookie 的
  信任根、过期、吊销或跨实例行为也没有任何改变。
