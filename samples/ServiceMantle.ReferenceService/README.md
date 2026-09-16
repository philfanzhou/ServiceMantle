# 参考服务骨架

这是一个由消费方自有的验收宿主，不是生产模板。它刻意只暴露 `GET /`，返回
`status: skeleton`。默认情况下，启动不创建数据库、不执行迁移、不运行 setup contributor、
不预配管理员，也不启用管理/健康/遥测 endpoint。一个 opt-in 的启动部署 gate 可以显式打开；
除非下面每一项输入都已声明，否则它是关闭的。

```bash
dotnet run --project samples/ServiceMantle.ReferenceService -- --urls http://127.0.0.1:5080
```

本示例与外部消费方使用完全相同的公开 ServiceMantle 包项目。仓库内构建对这些可打包项目使用
`ProjectReference`；绝不导入库源文件或使用库内部成员/`InternalsVisibleTo`。以发布包方式消费
是单独的发布验收任务（#113）。样例及其冒烟测试包含在解决方案中；测试项目登记在
`eng/packages.json` 的 ASP.NET Core 之下，因此既有的 ReleaseTool 与 CI 会还原、构建并测试
两个项目，无需单独的样例清单。

| 消费方自有组件 | 当前边界 | 后续 |
| --- | --- | --- |
| `ReferenceApplication` | 公开的组合接缝，一条骨架路由 | 由集成任务共享 |
| `ReferenceDbContext` 与 `Data/Migrations` | 一张 workspace 表；迁移、保存与事务由调用方拥有 | #160 |
| `Database/Sqlite/` | opt-in 的 SQLite 启动部署 gate 与消费方自有的迁移 executor | #112 / #113 |
| `Database/PostgreSql/` | 消费方自有的 PostgreSQL 迁移 executor、单事务初始化 executor 与 opt-in 启动部署 gate | #497 / #160 |
| `ReferenceSetupContributor` | 只读校验与仅 staging 的示例；启动时绝不调用 | [#175](https://github.com/philfanzhou/ServiceMantle/issues/175) |
| `ReferenceSettingDefinitions` | 只有默认值与约束；没有 store、HTTP 或激活 | #177 |
| `ReferenceReadinessContributor` | gate 关闭路径的唯一占位 contributor，返回 `reference.health_not_integrated`；绝不声称就绪 | 由 [#156](https://github.com/philfanzhou/ServiceMantle/issues/156) 在 PostgreSQL 路径改接业务 contributor |
| `Health/PostgreSql/` | 仅当 PostgreSQL 启动 gate 打开时接线：`ReferencePostgreSqlHealthSnapshotSource` 每次请求重读安装行，`ReferencePostgreSqlWorkspaceReadinessContributor` 提供业务就绪否决 | 由 [#156](https://github.com/philfanzhou/ServiceMantle/issues/156) / [#388](https://github.com/philfanzhou/ServiceMantle/issues/388) 交付 |
| `ExternalManagementIdentityPlaceholder` | gate 关闭路径的未配置 provider；PostgreSQL 路径改用 `ReferenceExternalManagementIdentityProvider`（部署配置的操作员目录） | 由 [#109](https://github.com/philfanzhou/ServiceMantle/issues/109) 交付 |
| `Logging/` | opt-in 的 Serilog Console 接线与一行已清理的请求日志 | 由 [#157](https://github.com/philfanzhou/ServiceMantle/issues/157) / [PR #307](https://github.com/philfanzhou/ServiceMantle/pull/307) 交付 |
| `Telemetry/` | opt-in 的基础 ASP.NET Core、HttpClient 与运行时插桩（#158）；opt-in 的阶段指标发布（#521）；opt-in 的管理员会话授权 Prometheus 抓取端点（#520） | 见下文 |

EF SQLite 是消费方的模型载体。启动 gate 关闭时，ServiceMantle 的 SQLite provider 完全不注册：
默认文件是内容根目录下的 `reference.db`，`ReferenceService:DatabasePath` 可以更改它，而且仅仅
启动宿主既不创建该文件也不迁移它。初始迁移与快照放在这里，绝不放在 ServiceMantle 中。

<a id="explicit-sqlite-startup-deployment"></a>

## 显式 SQLite 启动部署

```bash
dotnet run --project samples/ServiceMantle.ReferenceService -- \
  --ReferenceService:SqliteStartup:Enabled true \
  --ReferenceService:SqliteStartup:DeploymentMode SingleInstance \
  --ReferenceService:SqliteStartup:PrepareIfMissing true \
  --ReferenceService:DatabasePath /absolute/path/reference.db \
  --urls http://127.0.0.1:5080
```

`ReferenceService:SqliteStartup:Enabled` 默认为 `false`；只有显式的 `true` 才会激活该
gate。打开时：

- `DeploymentMode` 必须声明且必须是 `SingleInstance`。`Unspecified`、`MultiInstance` 以及任何
  无法解析的值都会被拒绝。是机器上唯一的进程、不持有任何锁，或者省略该设置，绝不会被当作授权。
- `ReferenceService:DatabasePath` 必须是绝对的本地普通文件路径。
- `ReferenceService:SqliteStartup:PrepareIfMissing` 默认为 `false`。只有显式的 `true` 才允许
  创建缺失的文件；无法解析的值会被拒绝。

不可用的输入会在任何 provider、文件或 EF 调用之前失败，且失败信息指出设置名，而不是它读到的
值。preparation 调用与等待进程内单实例回合共用一个固定的 5 秒预算；迁移本身与启动总时长没有
任何上界。样例构建自己的 SQLite 连接——非池化、私有缓存、没有管理连接或密码输入——并且 EF
使用 `Mode=ReadWrite`，因此 EF 默认的 `ReadWriteCreate` 绝不可能在 gate 背后创建文件。

gate 随后以固定顺序运行，每一步都失败关闭：部署模式先从捕获的能力声明校验；一次只读观察判定
目标是否存在；除非显式允许 preparation，缺失的目标会中止启动；消费方自己的 scoped executor
检查、至多迁移一次、再检查。对同一 canonical target 的并发调用会取得一个横跨观察与迁移的
进程内回合，因此一个调用绝不会在兄弟调用的迁移正在写入目标时观察它。只有成功的迁移才允许
宿主完成启动——gate 在任何 hosted service 之前运行，因此绝不会有请求在未迁移的数据库上被
服务。失败会关闭启动并记录一个固定的结果，不含路径、连接字符串、provider 消息或异常文本。

检查刻意保守。空的可读数据库可以认领；完整的已知迁移集合加上可读的 workspace 表为当前版本；
严格前缀为待迁移；未知的历史记录被视为更新的 schema；没有历史记录的应用表、缺失的必需表或
不可读的历史都拒绝该数据库，而不是认领或修复它。检查打开自己的只读连接，绝不调用 `Migrate`、
`EnsureCreated` 或 `SaveChanges`。

没有跨进程或跨主机互斥：两个都声明 `SingleInstance` 的进程是本契约无法检测的部署错误。文件
发布与迁移之间没有任何事务性关联——已提交的迁移或已发布的空文件不会被后来的取消撤销，被中断
的数据库也不会自动修复。本样例是消费方示例；生产服务选择自己的迁移策略。

staging 示例在每次显式调用 `RegisterAsync` 时创建一个新 workspace，带一个生成的 ID 和一个
固定的演示显示名。它不是安装工作流，也不是幂等性契约。它要求单个调用方自有的 scoped
context。只有调用方能保存或提交这些 staged 变更；冒烟测试显式应用迁移并演示这一边界，包括
staging 之前的回滚与取消。它们不调用完整的 setup 编排。

<a id="explicit-postgresql-startup-deployment"></a>

## 显式 PostgreSQL 启动部署

```bash
dotnet run --project samples/ServiceMantle.ReferenceService -- \
  --ReferenceService:PostgreSqlStartup:Enabled true \
  --ReferenceService:PostgreSqlStartup:ConnectionString \
    'Host=127.0.0.1;Port=5432;Database=reference;Username=reference_runtime;Password=…' \
  --ReferenceService:PostgreSqlStartup:PrepareIfMissing true \
  --ReferenceService:PostgreSqlStartup:AdministrativeConnectionString \
    'Host=127.0.0.1;Port=5432;Username=reference_admin;Password=…' \
  --urls http://127.0.0.1:5080
```

`ReferenceService:PostgreSqlStartup:Enabled` 默认为 `false`；只有显式的 `true` 才会激活该
gate。全部输入在 `Build` 之前读取并固定，不可用的输入在任何 provider、网络或 EF 调用之前
失败，且失败信息只指出设置名，不回显读到的值。打开时：

- `ReferenceService:PostgreSqlStartup:ConnectionString` 必填，是目标运行时连接。运行时
  `DbContext`、迁移锁与迁移 executor 的检查连接都只用它；行政连接绝不进入这些组件。
- `ReferenceService:PostgreSqlStartup:PrepareIfMissing` 默认为 `false`。只有显式的 `true`
  才允许创建缺失的目标；无法解析的值会被拒绝。
- `ReferenceService:PostgreSqlStartup:AdministrativeConnectionString` 只在
  `PrepareIfMissing=true` 时必填，也只在那时被读取。行政连接只传给一次准备调用
  （`CREATE DATABASE`，数据库 OWNER 为目标连接的账户），不持久化、不记录、不返回。

该 gate 与 SQLite gate 互斥：两个 `Enabled` 同时为 `true` 时 `CreateBuilder` 在任何 gate
注册或数据库副作用之前抛出，消息只含两个设置名。两个 gate 都未启用时，默认行为完全不变。
固定预算：准备调用 10 秒，迁移锁获取 30 秒；迁移执行与启动总时长没有任何上界。

gate 随后以固定顺序运行，每一步都失败关闭：

1. 一次只读观察判定目标是否存在。缺失且未授权 → `TargetMissing`，数据库仍不存在；缺失且
   已授权 → 一次准备调用创建空库，失败 → `PreparationFailed`。准备成功后不再重新观察，
   空库与否则由锁内检查判定。
2. 服务器不可达、目标存在但不可连接（含认证失败与权限拒绝），或观察抛出异常 →
   `TargetUnavailable`。已存在但不可用的目标绝不创建、不迁移、不被接管。
3. 迁移编排走 `PostgreSqlMigrationLockProvider` 的真实 session 级 advisory lock（没有部署
   模式分支）。锁获取超时或失败 → `LockUnavailable`；锁内检查发现 `VersionTooNew` →
   `VersionTooNew`；初始化或迁移失败、final state 无效、迁移作用域释放失败 →
   `MigrationFailed`。执行器被调用之后租约丢失 → `LockUnavailable`
   （`ExecutorWasCalled=true`），已提交的副作用保留。
4. 编排成功后在同一迁移作用域内读取安装行：缺失 → `InstallationStateMissing`；行无效或
   无法读取 → `InstallationStateInvalid`。两种情况都不创建、不修复、不接管，留给人工处置。
   行有效 → `Ready`，并发布由该行解析出的启动阶段（`PendingSetup` 或 `Completed`）。

结果只包含有限 `Outcome`、`ExecutorWasCalled` 与 Ready 时的启动阶段，不含连接串、密码、
provider 消息或异常文本。只有 `Ready` 允许宿主完成启动——gate 在任何 hosted service 之前
运行，因此不会有请求落在未迁移或未注册的数据库上；任何有限失败都会让 `StartAsync` 抛出固定
消息并阻止宿主监听。调用方取消向上传播为调用方 token 的 `OperationCanceledException`，且
不发布结果。

本 gate **不**保证：不自动接管任意旧库（缺安装行或安装行无效一律关闭失败）；不回滚已提交的
迁移或 `CREATE DATABASE`；不保证 gate 结果之后的状态新鲜度（实时健康见下文的
[阶段 Live/Ready 健康接线](#postgresql-live-ready-health)）；不签发 Setup Code（
[#175](https://github.com/philfanzhou/ServiceMantle/issues/175)）；不做双实例最终 E2E（
[#165](https://github.com/philfanzhou/ServiceMantle/issues/165)）；不保证行政连接端点的 TLS
与网络信任。调用方责任：可信的 PostgreSQL 端点、最小权限的运行时账户、首次准备之后移除
行政凭据、部署侧负责备份。

<a id="postgresql-management-session"></a>

## 管理会话与外部身份接线

当且仅当 PostgreSQL 启动 gate 被显式打开时，样例在同一个分支内接线共享管理会话。gate 关闭
的所有路径逐字不变：没有 cookie 方案、没有管理路由、placeholder provider 仍是唯一的身份注册。

外部身份来自部署配置，不是服务自建的本地管理员：

| 配置键 | 说明 |
| --- | --- |
| `ReferenceService:Management:RootKey` | **必填**。管理 cookie 共享 key ring 的根密钥，至少 32 个字符；缺失或过短则组合失败（fail closed），绝不回落到进程内随机值。经由安全通道提供；轮换根密钥意味着既有 cookie 全部失效。 |
| `ReferenceService:Management:Operators:0:Id` | 操作员标识（登录用户名）。 |
| `ReferenceService:Management:Operators:0:DisplayName` | 可选显示名。 |
| `ReferenceService:Management:Operators:0:Permissions` | 逗号分隔的权限名，如 `management.read,management.admin`。 |
| `ReferenceService:Management:Operators:0:Credential` | 操作员共享秘密。与用户名都经 SHA-256 摘要后做固定时间比较。 |

操作员目录是**部署提供的外部事实**——样例不发明网络认证协议、不自建账户存储、也没有「首次运行
创建管理员」路径。未配置目录时 provider 返回 `reference.external_identity_not_configured`，
登录一律失败（HTTP 表现为固定的 `503 management.session.unavailable`）。

登录走共享条目 `POST {v1}/session/login`：请求体是严格解析的
`{"username":"…","secret":"…"}` JSON 信封（`application/json`，未知成员、重复成员与非字符串一律
拒绝）；凭据只存在于本次请求的 scoped accessor 与 provider 之间。共享 key ring 经
`PersistKeysToServiceMantleEfCore` 落在 gate 的同一 PostgreSQL 目标
（`service_data_protection_keys` 表，由第三个迁移创建，密文存储），因此指向同一数据库、使用同一
根密钥的多个实例接受同一张 cookie，进程重启后同样有效。`GET {v1}/session` 读当前会话，
`POST {v1}/session/logout` 本地登出——已复制的 ticket 在过期前仍然有效，这是共享契约声明的非保
证，不是样例可以加强的。

明确不保证与不在范围：操作员凭据与根密钥的机密性由部署来源负责（样例只保证它们不进入日志、响
应、异常与诊断）；目录条目不是生产身份体系；不做密钥轮换或已签发 cookie 的重加密；Consul、遥测
exporter 与配置管理 API 属于各自的后续任务。调用方责任：通过安全通道分发凭据与根密钥、生产部署
使用 HTTPS、备份 Bootstrap 与数据库中的 key ring。

真库验收见 [`ReferenceManagementSessionTests`](../../tests/ServiceMantle.ReferenceService.Tests/)
与双实例进程验收
[`ReferenceManagementCrossInstanceTests`](../../tests/ServiceMantle.ReferenceService.Tests/)。

## 阶段 Live/Ready 健康接线

当且仅当上面的 PostgreSQL 启动 gate 被显式打开时，样例才接线健康能力；**不新增任何配置键**。
gate 关闭的所有路径（默认、SQLite、日志、遥测开关）行为完全不变：`/health*` 仍返回 404，
`ReferenceReadinessContributor` 仍是唯一的 readiness contributor，也不注册快照来源或
`IDbContextFactory<ReferencePostgreSqlDbContext>`。

接线复用 gate 已注册的目标连接 factory 与 Ready 结果，注册三样东西：

- `ReferencePostgreSqlHealthSnapshotSource`（单例，`IServiceHealthSnapshotSource`）：每次
  `GetSnapshotAsync` 先确认 gate 结果为 Ready，再从 `IDbContextFactory<ReferencePostgreSqlDbContext>`
  创建一个本次调用独占的 context，经 `EfCoreServiceInstallationStore` 调用一次
  `FindAsync(serviceId)`，随后释放该 context。它不缓存、不后台轮询、不重试、不写入、也不捕获
  请求 scope 的 context，因此安装行的变化在下一次请求即被反映，无需重启。
- `mantle.AddServiceMantleHealthEndpoints()`：使用库的默认探测预算，映射 `/health/live`、
  `/health/ready` 与 `/health`。不接线 Phase Gate、管理 API 或安装状态端点。
- `ReferencePostgreSqlWorkspaceReadinessContributor`（经 `AddServiceReadinessContributor<T>`）：
  业务就绪否决，替换占位 contributor；两者同为 `Order 100`，因此 PostgreSQL 路径只注册业务者，
  以免 `HealthStartupValidator` 因重复 order 拒绝启动。

字段来源固定：`phase` 只来自本次读取到的非 null 安装行，经
`ServiceStartupPhaseResolver.Resolve(true, state)` 解析，绝不使用 gate 结果里的启动阶段；
`migrationStatus` 恒为 `succeeded`（依据是 gate 已 Ready，宿主只在此后接收请求）；
`databaseStatus` 只在本次读取成功时为 `reachable`，本来源绝不产出 `unreachable` 快照；
`errorCode` 为 null。

完整的结果矩阵（入口 × 外部输入 × 事件 → 唯一结果）以
[#156](https://github.com/philfanzhou/ServiceMantle/issues/156) 的「语义模型」一节为权威，
本节不复制第二套规则。其要点：`/health/live` 恒 200 且不读数据库；安装行 `PendingSetup` →
503 `phase: pendingSetup`；`Completed` 且至少一个 workspace → 200 `ready`；`Completed` 且
workspace 为空 → 503 `reference.workspace_missing`；workspace 不可读 →
503 `reference.workspace_probe_failed`；gate 未 Ready、安装行缺失/无效、数据库拒绝连接或读取
失败 → 503 `health.probe_failed` 且 `phase` 为 null（绝不回落为 `pendingSetup`）；读取超出探测
预算 → 503 `health.probe_timeout`；调用方取消 → 以请求 token 的 `OperationCanceledException`
结束，不写出响应。

本接线**不**保证：请求时刻之后的状态新鲜度，或跨实例的原子阶段转换与一致观察（
[#173](https://github.com/philfanzhou/ServiceMantle/issues/173)）；`migrationStatus` 在宿主生命
周期内恒为 `succeeded`，启动后由其他实例或外部 DDL 推进的 schema 变化只有当安装行读取因此失败
时才表现为 `health.probe_failed`；不区分「数据库不可达」与「安装行缺失/无效」——两者都是无快照的
`health.probe_failed`，不产出 `databaseStatus: unreachable`；不强制中断不合作的 provider，探测
预算（库默认 5 秒）与 Npgsql 连接超时的协调不在保证内，超出预算即 `health.probe_timeout`。健康
响应只含 `status`、`phase`、`migrationStatus`、`databaseStatus`、`errorCode` 五个有限字段，不含
连接串、密码、用户名或 provider 文本。调用方责任：探针把 503 视为 not ready；运行时账户对
`service_installations` 与 `reference_workspaces` 具有 SELECT 权限；部署侧决定探测频率与负载
均衡摘除策略。真库验收见
[`ReferencePostgreSqlHealthTests`](../../tests/ServiceMantle.ReferenceService.Tests/) 与无需数据库的
[`ReferencePostgreSqlHealthSnapshotSourceTests`](../../tests/ServiceMantle.ReferenceService.Tests/)。

## PostgreSQL 单事务初始化 executor

`Database/PostgreSql/` 下的 `ReferencePostgreSqlMigrationExecutor` 是 schema-only 的观察与
迁移边界；`ReferencePostgreSqlInstallationInitializationExecutor` 在其上组合出新库初始化：
当它在本次编排作用域内观察到 `Empty` 时，把本构建全部已知迁移的脚本（以
`NoTransactions` 生成，包括 `__EFMigrationsHistory` 的写入）与初始 `PendingSetup` 安装行
放进同一个 PostgreSQL 事务，只提交一次。commit 之前的任何失败、调用方取消或连接中断都
不留下表、历史或安装行；commit 之后的取消以调用方 token 报告，但不代表回滚。观察到
`PendingMigration` 的旧库只委托既有 schema executor 迁移，绝不补建安装行；没有合格观察就
调用执行是固定失败的拒绝。该组件经上文的显式 PostgreSQL 启动部署 gate 接入宿主与 DI
（[#160](https://github.com/philfanzhou/ServiceMantle/issues/160)），不签发 Setup Code，也不
保存消费方业务数据；真实迁移锁的串行化由 gate 的编排完成，每次编排使用新的作用域。真库
验收见
[`ReferencePostgreSqlInstallationInitializationTests`](../../tests/ServiceMantle.ReferenceService.Tests/)。

身份占位符不会为不可用的外部系统编造未认证成功的故事，不发出凭据、不联系网络服务、也不创建
本地管理员。本样例中不存在本地管理员实体或预配路径。

配置管理事务审计仍在
[#177](https://github.com/philfanzhou/ServiceMantle/issues/177)，首次安装事务审计在
[#175](https://github.com/philfanzhou/ServiceMantle/issues/175)，遥测在
[#158](https://github.com/philfanzhou/ServiceMantle/issues/158)，Consul 在
[#159](https://github.com/philfanzhou/ServiceMantle/issues/159)。本骨架对 TLS、部署、反向
代理、生产安全加固、API 兼容性、最终管理路由或多实例 E2E 行为不做任何保证。它的启动状态不是
健康/就绪声明。

## 日志安全接线

`ReferenceService:Logging:Enabled` 是显式布尔开关，默认为 `false`。缺失或无法解析的值会使
ServiceMantle Serilog 宿主、敏感 Header 注册表与请求日志保持未注册；该值在 `Build` 之前固定，
绝不重载。开关关闭时宿主仍然启动、服务 `GET /`、正常停止与释放。

```bash
dotnet run --project samples/ServiceMantle.ReferenceService --   --ReferenceService:Logging:Enabled true --urls http://127.0.0.1:5080
```

打开时，样例注册既有的 `ServiceMantle.Serilog` Console 管道及其强制的结构化清理，通过
`AddSensitiveHeaders` 把样例自有的 `X-Reference-Secret` 加入内置拒绝 Header 名，并在
Problem Details 之外组合 Correlation ID，使一个标识符富化整个下游 scope。它唯一的请求日志行
只携带一个固定的消息模板、一个有界的结果分类（`success`、`client_error`、`server_error`、
`other`、`cancelled`、`faulted`）、状态码、收敛到框架已知 token 集合的请求方法（其他任何值
变成 `(other)`）、匹配的路由模式，以及由 DI 拥有的 `RequestHeaderDiagnosticProjector` 产生的
Header 图。原始路径、查询、body、连接设置或异常细节都不会被记录，也没有注册第二个清理器。

Header 值遵循库契约而不是允许列表：内置拒绝 Header 与样例自有的 `X-Reference-Secret` 被
redaction 标记整体替换，而拒绝列表之外的 Header——`User-Agent`、`Referer`、`X-Forwarded-For`
以及样例从未声明的任何调用方 Header——的值按
[结构化日志安全契约](../../LOGGING_SECURITY.md)的自由文本规则投影，因此确实会进入日志行。
只有该契约识别的形状才会在那里被 redact。通过 `AddSensitiveHeaders` 添加 Header 名可以把它的
值挡在外面。调用方取消保持为取消，绝不会被吞掉。phase gate、健康 endpoint、管理路由与速率
限制在这里保持未接线；基础遥测有自己的开关，见下文，两个开关互不改变。

安全边界就是[结构化日志安全契约](../../LOGGING_SECURITY.md)中记录的那个。被拒绝的结构化字段
名、被拒绝的 Header、受支持的敏感值类型以及显式识别的自由文本形状会被 redact；未标注的不透明
秘密、从不经过清理器或 projector 的值、第三方 provider、任意框架事件与远程系统不在覆盖范围
内。启用日志不创建数据库、不运行迁移、不调用 setup、也不保存消费方的 `DbContext`。测试使用的
失败、拒绝与异常路由由测试在公开的 `Build` 接缝上映射；运行中的样例只保留骨架路由。

## 基础遥测插桩

`ReferenceService:Telemetry:Enabled` 是显式布尔开关，默认为 `false`。只有能解析为 `true` 的
值才注册插桩；缺失、为空或无法解析的值使每个 ServiceMantle 拥有的 OpenTelemetry provider
保持未注册。该值在宿主 builder 创建之前读取，在 `Build` 之前固定，绝不重载。

```bash
dotnet run --project samples/ServiceMantle.ReferenceService -- \
  --ReferenceService:Telemetry:Enabled true --urls http://127.0.0.1:5080
```

打开时，样例调用 `AddOpenTelemetryInstrumentation`，此外什么都不做。这正是公开的
`ServiceMantle.OpenTelemetry` 包已经交付的固定集合：ASP.NET Core 请求跟踪、`HttpClient`
跟踪与 .NET 运行时指标。样例不在其上添加自己的选项系统。OpenTelemetry resource 恰好是
`AddServiceMantle` 已注册的身份——服务名、服务版本与实例 ID——因此样例不贡献任何属性、任何
高基数维度，也不读取任何 Header、body、查询或连接字段。

这个开关**不**做的事：不接 OTLP exporter、不接 Prometheus endpoint、不接 `ServiceMetrics`、
不接健康 endpoint、不接任何服务或安装阶段指标，也不伪造阶段。开关打开时 `/health` 与
`/management` 仍返回 404；`/metrics` 由下文的独立开关决定。这里不创建任何远程导出目标，因此
没有注册 exporter 时收集到的信号无处可去。那些能力仍属于
[#158](https://github.com/philfanzhou/ServiceMantle/issues/158) 及拥有它们的任务。

样例现在对 `ServiceMantle.OpenTelemetry` 及其插桩包是**静态**依赖：无论开关是否打开，它们都
在构建输出中。关闭开关会阻止 ServiceMantle 拥有的 provider 与 listener 被注册；它不会从发布
输出中移除那些程序集，也不构成 .NET 进程不运行任何线程、定时器或 socket 的主张。

验收矩阵以及本接线不保证的事项完整清单，见
[`docs/testing/reference-telemetry.md`](../../docs/testing/reference-telemetry.md)。

```bash
dotnet test --project tests/ServiceMantle.ReferenceService.Tests -c Release
```

### 安装阶段指标

`ReferenceService:Telemetry:PhaseMetrics:Enabled` 是第二个显式布尔开关，默认为 `false`，只能与
PostgreSQL 启动 gate 一起打开——没有 gate 就没有权威阶段，`CreateBuilder` 会在任何注册与副作用
之前拒绝并只点名两个配置键。

打开时，样例注册宿主拥有的 `ServiceMetrics` 发布器，并把权威健康 source
`ReferencePostgreSqlHealthSnapshotSource` 包进透明的装饰器
`ReferencePhaseMetricsSnapshotSource`：phase gate 与健康 endpoint 的每次读取都顺带发布阶段指标。
成功观察发布观察到的阶段；任何非调用方取消的失败（gate 未 Ready、行缺失、数据库不可达）发布
`unknown`，因此失败后绝不残留 `completed`；调用方取消不发布、保留上次值；宿主已释放发布器时忽略
其异常。指标是**最后一次**观察：没有轮询、没有缓存、没有后台刷新，部署方需要新鲜值时自行安排
周期性健康探测（例如 `/health/ready`）。

关闭时注册与之前逐字一致，容器中没有 `ServiceMetrics`，`IServiceHealthSnapshotSource` 仍直接
解析为权威 source。与基础遥测开关互相独立：阶段指标开启会注册自己的 meter provider，不要求
`ReferenceService:Telemetry:Enabled`。

```bash
dotnet run --project samples/ServiceMantle.ReferenceService -- \
  --ReferenceService:PostgreSqlStartup:Enabled true \
  --ReferenceService:PostgreSqlStartup:ConnectionString 'Host=...;Database=...;Username=...;Password=...' \
  --ReferenceService:Management:RootKey '<32+ 字符部署密钥>' \
  --ReferenceService:Telemetry:PhaseMetrics:Enabled true --urls http://127.0.0.1:5080
```

### Prometheus 抓取端点

`ReferenceService:Telemetry:Prometheus:Enabled` 是第三个显式布尔开关，默认为 `false`，只能与
PostgreSQL 启动 gate 一起打开——抓取由 gate 注册的管理会话授权，没有 gate 时 `CreateBuilder`
在任何注册与副作用之前拒绝并只点名两个配置键。

打开时映射包默认的 `/metrics`，授权为既有的 `ServiceMantle.ManagementAdmin` 管理策略：管理员
通过 `POST /management/v1/session/login` 登录后以会话 cookie 抓取，匿名与只读操作员分别得到
401 与 403；安装未完成时 phase gate 在认证之前返回 503。关闭时端点不映射（404），容器中没有
Prometheus 服务。与基础遥测开关互相独立：基础遥测关闭时抓取成功但输出不含任何序列。

不保证：真实 Prometheus 服务器可直接使用本样例抓取（管理 cookie 是样例的授权选择，不是生产
抓取方案）；`completed` 之前的指标可抓取；指标内容与基数。部署方若要生产抓取，需自行提供适
合机器身份的认证方案并替换策略名。验收矩阵见
[`docs/testing/reference-telemetry.md`](../../docs/testing/reference-telemetry.md)。

```bash
dotnet run --project samples/ServiceMantle.ReferenceService -- \
  --ReferenceService:PostgreSqlStartup:Enabled true \
  --ReferenceService:PostgreSqlStartup:ConnectionString 'Host=...;Database=...;Username=...;Password=...' \
  --ReferenceService:Management:RootKey '<32+ 字符部署密钥>' \
  --ReferenceService:Management:Operators:0:Id ops-admin \
  --ReferenceService:Management:Operators:0:Permissions management.read,management.admin \
  --ReferenceService:Management:Operators:0:Credential '<操作员凭据>' \
  --ReferenceService:Telemetry:Enabled true \
  --ReferenceService:Telemetry:Prometheus:Enabled true --urls http://127.0.0.1:5080
```
