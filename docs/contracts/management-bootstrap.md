# Bootstrap management endpoint 契约

Bootstrap management 条目是写入本实例自己的本地 Bootstrap 文件的两个 HTTP 操作：由一次性本地
凭据授权的匿名首次创建，以及对已安装实例的管理员更新。

它们自身不实现任何文件格式、校验规则或授权能力。它们对既有能力进行编排——共享的 management
条目基线、实例本地的 `BootstrapFileStore` 及其 `BootstrapConfigurationManager`、一次性凭据
store，以及进程本地的重启闩锁——并把它们的有限结果投射为固定响应。

## 接线

```csharp
builder.Services
    .AddServiceMantle(serviceId, instanceId)
    .AddServiceMantleManagementApiV1()
    .AddManagementCookieAuthentication()
    .AddSecurityResponseHeaders()
    .AddRateLimiting()
    .AddServiceMantleBootstrapManagement();

// The consuming service registers the credential store explicitly.
builder.Services.AddSingleton<IBootstrapCredentialStore>(
    new BootstrapCredentialFileStore(serviceId));

var app = builder.Build();
app.UseServiceMantlePipeline();
app.MapServiceMantleBootstrap();
```

`AddServiceMantleBootstrapManagement` 添加共享的 management 条目约定，与安装状态条目共享同
一个进程本地重启闩锁，并把 `X-ServiceMantle-Bootstrap-Credential` 加入被拒绝的 Header 名列表，
使其值绝不可能进入 ServiceMantle 的日志行。它刻意不注册任何 `IBootstrapCredentialStore`：
ServiceMantle 从不代替消费方预置凭据，也没有任何 HTTP 请求可以配置凭据的存储位置。

当该组被映射多次、没有注册 `IBootstrapCredentialStore`，或者更新条目所解析的固定 management
cookie 认证方案缺失时，宿主启动失败。没有映射该组的宿主不会获得这些前置条件中的任何一个。

## 两个条目

| 条目 | 方法与路径 | 允许的阶段 | 授权 | 速率限制 |
| --- | --- | --- | --- | --- |
| 创建 | `POST {v1}/bootstrap` | `BootstrapConfiguration` | 匿名传输外加一个一次性凭据 Header | Setup 策略 |
| 更新 | `PUT {v1}/bootstrap` | `Completed + Succeeded + Reachable` | `ServiceMantle.ManagementAdmin` **且** `ServiceMantle.ManagementSession`，即固定 management cookie | Management 策略 |

两者都是版本化根的直接子级，映射在受保护的 `MapServiceMantleManagementApiV1` 组旁边而不是其
内部，携带安全响应 Header，并要求恰好一个 `X-ServiceMantle-Request: 1` Header。更新条目从不
读取创建凭据，也从不被创建凭据授权。

## 请求

两个方法都只接受 `application/json`，可选地带一个 UTF-8 `charset` 参数。查询字符串、
`Content-Encoding` Header、任何其他媒体类型、字节顺序标记、无效 UTF-8、非对象根、多余的顶层
值、注释、尾随逗号，以及超过 8 层的 JSON 嵌套深度，全部会被拒绝。原始请求体至多 65536 字节，
在任何解码之前计数：更大的 `Content-Length` 不经读取即被拒绝；没有声明长度的请求体最多读到
超出限制一个字节为止，这足以证明它超限。

线上格式是小驼峰，与磁盘上的 PascalCase 文件相互独立。

| 属性 | 类型 | 规则 |
| --- | --- | --- |
| `database` | object | `POST` 必填。在 `PUT` 上是完整替换，绝不是合并。 |
| `database.provider` | string | 必填、非空白。修剪后 1-64 个字符，以 ASCII 字母或数字开头，随后为字母、数字、`.`、`-` 或 `_`。 |
| `database.connectionString` | string | 必填、非空白。按既有核心规则修剪。 |
| `database.serverVersion` | string 或 null | 可选。省略和显式 `null` 都表示不存在；显式空白字符串会被拒绝。 |
| `masterKey` | string | `POST` 必填、非空白。按既有核心规则修剪。 |

`POST` 要求 `database` 和 `masterKey` 两者。`PUT` 至少要求其中之一，并保留未发送的内容。显式
`null` 或空白 `masterKey` 在两个方法上都会被拒绝，因此核心的“空白表示保留”规则绝不会让 HTTP
语义产生歧义。未知的、大小写不同的以及仅存在于磁盘上的名称——`serviceId`、`instanceId`、
`formatVersion`、`path`、`restartRequired`——都会被拒绝，重复名称也一样，包括用转义写成、解码后
与另一个名称相同的情况。

结构检查只构建既有的请求值并应用上述规则。它不调用任何 provider。provider 是否已注册、是否
需要 server version、数据库是否可达，都在之后由 manager 的既有校验器决定——对创建而言，只在
凭据已被消费之后。

## 凭据

创建条目之所以是匿名的，只是因为它在恰好一个 `X-ServiceMantle-Bootstrap-Credential` Header 中
携带来自凭据契约的一次性凭据，且原样携带：43 个 Base64URL 字符，绝不修剪或规范化。它绝不是
Setup Code、management cookie、数据库密码或 MasterKey。

缺失、格式错误、重复或以逗号合并的 Header 值，以及 store 报告为无效的候选值——过期、未知、
不匹配或已消费——共享同一个响应：

```json
{"errorCode":"management.bootstrap.credential_invalid"}
```

store 失败不属于上述情况：不可用的 store、null 结果和异常都是
`503 {"errorCode":"management.bootstrap.unavailable"}`。

## 顺序与结果

1. 共享阶段门；
2. 认证、速率限制和授权；
3. `X-ServiceMantle-Request` 守卫；
4. HTTP 与结构解析器；
5. 对 `POST`，先检查凭据形状，再消费凭据；
6. manager 的 provider 校验与文件发布；
7. 重启闩锁；
8. 一个固定响应。

第 5 步之前的一切都在不消费任何东西、不写入任何东西的情况下拒绝。只有已消费的凭据才会到达
`CreateAsync`。

| 结果 | HTTP 结果 |
| --- | --- |
| 已发布的创建 | `201 {"restartRequired":true}` |
| 已发布的更新 | `200 {"restartRequired":true}` |
| 不可用的媒体类型、形状、大小、深度、字段、值或不安全 Header | 固定 management `400` |
| 来自校验器的普通候选值拒绝 | 固定 management `400` |
| 不可用的创建凭据 | `401 {"errorCode":"management.bootstrap.credential_invalid"}` |
| `BootstrapFileFailureKind.TargetAlreadyExists` 或 `TargetMissing` | 固定 management `409` |
| `BootstrapFileFailureKind.Unavailable` 或无法识别的值 | `503 {"errorCode":"management.bootstrap.unavailable"}` |
| 校验器内部失败、其他内部失败、内部取消或内部超时 | `503 {"errorCode":"management.bootstrap.unavailable"}` |

校验器失败中 400/503 的划分只看代码。四个内部代码
`candidate.validation_failed`、`candidate.invalid_result`、`database.provider_invalid_result`
和 `database.provider_validation_failed` 是存储失败；其他所有校验器失败都是被拒绝的请求。代码
本身只用于比较，绝不写入响应、消息或日志。

409/503 的划分是 store 自己的分类。消息、路径、内部异常、`HResult` 和单独的存在性检查绝不会被
参考。

## 重启闩锁

闩锁在 manager 确认文件已发布后立即置位，早于考虑之后的取消或失败的响应。发布之前的失败不置位
任何东西；响应丢失的发布保留闩锁，因此进程在写过文件之后绝不会再声称文件未变。闩锁是每进程的：
它从不被持久化，新进程以 `false` 启动，直到它再次写入。安装状态条目报告的是同一个闩锁。

## 取消

在每个调用、返回和异常观察点，调用方自己的取消优先于边界产生的任何结果。当 `RequestAborted`
已处于取消状态时，处理程序抛出恰好携带该 token 的 `OperationCanceledException` 并且不返回任何
结果，无论 store、解析器或 manager 应答了什么——包括携带不同 token 的内部取消。调用方的 token
原样传给凭据 store 和 manager；在写入继续进行的同时，没有任何东西被遗弃给后台任务。当请求未被
取消时，内部取消、内部超时和普通失败都保持为那一个固定的 unavailable 结果。本组不引入任何
endpoint 级别的自身超时。

## 明确非保证

- 凭据消费和文件发布是两个文件，不是一个事务。调用方或进程取消不会回滚已发布的文件，丢失的
  响应不会恢复已消费的凭据，恢复是一项本地运维操作。
- 只有本实例被改变。没有任何东西被热重载、跨实例同步或迁移，激活文件靠的是重启。
- 同步且不协作的 I/O 没有硬性时间界限，阶段门放行也不会锁定之后的阶段。
- 来自不同进程的并发更新没有 compare-and-swap。在排他句柄之外，ACL 和文件替换保持核心 store
  既有的非保证。
- 机密保证只覆盖本组自己的输出和既有的 ServiceMantle 投射器。消费方自己的校验器或凭据 store、
  第三方请求捕获、任意代理或框架日志、进程内存以及调试器都在其范围之外。
- `X-ServiceMantle-Request` Header 是对这组条目的有限 CSRF 缓解，不能替代 CORS、TLS 或代理
  策略。
