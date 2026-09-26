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

### 可选：更新条目接受管理 Bearer

默认情况下更新条目只接受固定 management cookie，与 0.1.0 完全相同。消费方可以显式指定一个
**已注册**的管理 Bearer 认证方案，让更新条目额外接受它：

```csharp
builder.Services
    .AddServiceMantle(serviceId, instanceId)
    // ...
    .AddServiceMantleBootstrapManagement(options =>
        options.UpdateBearerAuthenticationScheme = "OrderService.ManagementBearer");
```

启用后：

- 更新条目改由固定名称的 policy scheme `ServiceMantle.ManagementBootstrapUpdateCredential` 认证，
  并以固定策略 `ServiceMantle.ManagementBootstrapUpdateSession`（该 policy scheme +
  `RequireAuthenticatedUser` + `ManagementSessionRequirement`）替代 `ServiceMantle.ManagementSession`，
  仍与 `ServiceMantle.ManagementAdmin` 组合。其它条目（session login/logout/current、setup、status、
  bootstrap 创建）与 `ServiceMantle.ManagementSession` 策略不变。
- **选择规则：** 请求中**存在任意** `Authorization` Header（包括空值、多值、非 Bearer 形状）时，
  认证、challenge 与 forbid 全部只交给所配置的 Bearer 方案；否则交给 management cookie。每个请求只
  由一个方案决定，授权评估不会合并两个 principal，无效 Bearer 也不会回退到同时携带的有效 cookie。
- **失败归属：** 选中哪个方案，401/403 就由哪个方案给出。cookie 分支沿用既有 management
  401/403；Bearer 分支的状态码与正文由消费方方案决定，ServiceMantle 不为其发明结果。
- phase、`X-ServiceMantle-Request`、Management 速率限制、请求解析、Admin 权限与成功/失败结果不变；
  处理器与消费方审计看到的操作员来自被选中方案认证出的合法 principal。调用方取消以原 token 传播。
- **启动校验：** 方案名为空白、未注册、等于 `ServiceMantle.ManagementCookie`、等于上述 policy scheme
  本身，或其 handler 是转发型 `PolicySchemeHandler`；management cookie 方案缺失；多次注册给出不同值
  （无参重载等价于不设置，因此与启用的注册同样冲突）——都会让宿主启动失败，异常消息固定且不含方案
  名或其它配置值。等价的重复注册是幂等的。
- **传输：** ServiceMantle 不强制 HTTPS。HTTP 下 Bearer 凭据以明文传输；推荐 HTTPS，由部署者选择
  传输方式。
- **调用方责任：** 所配置的方案只签发管理身份（例如用 `ManagementIdentity` 构造 principal），不要把
  业务或 OIDC token 方案配置为该选项；凭据签发、撤销与生命周期属于消费方。

## 两个条目

| 条目 | 方法与路径 | 允许的阶段 | 授权 | 速率限制 |
| --- | --- | --- | --- | --- |
| 创建 | `POST {v1}/bootstrap` | `BootstrapConfiguration` | 匿名传输外加一个一次性凭据 Header | Setup 策略 |
| 更新 | `PUT {v1}/bootstrap` | `Completed + Succeeded + Reachable` | `ServiceMantle.ManagementAdmin` **且** `ServiceMantle.ManagementSession`，即固定 management cookie；显式启用可选 Bearer 后改为 `ServiceMantle.ManagementBootstrapUpdateSession`（见上节） | Management 策略 |

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
| `masterKey` | string | 可选。`POST` 上省略该属性表示由服务端生成；`PUT` 上省略表示保留既有 key。一旦出现就必须是非空字符串（`POST` 与 `PUT` 同规则）。 |

`POST` 要求 `database`；`masterKey` 可省略。`PUT` 至少要求两者之一，并保留未发送的内容。「省略」
只指属性不出现：显式 `null` 或空白 `masterKey` 在两个方法上都会被拒绝，因此核心的“空白表示保留”
规则绝不会让 HTTP 语义产生歧义。未知的、大小写不同的以及仅存在于磁盘上的名称——
`serviceId`、`instanceId`、`formatVersion`、`path`、`restartRequired`——都会被拒绝，重复名称也一样，
包括用转义写成、解码后与另一个名称相同的情况。

### 省略即由服务端生成

创建请求省略 `masterKey` 时，根密钥由 manager 在同一次 `CreateAsync` 调用内生成：256 位来自
`RandomNumberGenerator` 的密码学安全熵，编码为 43 字符无填充 Base64URL，可安全地经由 shell、
YAML 与环境文件复制。生成值随后走与调用方提供值**完全相同**的候选校验与文件写入路径，没有第二
套规则或第二条写入分支。

- 生成值绝不被返回：不出现在响应体、`BootstrapChangeResult`、`ToString()`、异常消息或任何日志行
  中。创建成功的响应仍然只有 `201 {"restartRequired":true}`，操作员从 Bootstrap 文件读取 key。
- 失败不留下部分状态：校验失败、写入失败或调用方取消都不产生文件，生成值被丢弃。
- 更新条目省略 `masterKey` 的语义完全不变：保留既有 key，绝不生成新 key。

调用方责任：备份 Bootstrap 文件；理解根密钥丢失等于全部受保护数据不可读；在需要「操作员自带
key」的场景（迁移、恢复）继续显式提供 key。生成值的备份、分发与保管属于运维，ServiceMantle 不提
供找回、轮换或导出。

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
