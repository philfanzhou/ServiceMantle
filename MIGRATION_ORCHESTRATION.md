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
