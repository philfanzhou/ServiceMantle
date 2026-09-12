# 管理入口授权决策（#312）

状态：issues #94、#95、#96 与 #97 已接受的设计。本文档定义契约；它不实现任何 endpoint。

## 一个带版本的 surface、互不相同的入口类型

每个入口都是 `AddServiceMantleManagementApiV1` 配置的根的直接子级。默认根为
`/management/v1`；下文示例用 `{v1}` 表示这个配置根。路径版本化是唯一的版本协商机制。

这些入口不会削弱 `MapServiceMantleManagementApiV1` 返回的受保护组。Issue #323 拥有另一套独立的、
opt-in 的入口约定与方法感知的 phase 分类。启动校验会拒绝以下情形：通过受保护组映射的入口、
该组上的匿名覆盖、错误的路径或方法、重复的入口元数据、更弱的速率限制、缺失的安全 Header，
以及除下文固定规则之外的任何认证规则。

| 入口 | 方法与路径 | 允许的 phase | 认证或凭据 | 速率限制 |
| --- | --- | --- | --- | --- |
| 安装状态 | `GET`, `HEAD {v1}/status` | 所有已定义的 phase 与 migration/数据库状态 | 匿名 | 管理策略，匿名客户端分区 |
| Bootstrap 创建 | `POST {v1}/bootstrap` | 仅 `BootstrapConfiguration`；migration 不得为 Running 或 Failed | 匿名传输加上一个本地 Bootstrap 凭据 | Setup 策略，客户端 IP 分区 |
| Bootstrap 更新 | `PUT {v1}/bootstrap` | 仅 `Completed + Succeeded + Reachable` | 具备 Admin 权限的当前管理 cookie | 管理策略，操作员分区 |
| Setup 状态 | `GET`, `HEAD {v1}/setup` | `PendingSetup + Succeeded + Reachable`，或 `Completed + Succeeded + Reachable` | 匿名 | Setup 策略，客户端 IP 分区 |
| Setup 完成 | `POST {v1}/setup` | 与 Setup 状态相同，使完成后的重放到达稳定的冲突结果 | 匿名传输加上 pending 期间的当前 Setup Code | Setup 策略，客户端 IP 分区 |
| 登录 | `POST {v1}/session/login` | 仅 `Completed + Succeeded + Reachable` | 匿名传输；消费方登录 adapter 从其受信任的 scoped accessor 获取凭据 | Setup 策略，客户端 IP 分区 |
| 登出 | `POST {v1}/session/logout` | 仅 `Completed + Succeeded + Reachable` | 任何有效的 ServiceMantle 管理身份 cookie | 管理策略，操作员分区 |
| 当前会话 | `GET`, `HEAD {v1}/session` | 仅 `Completed + Succeeded + Reachable` | 任何有效的 ServiceMantle 管理身份 cookie | 管理策略，操作员分区 |

所有其他方法遵循正常路由行为，永远不会到达 handler。保留分支匹配使用既有的、按段感知且
大小写不敏感的 Phase Gate 规则；形似的 `/bootstrap-other` 不是 Bootstrap 入口。配置根仍必须以一个
独立的 `/v1` 段结尾。

Phase Gate 在认证、速率限制、授权和 endpoint handler 之前运行。因此错误的 phase 会返回既有的
`503 {"errorCode":"service.phase.unavailable"}`，而不检查任何凭据或 cookie。除安装状态外，每个被
接受的请求都恰好基于一个 Gate 快照。接受之后的 phase 变化不会召回该请求；文件/数据库并发以及
不可变的创建/消费操作仍是 handler 的职权。

## 公共 HTTP 与安全边界

每个入口都带有既有的 correlation 中间件和强制的安全响应 Header。既有的 Cookie 响应保持不变：

- 无 cookie：`401 {"errorCode":"management.session.unauthenticated"}`；
- 出示但不可接受或已过期的 cookie：
  `401 {"errorCode":"management.session.expired"}`；
- 身份有效但缺少所需权限，或 ServiceMantle claim 无效：
  `403 {"errorCode":"management.session.forbidden"}`。

既有速率限制器保留其 `429 application/problem+json` 契约。Gate、认证和速率限制拒绝不会执行任何
endpoint adapter、凭据检查、provider、文件写入、数据库阶段、cookie 登录或 cookie 登出。

每个不安全入口都要求恰好一个 `X-ServiceMantle-Request` Header，且值精确为 `1`。缺失、为空、重复、
逗号合并或不同的值，会在处理 body 或凭据之前产生固定的管理无效请求响应。写 JSON 的入口也只接受
`application/json`（可选带 UTF-8 charset），并拒绝查询字符串和内容编码。

自定义 Header、仅 JSON 解析以及默认的 `SameSite=Strict` 管理 cookie 共同构成针对这组入口的有限
浏览器 CSRF 缓解。它们能阻止简单的跨站 HTML 表单发出符合要求的请求。它们不能替代正确的 CORS 策略、
受信任来源校验、TLS、代理配置，也不能防御被攻陷的同源脚本。放宽 SameSite 或允许跨源自定义 Header
的消费方自行承担由此产生的策略责任。

请求取消在每个 adapter 边界上具有优先权。原始的 `RequestAborted` token 会被透传，携带该 token 的
`OperationCanceledException` 不会被转换为固定的 HTTP 错误。endpoint 自身拥有的超时，或实现中与之
无关的取消，属于内部不可用响应。同步或不协作的消费方组件无法被强制终止；对其不承诺硬性的墙钟
时间上界。

## 安装状态（#94）

`GET` 和 `HEAD {v1}/status` 在每个 phase 中都是匿名的。Phase Gate 对该入口刻意不读取快照。
handler 读取一次消费方健康来源、读取一次本地 `BootstrapConfigurationManager` 状态，并读取一个
进程本地的 restart-required 闩锁。它只在组合一致时发出成功：

- `BootstrapConfiguration` 可以没有 Bootstrap 文件，或者仅在成功创建之后、本地 restart 闩锁为
  true 时可以有一个有效文件；
- `PendingSetup` 和 `Completed` 要求存在有效的 Bootstrap 文件；
- 损坏/不可访问的 Bootstrap 文件、缺失/为 null/无效的健康快照、不可能的组合、内部异常或内部
  超时均为不可用。

成功 body 恰好包含 `phase`、`migrationStatus`、`databaseStatus`、`bootstrapConfigured` 和
`restartRequired`。枚举值固定为小写下划线格式。它不序列化 `BootstrapManagementStatus`，因此不会
暴露 ServiceId、InstanceId、provider、服务器版本、连接字符串、MasterKey、路径或来源错误码。
`HEAD` 返回与 `GET` 相同的状态和 Header，但没有 body。

| 状态结果 | HTTP 结果 | 副作用 |
| --- | --- | --- |
| 一致的有限观察 | `200 application/json`，带五个固定字段 | 无 |
| 来源/存储失败、无效值、不一致的转换或内部超时 | `503 {"errorCode":"management.status.unavailable"}` | 无 |
| 调用方取消 | 原始取消 | 无 |

结果是一次 handler 调用期间组装的一致性观察，而不是对未来 phase 变化的锁。restart 闩锁初始为
false，仅在本进程成功创建或替换 Bootstrap 后变为 true，并在进程重启时重置。它不声称另一个进程
已经重启，也不声称配置已在别处激活。

## Bootstrap 创建与更新（#95）

两个操作都接受一个完整的、严格解析的 Bootstrap 候选。原始 body 上限为 64 KiB（65536 字节），
JSON 深度至多为 8。未知或重复的属性、格式错误的 UTF-8、字节顺序标记、多余的顶层值、查询字符串、
内容编码以及非 JSON 媒体类型均为无效。连接字符串和 MasterKey 只存在于 body 和既有的输入对象中；
响应和 ServiceMantle 拥有的诊断使用封闭值。

wire 形状为小驼峰，与磁盘上的 PascalCase 文件相互独立。顶层恰好接受 `database` 和 `masterKey`；
`database` 恰好接受 `provider`、`connectionString` 和 `serverVersion`。`serviceId`、`instanceId`、
`formatVersion`、`path` 和 `restartRequired` 不是请求字段。`POST` 要求两个顶层属性都存在；`PUT`
至少要求一个，保留未发送的部分，并把提供的 `database` 视为完整替换而非合并。显式的 `null` 或空白
`masterKey` 在两个操作上都是无效的，因此"保留旧值"绝不会由到达 wire 的某个值来表达。完整的字段、
取值与结果规则见
[Bootstrap 管理契约](management-bootstrap.md)。

### 首次创建

`POST {v1}/bootstrap` 之所以是匿名的，仅仅是因为它要求在一个 `X-ServiceMantle-Bootstrap-Credential`
Header 中携带来自 #324 的独立凭据。它绝不接受 Setup Code、管理 cookie、数据库密码或 MasterKey
作为该凭据。恰好读取一个 Header 值，且完全按其到达的原样读取。缺失、格式错误、重复、逗号合并、
已过期、未知、已被消费以及不匹配的凭据共享同一个 `401` 响应：

```json
{"errorCode":"management.bootstrap.credential_invalid"}
```

在 Gate、速率限制器、不安全请求 Header、请求解析器和结构性候选检查都成功之后，endpoint 在调用
`BootstrapConfigurationManager.CreateAsync` 之前消费该凭据。高熵凭据以固定时间比较，并通过原子的
本地文件操作获取。无效的 HTTP 或结构候选不会消费它。一旦结构有效的候选到达消费环节，之后的
provider 校验、I/O、取消、进程失败或丢失的响应都会使其保持已消费状态。

这种先消费的顺序防止凭据重放，但无法把凭据文件和 Bootstrap 文件原子地纳入同一事务。消费之后、
Bootstrap 发布之前的崩溃可能既没有可用凭据也没有 Bootstrap 文件；本地操作必须显式提供新凭据。
成功发布之后丢失的响应通过状态 endpoint 恢复：重试该凭据会失败，而状态会报告 Bootstrap 已配置且
需要重启。

创建成功仅在 Bootstrap 文件持久发布之后返回 `201 {"restartRequired":true}`，随后设置进程本地的
restart 闩锁。它不返回任何凭据或配置值。

### 已安装后的更新

`PUT {v1}/bootstrap` 绝不是匿名的，也绝不接受 Bootstrap 凭据。它的映射在既有的 Admin 策略之上
增加固定的管理 cookie 会话策略，因此其授权结论来自主机自身的管理 cookie，而不是消费服务配置的
任何默认方案；在没有注册该方案的情况下进行映射会在主机启动前失败。在 Ready 的 Gate 快照下，它
随后调用 `BootstrapConfigurationManager.UpdateAsync`。成功在原子替换之后返回
`200 {"restartRequired":true}` 并设置同一个闩锁。Gate 接受之后的 phase 翻转不会撤销已完成的本地
替换。

| Bootstrap 结果 | HTTP 结果 |
| --- | --- |
| 无效的媒体、形状、大小、不安全 Header，或普通的候选拒绝 | 固定管理 400 |
| 创建目标已存在，或更新目标缺失 | 固定管理 409 |
| 无效的首次创建凭据 | 固定凭据 401 |
| 文件/存储/内部失败、内部校验器失败或内部超时 | `503 {"errorCode":"management.bootstrap.unavailable"}` |
| 创建成功 | `201 {"restartRequired":true}` |
| 更新成功 | `200 {"restartRequired":true}` |

校验器失败在 400/503 之间的划分只依据代码，绝不依据消息：四个内部代码
`candidate.validation_failed`、`candidate.invalid_result`、`database.provider_invalid_result` 和
`database.provider_validation_failed` 属于存储失败，其他所有校验器失败都属于被拒绝的请求。
409/503 的划分依据存储自身的 `BootstrapFileFailureKind`（`TargetAlreadyExists` 和 `TargetMissing`
是冲突）；绝不查阅消息、路径、内部异常或单独的存在性检查。任何代码、消息、连接字符串、MasterKey、
凭据、provider 或服务器版本都不会到达响应。

restart 闩锁在管理器确认文件已发布时立即设置，早于考虑后续取消或响应失败，因此进程在写入文件
之后绝不会声称文件未变。发布之前的失败不设置任何东西。在每个调用、返回和异常观察点上，已中止的
请求优先于边界结果，并传播其自身的 `RequestAborted` token 而非固定结果。

文件发布保持既有的每实例原子创建/替换保证。它不是跨实例更新、活动配置重载、数据库事务，也不是
与凭据消费的原子事务，并且不会在并发更新之间增加 compare-and-swap。

## Setup 状态与完成（#96）

`GET` 和 `HEAD {v1}/setup` 只从当前安装权威透露 `{"status":"pending"}` 或
`{"status":"completed"}`。它们不暴露 Setup Code 的生成、摘要、签发/过期时间、操作员、contributor
或已存储的安装值。Bootstrap phase 会被 Gate 拒绝；Completed 保持可读以获得稳定的终态结果。`HEAD`
没有 body。

`POST {v1}/setup` 在 4 KiB 原始 body 上限和 JSON 深度 4 之下接受恰好一个 JSON `code` 字符串。该值
必须恰好是既有的 32 字符、大小写敏感的 Base64URL Setup Code；绝不做修剪。在 `Completed` 中，
handler 不解析或校验所提供的 code，直接返回固定的管理冲突。在 `PendingSetup` 中，缺失、格式错误、
已过期、不匹配、从未签发或重放的 code 结果使用同一个响应，且不透露任何区别：

```json
{"errorCode":"management.setup.credential_invalid"}
```

endpoint 要求消费方提供显式的事务 executor，使用全新的 scope 和干净的 DbContext。它执行只读的
code 校验，在干净的 staging scope 上调用 `ServiceSetupOrchestrator`，调用 `StageConsumeAsync`，
一次性保存所有 contributor 和安装变更，然后提交。它只在提交之后返回成功。如果 code 在 contributor
staging 之后输掉竞争，或者任何校验、staging、保存、提交、取消或清理失败，executor 会回滚并丢弃
整个 scope，不做重试。核心 orchestrator 的成功仍是"已 staged"，而不是"已提交"。

| Setup 结果 | HTTP 结果 |
| --- | --- |
| Pending 或 Completed 状态读取 | `200` 固定状态 body |
| 无效的媒体、body、不安全 Header 或 contributor 校验 | 固定管理 400 |
| pending 期间无效/过期/缺失/重放的 Setup Code | 固定凭据 401 |
| 已经 Completed、并发完成或版本冲突 | 固定管理 409 |
| 存储/orchestrator/保存/提交/清理失败或内部超时 | `503 {"errorCode":"management.setup.unavailable"}` |
| 提交确认 | `204`，空 body |

消费方事务拥有回滚和处置权。清理失败会使 context 不可用，但仍返回固定的不可用结果。数据库回滚
无法恢复不合规 contributor 已产生的外部副作用；contributor 保留其既有的仅 staging 契约。

## 登录、登出与当前会话（#97）

`POST {v1}/session/login` 是匿名的但受 phase 门控。ServiceMantle 不定义通用的凭据 DTO。所需的
endpoint 专用登录 adapter 接收 `HttpContext` 和原始请求 token，在 64 KiB 原始 body 上限下解析一个
消费方支持的表示形式，仅将其存入消费方受信任的 scoped 凭据 accessor，并调用
`ManagementIdentityProviderInvoker`。公开的核心 provider SPI 继续不接受任意凭据对象。adapter 不得
保留或返回原始凭据。

已认证的 provider 结果会被转换为 ServiceMantle claims principal，并传给固定管理 cookie 方案的
`SignInAsync`。只有 `SignInAsync` 完成才产生 `204`；失败为不可用且不发送部分 cookie。未认证的
结果使用既有的会话 401。失败、null、异常、内部取消、超时或无效的 provider 结果使用
`503 {"errorCode":"management.session.unavailable"}`。消费方提供的来源错误码绝不会被复制到 HTTP
或 ServiceMantle 诊断中，因为语法有效性并不能证明它们不是机密。

`POST {v1}/session/logout` 要求有效的 ServiceMantle 身份但不要求 Admin。它为固定方案调用
`SignOutAsync`，且只在响应 cookie 删除已准备好之后返回 `204`。登出是本地且无状态的：它不会撤销
已复制的 ticket，也不会使另一个客户端上的 cookie 失效。当服务未处于 Ready、Gate 因此返回 503 时，
客户端也可以删除其本地 cookie。

`GET` 和 `HEAD {v1}/session` 同样要求有效身份但不要求 Admin。成功恰好返回 `authenticated`、
`expiresAtUtc` 和一个稳定排序的已定义权限名称数组。它省略操作员 ID/显示名/来源、claim、认证属性、
ticket 材料和 provider 数据。`HEAD` 没有 body。缺失、过期、损坏或 claim 无效的 cookie 保留既有的
401/403 契约。

| 会话结果 | HTTP 结果 | Cookie 效果 |
| --- | --- | --- |
| 登录已认证且 sign-in 完成 | `204` | 签发一个固定方案的 cookie |
| 登录未认证 | 既有会话 401 | 无 |
| 登录 provider/adapter/内部失败或超时 | 固定会话 503 | 无 |
| 以有效身份登出 | `204` | 当前响应使主机范围的 cookie 过期 |
| 以有效身份读取当前会话 | `200` 固定投影 | 既有的滑动续期行为可能适用 |
| 缺失/过期/损坏的 cookie | 既有会话 401 | 既有 Cookie handler 行为 |
| 无效 claim | 既有会话 403 | 无 |

## 必需的实现归属

Issue #323 实现共享的入口类型、方法感知的 Phase Gate、精确的认证/速率元数据、不安全请求 Header
和启动反降级检查。Issue #324 实现本地一次性 Bootstrap 凭据。两个 issue 都不映射任何业务 endpoint。

Issues #94、#95、#96 和 #97 各自只拥有上表中属于自己的行、自己的严格解析器/结果 adapter，以及
自己的成功、失败、取消、安全和并发测试。它们必须保留对 #312 和 #323 的 GitHub 原生依赖；#95 还
额外依赖 #94 和 #324。任何 endpoint 任务都不得把共享的 Gate 或凭据实现复制进自己的 PR。

## 明确的非保证

- 状态观察在其一次一致性采样之后不会冻结 phase、Bootstrap、migration、数据库或 restart 状态。
- Bootstrap 凭据消费与 Bootstrap 文件发布不是一个原子事务；先消费的失败需要显式的本地恢复。
- 安装提交无法回滚不合规 contributor 已执行的外部副作用。
- Cookie 登出不提供服务端撤销权威，也无法使已复制的 ticket 失效。既有的过期和 Data Protection
  边界保持不变。
- 固定的不安全请求 Header 不是全局的 CSRF、CORS、TLS、代理、防火墙或 DDoS 策略。
- ServiceMantle 的凭据负保证覆盖其解析器、固定响应、投影和诊断。它不覆盖消费方 adapter、第三方
  请求日志、原始请求捕获、外部身份系统、调试器或进程内存。
- provider 名称、服务器版本、provider 错误码以及语法有效的消费方字符串不被推定为安全，也不会被
  自动序列化。
