# 数据库迁移编排

本文档总结 ServiceMantle 中与 provider 无关的数据库迁移编排实现，以及可选的多实例迁移锁支持。

## 核心架构

### 与 provider 无关的核心（`ServiceMantle.Migration`）

核心包在不引入任何数据库驱动依赖的情况下定义契约与编排逻辑。

**关键类型：**

1. **`IDatabaseMigrationExecutor`** - 消费服务的扩展点
   - `InspectAsync()` - 观察当前数据库状态（Empty、CurrentVersionCompatible、PendingMigration、VersionTooNew、InspectionFailed）。它是只读的；读取什么、拒绝什么由消费服务自己决定
   - `ExecuteAsync()` - 运行消费服务自己的迁移工作流。编排器**每次编排至多调用它一次**，且只有在持权检查返回 `Empty` 或 `PendingMigration` 时才调用。`CurrentVersionCompatible` 的目标会完全跳过它，因此对已经是最新版本的数据库，编排成功而未调用 executor 是正常结果

2. **`IDatabaseMigrationLock`** - 已获取的锁租约
   - 扩展 `IAsyncDisposable` 以获得 RAII 语义
   - 在其生命周期内持有锁
   - 当 provider 检测到权限丢失时，暴露一个永久的 `LeaseLost` 取消信号

3. **`IDatabaseMigrationLockProvider`** - 锁能力的 provider SPI
   - `ProviderId` 属性用于匹配 bootstrap provider ID
   - `AcquireAsync()` - 获取锁，支持超时与取消

4. **`DatabaseMigrationLockProviderRegistry`** - 不区分大小写的查找
   - 在启动时累积锁 provider
   - 拒绝重复注册
   - 接收共享的 `DatabaseProviderIdResolver` 快照，使注册键与查找键以完全相同的方式规范化，
     bootstrap provider 别名能找到以规范 id 注册的锁 provider。解析别名绝不意味着具备锁能力：
     未注册的能力仍返回 `migration.lock_not_supported`。

5. **`DatabaseMigrationOrchestrator`** - 编排引擎
   - 实现下文描述的持权流程
   - 产生带安全错误码的 `MigrationExecutionResult`

6. **`MigrationExecutionResult`** - 安全的不可变结果
   - `Succeeded` - 迁移是否成功
   - `ErrorCode` - 众所周知的安全错误码（失败时）
   - `ErrorMessage` - 不含秘密的安全消息
   - `ExecutorWasCalled` - executor 是否被调用

7. **`WellKnownMigrationErrorCodes`** - 标准错误码
   - `migration.lock_not_supported`
   - `migration.lock_timeout`
   - `migration.lock_failed`
   - `migration.inspection_failed`
   - `migration.version_too_new`
   - `migration.execution_failed`
   - `migration.final_state_invalid`

8. **`DatabaseMigrationLockException`** - 安全的锁失败异常
   - `ErrorCode` 属性用于结构化错误处理
   - 消息中不含连接字符串或秘密

### PostgreSQL 启动门显式选项预设

`PostgreSqlStartupDatabaseGateOptions.Create(database, allowTargetCreation)` 组合现有公开选项：
MultiInstance、enableTargetPreparation=true、显式创建许可、30秒lockWaitBudget/preparationTimeout，
以及既有 `PostgreSqlMaintenanceConnection.DeriveConnectionString`。目标对象原样保留，旧选项默认不变。
非法provider/连接串用固定白名单code的ArgumentException且无parser inner；不读配置/环境、注册DI或做I/O。
调用方仍独立注册capability/preparation/lock/executor并判断创建许可；本预设不证明可达/权限或锁可用。

### PostgreSQL Provider（`ServiceMantle.Database.PostgreSql.Migration`）

**`PostgreSqlMigrationLockProvider`** 实现 `IDatabaseMigrationLockProvider`：

1. **锁键推导**（`ServiceIdToLockKeyDeriver`）
   - 使用 `"ServiceMantle.Migration." + serviceId.Value` 的 SHA-256 哈希
   - 读取前 8 个字节作为有符号 64 位整数（大端序）
   - 跨进程、跨机器、跨重启确定且稳定
   - 不依赖 `.GetHashCode()`

2. **有界轮询的锁获取**：
   - 打开一条带超时的专用 Npgsql 连接
   - 使用 `pg_try_advisory_lock()` 进行非阻塞获取
   - 以 100ms 间隔轮询，直到获得锁或超过截止时间
   - 同时尊重调用方的超时与取消 token
   - 取消优先于超时

3. **锁租约**（`PostgreSqlMigrationLock`）：
   - 在锁的生命周期内保持一条打开的连接
   - 每 250ms 以一秒命令超时探测该专用连接
   - 在保守的五秒运行进程上界内发出检测到的会话丢失信号
   - 在 `DisposeAsync()` 时：
     - 若连接仍打开，尝试显式 `pg_advisory_unlock()`
     - 关闭连接（会话锁由 PostgreSQL 释放）
     - 抑制任何错误，避免掩盖主异常

### SQL Server 显式部署声明与身份

`services.AddServiceMantleSqlServerDeploymentCapability()` 仅幂等注册独立的 SingleAndMultiInstance
声明，不隐含 bootstrap/preparation/lock。单实例只接受显式 InitialCatalog 和单 TCP endpoint，
DataSource主机trim/小写，缺省端口1433；连接被强制为规范tcp endpoint，非池化、不enlist。
命名实例、np/lpc等非TCP、LocalDB、AttachDBFilename、UserInstance、FailoverPartner、空库或
不明确endpoint纯解析失败关闭，零 I/O；核心返回LockNotSupported且executor=0。
合法输入只在一个owned连接上执行固定只读 `SELECT DB_NAME()`，以服务器返回的canonical库名
Ordinal编码，既合并CI服务器同一库拼写，也保留CS服务器不同库，不能用库自身collation猜测
实例目录名字规则。provider/domain、host、port、库名按UTF-8长度分隔摘要；凭据不参与身份。
正常/异常、command/connection完整释放后均检查caller取消。失败用固定无inner InvalidOperationException，
核心LockFailed；预算超时LockTimeout，caller取消原token优先。无DDL/transaction/cache/提交工作单元。
missing目标先prepare；MultiInstance仍单独真实锁且不调用身份方法。DNS/代理/server别名不解析，
不会查询注册表或SQL Browser；单实例不承诺进程外互斥。CI/CS真实验收分别使用不同实例的server
catalog collation，非仅改变database数据collation。

### Oracle 显式部署声明与单实例拒绝边界

`services.AddServiceMantleOracleDeploymentCapability()` 幂等注册独立声明，支持等级
SingleAndMultiInstance仅用于部署验证，不代表内置single-instance身份已经可用。
内置 `OracleDatabaseDeploymentCapabilityProvider.GetCanonicalTargetIdentityAsync` 校验参数与caller
取消后确定返回空身份，不解析UserID/密码，不做I/O；核心SingleInstance编排返回
`migration.lock_not_supported`且executor=0。不能用DataSource混合不同schema，也不会把UserID输出。
选择MultiInstance并另外注册真实Oracle锁；确需SingleInstance时，以
`services.AddSingleton<IDatabaseDeploymentCapabilityProvider, YourOracleCapability>()` 注册自己
符合核心canonical schema契约的声明，替代内置类型，不要同时注册同provider两次。
本项没有资源/写入/事务或回滚；旧Oracle bootstrap/preparation/lock行为保持。

### Oracle Provider（`ServiceMantle.Database.Oracle.Migration`）

**`OracleMigrationLockProvider`** 实现 `IDatabaseMigrationLockProvider`：

1. 它以 `ServiceMantle.Migration.` 加上规范化 `ServiceId` 的完整小写 SHA-256 摘要来派生锁标识，
   避开由调用方分配、容易冲突的数字 lock-ID 范围。
2. 它打开一条禁用连接池与环境事务加入的专用目标用户会话，校验受支持的运行时拓扑，用
   `DBMS_LOCK.ALLOCATE_UNIQUE_AUTONOMOUS` 分配句柄，并在剩余的有界超时内以
   `release_on_commit => FALSE` 请求 `X_MODE`。
3. 缺少直接的 `EXECUTE ON SYS.DBMS_LOCK` 授权映射为 `migration.lock_not_supported`；
   `REQUEST` 返回码 1 映射为 `migration.lock_timeout`；返回码 2 到 5 及其他运行失败映射为
   `migration.lock_failed`；调用方取消仍然是 `OperationCanceledException`。
4. 获得的租约每 250 毫秒以一秒命令超时探测其专用会话，并使用与 provider 无关的 `LeaseLost`
   信号。释放时显式调用 `DBMS_LOCK.RELEASE`，然后关闭这条非池化会话。

## 编排流程

**持权流程为：**

1. **参数校验** - 立即检查取消
2. **锁解析** - 查找并获取 provider 特定的锁
   - 未注册锁 provider 时失败关闭（安全边界）
   - 超时或取消时失败关闭
3. **持权检查** - 在锁内重新检查状态，同时监视 `LeaseLost`
4. **决策树**：
   - 若 `CurrentVersionCompatible` → 跳过执行，返回成功
   - 若 `VersionTooNew` → 失败关闭，不执行
   - 若 `InspectionFailed` → 失败关闭，不执行
   - 若 `Empty` 或 `PendingMigration` → 调用 executor 一次，且只在此分支调用
5. **持权复查** - 在同一受监视租约下检查执行后的状态
   - 只有最终状态为 `CurrentVersionCompatible` 才算成功
6. **锁释放** - 始终在 finally 块中，错误被抑制

因此 `ExecuteAsync` 每次编排至多被调用一次；当目标已兼容或观察失败关闭时完全不调用。
它在任何全局意义上都不是「恰好一次」：对一个仍需要迁移的目标重复编排会再次调用它。

每次 executor 调用都收到一个链接了调用方取消与 `LeaseLost` 的 token。编排器在每个阶段前后都先
检查调用方取消，因此调用方取消与租约丢失的竞争仍表现为 `OperationCanceledException`。租约丢失
映射为 `migration.lock_failed`，记录执行是否已开始，并阻止尚未开始的下一阶段。executor 必须及时
观察传入的 token；丢失检测无法回滚 executor 已提交的副作用。

### 两个 `OrchestrateMigrationAsync` 重载

`DatabaseMigrationOrchestrator` 暴露两个重载，二者都不会退化为对方。

- **`(serviceId, bootstrap, lockAcquireTimeout, cancellationToken)`** 始终要求真实的分布式租约。
  缺少锁 provider 不是继续执行的理由：它以 `migration.lock_not_supported` 失败关闭。
  该重载绝不查阅部署声明。
- **`(serviceId, bootstrap, deploymentMode, lockAcquireTimeout, cancellationToken)`** 先用声明的
  能力校验消费方提供的 `DatabaseDeploymentMode`。`MultiInstance` 执行的正是上述流程，包含真实
  租约。`SingleInstance` 则解析 provider 的规范目标标识，并**在本进程内**串行化同一
  provider/目标的调用；它不构造任何 `IDatabaseMigrationLock`。`Unspecified`、未定义的模式、
  没有声明能力的 provider，或只声明 `SingleInstance` 能力却要求 `MultiInstance`，都以
  `migration.lock_not_supported` 失败关闭。该重载要求使用接收
  `DatabaseDeploymentCapabilityRegistry` 的三参数构造函数；与两参数构造函数一起使用时同样
  失败关闭。

锁 provider 缺失绝不会自动回退到 `SingleInstance`。模式是消费方的决定，不能从已注册的
provider 或连接字符串推断——见 `README.md` 中的
[Explicit database deployment mode](README.md#explicit-database-deployment-mode)。
进程内串行化不是跨进程锁，也不能证明部署拓扑：两个都配置为 `SingleInstance` 的进程既不会被
检测到，也不会被协调。

## 多实例行为

当两个实例尝试对同一数据库执行迁移时：

1. **实例 A** 先获得锁
2. **实例 B** 在锁获取期间等待（带超时轮询）
3. **实例 A** 检查，看到 `PendingMigration`，调用 executor，复查，成功
4. **实例 A** 在 finally 块中释放锁
5. **实例 B** 最终获得锁
6. **实例 B** 检查，看到 `CurrentVersionCompatible`（因为实例 A 的工作）
7. **实例 B** 跳过执行并返回成功
8. **实例 B** 释放锁

两个实例都报告成功，但只有实例 A 执行了迁移。没有重复执行，也没有静默失败。

## 安全边界

### 错误码

所有迁移失败都产生安全的、众所周知的错误码，它们：
- 不暴露连接字符串、密码或内部细节
- 可以安全地记录和展示
- 可供消费服务做结构化错误处理

### 异常消息

`DatabaseMigrationLockException` 与 `MigrationExecutionResult` 的消息：
- 绝不包含连接字符串或认证细节
- 只按 `ErrorCode` 分类错误
- provider 异常会被捕获并以安全分类重新包装

### 锁秘密

锁键：
- 由 ServiceId 确定性派生
- 绝不记录或暴露
- 同一 ServiceId 的所有调用中保持一致
- 不同 ServiceId 之间互不相同（没有跨服务竞争）

## 测试与验证状态

单元与内存并发测试在常规解决方案套件中运行。真实 PostgreSQL Testcontainers 套件需要 Docker；
它可以在本地启用，并由 GitHub Actions 在每个 pull request 和发布前执行（见
`.github/workflows/ci.yml`）。当前的通过/失败/跳过数量请运行
`dotnet test --solution ServiceMantle.slnx` 获取，而不要依赖此处记录的数字，因为数量会随测试
增加而漂移。

### 单元与内存测试（已在本地验证）

**`ServiceMantle.Tests.Migration`：**
- `DatabaseMigrationOrchestratorTests` - 核心编排逻辑，覆盖：
  - 当前版本跳过、空库/待迁移执行、版本过新失败关闭
  - 初始检查失败、执行失败、最终状态校验失败
  - 开始前取消与执行中取消（两者都使租约恰好释放一次）
  - 锁超时、锁不支持与空租约的失败关闭路径
  - 每条成功与失败路径的租约释放计数（通过 `FakeMigrationLockProvider.LeaseDisposeCount`）
  - 共享内存状态的双实例场景（只有一个实例执行）
  - 初始检查、执行与最终检查期间的租约丢失
  - 调用方取消与租约丢失竞争时的取消优先级
- `DatabaseMigrationLockProviderRegistryTests` - 注册表查找、大小写不敏感、重复/空值拒绝
- `ProviderIdCanonicalizationTests` - 跨持久化、provider 分发、目标准备与锁查找的别名到规范 id 解析

**`ServiceMantle.Database.PostgreSql.Tests.Migration`：**
- `ServiceIdToLockKeyDeriverTests` - 锁键推导确定性、按 ServiceId 区分、匹配固定 SHA-256 向量、拒绝空输入

**`ServiceMantle.Database.Oracle.Tests.Migration`：**
- `OracleMigrationLockProviderTests` - 完整摘要固定向量、目标会话隔离、超时与调用方取消优先级、
  每个 `REQUEST` 返回码映射、缺少直接权限、安全失败、显式释放、清理与租约丢失信号

### 真实 PostgreSQL 测试（Testcontainers，需要 Docker，在 GitHub Actions CI 中运行）

**`PostgreSqlMigrationLockConcurrencyTests`** 通过环境变量启用：

```bash
RUN_SERVICEMANTLE_POSTGRES_TESTS=true dotnet test --project tests/ServiceMantle.Database.PostgreSql.Tests/ServiceMantle.Database.PostgreSql.Tests.csproj
```

可选的镜像覆盖：
```bash
SERVICEMANTLE_POSTGRES_IMAGE=postgres:16 RUN_SERVICEMANTLE_POSTGRES_TESTS=true dotnet test --solution ServiceMantle.slnx
```

**针对真实 PostgreSQL 容器的 advisory lock 测试：**
- 相同 ServiceId 使用相同锁键；不同 ServiceId 使用不同锁键
- 第二个实例在获取时阻塞，只有在第一个释放后才继续
- 不同 ServiceId 之间不竞争：`Lock_DifferentServiceIds_DontCompete` 保持 service-a 的租约打开，
  并以一个短的有界超时获取 service-b 的租约——如果两个 ServiceId 被错误地映射到同一个 advisory
  lock 键，这次获取会超时并确定性地使测试失败
- 锁获取尊重超时，并以 `LockTimeout` 安全失败
- 轮询期间取消会抛出（`OperationCanceledException` 或 `TaskCanceledException`）
- 锁释放后允许重新获取
- 异常消息中没有秘密（密码、连接字符串）
- 执行期间对持有会话确定性地 `pg_terminate_backend`，证明编排器在五秒检测上界内返回
  `migration.lock_failed` 且不开始最终检查

**针对真实 PostgreSQL 容器的端到端编排测试：**
- `OrchestratorDoubleInstance_OnlyOneExecutes_ViaAdvisoryLock` 让两个编排器实例针对同一 ServiceId
  和真实的 `test_migration_state` 表并发运行。一个共享门（`TaskCompletionSource`，
  `RunContinuationsAsynchronously`）把胜出的 executor 保持在 `ExecuteAsync` 内部——此时 advisory
  lock 仍被持有，由一次必须超时的有界探测获取来验证——直到第二个编排器自己的获取尝试已经开始。
  两个 executor 共享同一个门，因此如果 advisory lock 未能提供互斥，二者都会到达 `ExecuteAsync`，
  并在门放行后并发竞争递增 `execution_count`。测试断言两个编排器都成功、恰好一个报告
  `ExecutorWasCalled`、`execution_count` 恰好为 1、最终状态为 `current`——使锁失效时断言确定性
  失败，而不是靠时序巧合通过。

**测试基础设施：**
- Testcontainers PostgreSQL（镜像可通过 `SERVICEMANTLE_POSTGRES_IMAGE` 配置，默认
  `postgres:15-alpine`），自动生命周期管理
- 真实测试数据库，每次编排测试创建并删除一张迁移状态表
- 真实的 `PostgreSqlMigrationLockProvider`，使用 PostgreSQL advisory lock（这些测试中没有
  fake/内存锁）
- 真实的 `DatabaseMigrationOrchestrator` 编排两个实例

### 真实 Oracle 测试（固定 FREEPDB1，需要共享 Oracle 环境）

`OracleMigrationLockRealDatabaseTests` 使用在 `eng/packages.json` 中登记的硬失败环境。它证明
同服务互斥、不同服务独立、释放/重获取与非池化连接清理、有界超时、调用方取消、直接的包权限
拒绝、获取前终止、初始检查/执行/最终检查期间的确定性终止，以及双编排器持锁复查且恰好一次真实
状态更新。CI 与 ReleaseTool 要求该环境，在变量缺失、跳过、发现零个测试、容器或连接失败时失败，
并使用 ADR 固定的 Oracle Database Free 镜像。

## SQLite WAL 恢复的显式选择

`SqliteTargetRecoveryOptions` 默认关闭；调用方可构造启用恢复的
`SqliteDatabaseTargetPreparationProvider` 实例，并通过既有 preparation/deployment 注册表使用它。
核心启动门 options 没有 provider-specific 开关，默认 Bootstrap 验证仍只读。恢复只针对稳定可信的
普通 existing 目标和安全普通 WAL/SHM：journal、目录、链接、hardlink、大小写别名、无目标、权限
不足或无法确认的 metadata 都不进入恢复 I/O。

单次调用最多一次 `PRAGMA wal_checkpoint(TRUNCATE)`，使用 ReadWrite（不创建）、Private、非池化连接；
读 busy 结果并完整释放资源后重新检查目标与 sidecar，再做一次 immutable 只读 schema 观察，避免
WAL 模式的普通只读连接重新生成 sidecar。库不自行删除、移动、替换文件，也不写应用数据；SQLite
合法 replay/checkpoint/close 的修改不承诺字节不变。忙锁或剩余 sidecar 失败关闭，内部预算到期映射
Timeout，SQLite busy 等待以整秒计；调用方取消保留原 token，清理期间取消也不能返回成功。

immutable 短暂观察只用于成功 checkpoint、全部关闭且 sidecar 已消失之后，不接纳调用方 URI/VFS；
它关闭变更检测与锁，因此外部替换、随后其他进程写入、网络文件系统和进程终止仍是非保证边界。
恢复不支持多实例 SQLite、不修损坏、不回滚已经持久化的 checkpoint；关闭后续恢复不能撤销旧写入。
真实崩溃子进程、busy writer、非法路径零恢复 I/O、一次尝试和取消/清理测试同时进入 Linux 与 Windows CI。

## 局限与后续工作

### 当前范围（已实现）

- 五种数据库产品的迁移锁 provider，各自的键推导、获取、租约探测与释放语义都在 `README.md`
  中记录：
  [PostgreSQL advisory lock](README.md#postgresql-advisory-lock)、
  [Oracle `DBMS_LOCK`](README.md#oracle-dbms_lock)、
  [MySQL named lock](README.md#mysql-named-lock)、
  [MariaDB named lock](README.md#mariadb-named-lock) 与
  [SQL Server application lock](README.md#sql-server-application-lock)
- 多实例安全编排
- 显式部署模式校验与进程内 `SingleInstance` 串行化
- 确定性锁键推导
- 超时与取消支持
- 结构化安全错误处理
- 完整的单元与并发测试
- 所有编排阶段中的租约丢失检测

### 范围之外（未实现）

- SQLite 的迁移锁 provider。SQLite 在本仓库中**没有跨进程迁移锁**。它只通过显式的
  `SingleInstance` 部署模式参与，该模式在一个进程内串行化迁移，不构成多实例支持的主张；
  对 SQLite 要求 `MultiInstance` 会以 `migration.lock_not_supported` 失败关闭
- 数据库创建或目标准备（见 `README.md` 中独立的「Database target preparation」一节，它是独立于
  本迁移编排工作加入的）
- 配置表或审计表
- Setup code 或管理端功能
- EF Core 自动迁移执行
- Break-glass/紧急解锁流程
- fencing token，或对租约丢失前 executor 已提交副作用的自动回滚

已交付的锁支持是按产品、按已记录边界而言的。它不主张每个 provider 提供等价的拓扑支持，
也不主张其中任何一个对给定部署已达到生产可用。

PostgreSQL 的五秒上界假设进程正常运行、定时器正常调度、Npgsql 命令超时机制正常工作。进程挂起、
严重的调度器饥饿，以及无法交付配置超时的运行时或网络栈，都是明确的不保证项。

### 未来的 provider 支持

需要更多 provider 时：
1. 在 provider 包中实现 `IDatabaseMigrationLockProvider`
2. 在 `DatabaseMigrationLockProviderRegistry` 中注册 provider 实例
3. provider 必须支持超时与取消语义
4. 使用与 PostgreSQL 模式一致的确定性锁键推导

SQLite 保持显式的单实例路径，而不是静默的 no-op 锁：消费方声明 `SingleInstance` 并自己承担该
部署假设。不得添加一个假装持有租约的 no-op `IDatabaseMigrationLockProvider`。

## 集成示例

这是一个**组合**示例。它展示消费服务如何把自己的 executor 交给编排器；它刻意不展示如何编写
那个 executor。

判断一个未知数据库是否可以安全接管是消费服务自己的问题，无法给出通用答案。特别地，
「`GetPendingMigrations()` 为空」不意味着目标兼容——一个持有本构建从未听说过的迁移 id 的数据库
同样报告没有待执行迁移——「业务表为空」也不是接管别人创建的 schema 的许可。executor 必须依据
它真正能读到的证据做决定，并拒绝它无法分类的东西。

`IDatabaseMigrationExecutor` 记录在 `README.md` 的
[Database migration orchestration](README.md#database-migration-orchestration) 中：
`InspectAsync` 是只读的，返回五个有限的 `MigrationObservationState` 值之一，
`ExecuteAsync` 运行消费服务自己的工作流，二者都收到一个链接了调用方取消与 `LeaseLost` 的
token，且必须及时观察它。取消无法回滚 executor 已经提交的副作用。

关于对服务真正拥有的 schema 做有限、保守观察的完整示例，见参考样例中消费方自有的 SQLite
executor：
`samples/ServiceMantle.ReferenceService/Database/Sqlite/ReferenceSqliteMigrationExecutor.cs`，
以及[它的验收说明](docs/testing/reference-sqlite-deployment.md)。它的规则特定于该样例的
schema；它们是示例，不是可以盲目照搬的库算法。

```csharp
// 1. The consuming service supplies its own IDatabaseMigrationExecutor implementation.
//    It owns its schema, its history, its finite observation rules, and its transactions.
IDatabaseMigrationExecutor executor = myServiceMigrationExecutor;

// 2. Register the lock provider for the database product in use and build the orchestrator.
var lockProviders = new DatabaseMigrationLockProviderRegistry(
    [new PostgreSqlMigrationLockProvider()],
    bootstrapProviders.ProviderIdResolver);

var orchestrator = new DatabaseMigrationOrchestrator(executor, lockProviders);

// 3. Orchestrate. This overload always requires a real distributed lease; a missing lock provider
//    fails closed rather than continuing without one.
var result = await orchestrator.OrchestrateMigrationAsync(
    serviceId,
    bootstrapConfiguration.Database,
    lockAcquireTimeout: TimeSpan.FromSeconds(30),
    cancellationToken: cts.Token);

if (!result.Succeeded)
{
    logger.LogError(
        "Database migration failed: {ErrorCode}: {ErrorMessage}",
        result.ErrorCode,
        result.ErrorMessage);
    return;
}

// A successful orchestration against an already-compatible target reports ExecutorWasCalled = false.
logger.LogInformation(
    "Database migration completed. Executor was called: {ExecutorWasCalled}",
    result.ExecutorWasCalled);
```

要让消费方改为声明 `SingleInstance`，请使用
[Explicit database deployment mode](README.md#explicit-database-deployment-mode) 中描述的
部署感知构造函数与重载。

## 证据构件用法（遗留库接管）

接管判断本身仍归消费方 executor（见上文「集成示例」）。库提供的是四件证据与原语构件，
按 [ADR 0008](docs/decisions/0008-schema-evidence-components.md) 拆分交付，语义模型的权威
位置在 [docs/contracts/schema-evidence-models.md](docs/contracts/schema-evidence-models.md)：

| 构件 | 包 | 职责 |
| --- | --- | --- |
| `PostgreSqlSchemaEvidenceReader` | `ServiceMantle.Database.PostgreSql` | 在调用方连接上只读地读出已应用迁移 id 与 `SchemaSnapshot`，并区分「目标数据库不存在」（仅 SQLState `3D000`）与「读取失败」两个事实 |
| `EfCoreExpectedSchemaDerivation` | `ServiceMantle.Persistence.Relational` | 把最终化的 EF 关系模型推导成同构的 `ExpectedSchema`（确定性；identity 只映射 SQL 标准策略） |
| `SchemaEvidenceComparer` | `ServiceMantle` 核心包 | 纯函数比对，输出全部结构化 `SchemaDifference`，不做任何分类 |
| `EfCoreMigrationBaselineWriter` | `ServiceMantle.Persistence.Relational` | 在调用方连接上以独立事务幂等写入基线迁移 id（建表经 provider 的 `IHistoryRepository`，参数化 `INSERT … WHERE NOT EXISTS`） |

典型的消费方 executor 检查顺序——证据在先，stamp 在后，全程分类决策留在消费方：

```csharp
// 1. Evidence: read the actual database (read-only, on the caller's connection, under the
//    orchestrator lease). The two failure facts stay separate; classifying "missing target is
//    an empty database" is the consumer's rule, not the reader's.
var read = await new PostgreSqlSchemaEvidenceReader().ReadAsync(connection, cancellationToken);
if (read.State != SchemaEvidenceReadState.Succeeded)
{
    return read.State == SchemaEvidenceReadState.TargetDatabaseMissing
        ? InspectAsync(TargetDatabaseState.Empty)   // 示例：消费方自己的分类
        : InspectAsync(TargetDatabaseState.InspectionFailed);
}

// 2. Expected: derive from the finalized model. Schema identifiers must align with the reader's
//    output — for PostgreSQL that means configuring the model's default schema (usually
//    "public"), because an unconfigured model derives null.
var expected = EfCoreExpectedSchemaDerivation.Derive(context.Model);

// 3. Compare: every structured difference, nothing decided here. A missing column carries its
//    expected nullability and stored-default flag as the consumer's backfill material.
var differences = SchemaEvidenceComparer.Compare(read.Snapshot, expected);

// 4. The consumer applies its own finite rules (known-migration prefix, allowed differences,
//    backfill policy) and only then stamps — the writer validates nothing about the id.
if (takeoverAllowed)
{
    var inserted = await new EfCoreMigrationBaselineWriter(context).WriteBaselineAsync(
        dedicatedConnection, "20260101000000_InitialCreate", "10.0.11", cancellationToken);
}
```

边界与调用方责任（全部经测试固定的细节见各包 XML 注释与契约文档）：读取不开启自己的
事务、不保证事务性时间点图像；类型串按 provider 方言精确比较，不做归一化；写入不验证
stamp 的 id 与实际结构一致，也不回滚自身事务之外的副作用；表 schema 标识符与期望侧的
对齐、外层连接与事务时序归调用方。

## 启动期数据库门

消费服务启动期普遍需要把「数据库目标准备 → 迁移编排 → 记录启动结果 → 失败即停止启动」
串联起来。`AddStartupDatabaseGate` 把这条流程收敛为一个显式注册：门按固定顺序执行部署
校验（仅凭已捕获的声明，先于任何 I/O）、可选的目标准备、
`DatabaseMigrationOrchestrator.OrchestrateMigrationAsync`，然后记录进程内的
`StartupDatabaseReceipt`。任一阶段失败都使宿主启动失败，失败前回执先记录 `Failed` 与
白名单错误码；宿主在门成功之前不开始监听请求。取消以 `OperationCanceledException`
结束且不记录成功。

约束与语义（详见 `README.md` 的 Startup database gate 一节）：

- 目标准备是显式开关：未声明时完全不调用 `IDatabaseTargetPreparationProvider`；已存在的
  目标只观察、不修改；「目标缺失时允许创建」缺省为不允许，且必须显式声明。准备成功后
  必须重新观察到目标可连接，才进入迁移编排（新增错误码
  `database_target_preparation.creation_not_allowed` 与
  `database_target_preparation.not_connectable_after_preparation`）。
- PostgreSQL 维护连接由 `PostgreSqlMaintenanceConnection.DeriveConnectionString` 派生：同一
  凭据、只把数据库名换成 `postgres`，消费方不再自行拼接。文件型目标无需维护连接。
- 门同时以可直接调用的形式提供（同一实现，不经宿主生命周期触发），供在宿主构建之前、
  或在消费方自有外层初始化锁内串联其他步骤的消费方使用；两种入口的顺序、失败与错误码
  语义完全相同。
- 门不读取 `IConfiguration`；目标配置、部署模式与创建许可由消费方读取后显式传入。
- 门不执行 EF Core 迁移、不判断遗留库能否接管——这两者仍归消费方 executor。

## 分阶段调用启动门

`StartupDatabaseGate.PrepareAsync(options, token)` 共享完整 `RunAsync` 的部署校验与可选目标准备阶段，
不创建迁移 scope、不解析 executor、不读取或修改任何 receipt。结果 `StartupDatabasePreparationResult`
仅含 `Succeeded`、安全 `ErrorCode` 与 `Skipped`；准备关闭仍校验部署，Skipped 成功不能当作目标已创建
或已验证可连接。准备开启时，已有可连接目标 Observe1/Prepare0；允许创建的缺失目标 Observe2/Prepare1。

调用方可先单独准备，在成功后另建 `enableTargetPreparation=false` 的既有 options 调用完整 Run，
避免重复观察/创建；显式关闭时也必须将 allowTargetCreation 保持 false。库不缓存或推断已准备状态，
重复/并发 standalone 调用仍依具体 provider 现有并发语义，不能承诺分阶段互斥。只有 Run 更新一次性
receipt；单独准备在 receipt 任意状态下都不触碰它。

两种入口的 provider 缺失、创建拒绝、服务器 maintenance 缺失、timeout、不可达、异常与重观察失败
错误码及顺序相同。调用方取消在入口与 provider 正常、异常、清理返回后检查，保留原 token 且无驱动
inner；provider 内部 OCE 在 caller 未取消时仍是有限失败。步骤间外部操作不受保护，已提交的目标创建
不因后续失败/取消回滚。迁移 scope 的释放契约沿用现有行为，本项没有扩写迁移编排。

## 变更文件

### 核心包

**新增文件：**
- `src/ServiceMantle/Migration/IDatabaseMigrationExecutor.cs` - 扩展点
- `src/ServiceMantle/Migration/IDatabaseMigrationLock.cs` - 锁租约接口
- `src/ServiceMantle/Migration/IDatabaseMigrationLockProvider.cs` - 锁 provider SPI
- `src/ServiceMantle/Migration/DatabaseMigrationLockProviderRegistry.cs` - provider 注册表
- `src/ServiceMantle/Migration/DatabaseMigrationOrchestrator.cs` - 编排引擎
- `src/ServiceMantle/Migration/MigrationExecutionResult.cs` - 安全结果模型
- `src/ServiceMantle/Migration/DatabaseMigrationLockException.cs` - 安全异常
- `src/ServiceMantle/Migration/WellKnownMigrationErrorCodes.cs` - 错误码常量

### PostgreSQL Provider

**新增文件：**
- `src/ServiceMantle.Database.PostgreSql/Migration/PostgreSqlMigrationLockProvider.cs`
- `src/ServiceMantle.Database.PostgreSql/Migration/PostgreSqlMigrationLock.cs`
- `src/ServiceMantle.Database.PostgreSql/Migration/ServiceIdToLockKeyDeriver.cs`

**修改文件：**
- `src/ServiceMantle.Database.PostgreSql/ServiceMantle.Database.PostgreSql.csproj` - 更新描述与标签

### 核心测试

**新增文件：**
- `tests/ServiceMantle.Tests/Migration/DatabaseMigrationOrchestratorTests.cs`
- `tests/ServiceMantle.Tests/Migration/DatabaseMigrationLockProviderRegistryTests.cs`
- `tests/ServiceMantle.Tests/Migration/FakeMigrationExecutor.cs` - 测试替身
- `tests/ServiceMantle.Tests/Migration/FakeMigrationLockProvider.cs` - 测试替身

### PostgreSQL 测试

**新增文件：**
- `tests/ServiceMantle.Database.PostgreSql.Tests/Migration/ServiceIdToLockKeyDeriverTests.cs`
- `tests/ServiceMantle.Database.PostgreSql.Tests/Migration/PostgreSqlMigrationLockConcurrencyTests.cs`

**修改文件：**
- `tests/ServiceMantle.Database.PostgreSql.Tests/ServiceMantle.Database.PostgreSql.Tests.csproj` - 加入 Testcontainers

### 配置与文档

**修改文件：**
- `Directory.Packages.props` - 加入 Testcontainers 包
- `README.md` - 新增迁移编排章节
- `MIGRATION_ORCHESTRATION.md` - 本文档

## MySql 显式部署声明与身份

`services.AddServiceMantleMySqlDeploymentCapability()` 仅幂等注册独立的部署声明；
`MySqlDatabaseDeploymentCapabilityProvider` 声明 SingleAndMultiInstance，不隐含其他能力。
单实例纯解析只接受单 TCP server/port 与显式数据库；非法、多个host或非TCP返回空身份且零 I/O，
由核心映射 LockNotSupported（executor=0）。合法输入仅开一个自己拥有的非池化/non-enlisted连接，
只读查询 `SELECT @@lower_case_table_names, DATABASE(), LOWER(DATABASE())`；规则0保留server名字，
1/2使用server lower名字，未知规则/空名字失败关闭，不凭CLR或未知collation猜测。
UTF-8长度分隔的provider/domain、trim小写主机、端口与server规范库名构成SHA-256身份，凭据不进入身份。
打开/读取/完整释放后的正常和异常完成均观察caller取消；失败统一安全无inner InvalidOperationException，
核心返回LockFailed，acquisition预算超时为LockTimeout，caller取消原token优先。没有DDL、写入、
transaction或cache，不保存caller工作单元。缺失目标先prepare；MultiInstance另行真实锁且身份调用0。
DNS/代理/server别名不解析，单实例不承诺进程外互斥；数据库大小写等价由真实metadata决定。

## MariaDb 显式部署声明与身份

`services.AddServiceMantleMariaDbDeploymentCapability()` 仅幂等注册独立的部署声明；
`MariaDbDatabaseDeploymentCapabilityProvider` 声明 SingleAndMultiInstance，不隐含其他能力。
单实例纯解析只接受单 TCP server/port 与显式数据库；非法、多个host或非TCP返回空身份且零 I/O，
由核心映射 LockNotSupported（executor=0）。合法输入仅开一个自己拥有的非池化/non-enlisted连接，
只读查询 `SELECT @@lower_case_table_names, DATABASE(), LOWER(DATABASE())`；规则0保留server名字，
1/2使用server lower名字，未知规则/空名字失败关闭，不凭CLR或未知collation猜测。
UTF-8长度分隔的provider/domain、trim小写主机、端口与server规范库名构成SHA-256身份，凭据不进入身份。
打开/读取/完整释放后的正常和异常完成均观察caller取消；失败统一安全无inner InvalidOperationException，
核心返回LockFailed，acquisition预算超时为LockTimeout，caller取消原token优先。没有DDL、写入、
transaction或cache，不保存caller工作单元。缺失目标先prepare；MultiInstance另行真实锁且身份调用0。
DNS/代理/server别名不解析，单实例不承诺进程外互斥；数据库大小写等价由真实metadata决定。

## PostgreSQL 显式部署声明

`services.AddServiceMantlePostgreSqlDeploymentCapability()` 仅幂等注册部署 capability，
不隐含 bootstrap、preparation 或 lock。`PostgreSqlDatabaseDeploymentCapabilityProvider`
声明 `SingleAndMultiInstance`；单实例身份不打开连接，只从显式单 TCP Host/Port/Database
按 UTF-8 长度分隔字段构造 SHA-256 摘要，domain 区分 provider。主机 trim/小写、端口默认5432；
数据库名保留 Ordinal，用户名、密码、超时、池和属性拼写/顺序不参与身份。多主机、socket、
空主机/库与非法连接串返回空身份，核心确定返回 `migration.lock_not_supported`，执行器不调用。
入口与返回前检查 caller token，parser异常不向外暴露。DNS/代理/数据库名大小写别名不解析，
调用方负责对齐规范名字；MultiInstance 仍单独注册真实锁，并不会调用该身份方法。

## 直接调用启动门的注册入口

核心包的 `services.AddServiceMantleStartupDatabaseGateServices()` 注册门、receipt 和四个共享注册表，
不要求 options、身份或配置，不注册宿主服务、不解析执行器，也不进行 I/O。Web 的
`ServiceMantleBuilder` 提供同名转发入口；它和 `AddStartupDatabaseGate(options)` 任意顺序调用均幂等，
只有后者注册宿主入口，重复 options 仍保留首次值。配置在容器构建后确定的调用方可解析门与 receipt，
再显式调用 `RunAsync(options, receipt, serviceId, token)` 并处理结果。

门不直接释放执行器。外部构造后用 `AddSingleton(instance)` 注册的执行器仍由调用方释放；交给 DI
创建并拥有的 scoped 执行器仍随门创建的 scope 释放。注册成功不代表运行成功，缺失执行器、阶段失败
和取消沿用既有语义。直接入口不会自动运行，调用方负责触发时机。
