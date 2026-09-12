# 参考服务 PostgreSQL 迁移

参考示例拥有两个数据库 context。`ReferenceDbContext` 是示例的 host 实际启动的 SQLite
context，其迁移是针对 SQLite 自己的存储类型编写的。`ReferencePostgreSqlDbContext` 是一个
独立的、由消费方持有的 PostgreSQL context，拥有自己的迁移历史和自己的存储类型。

它们是刻意分开的。SQLite 迁移声明的是 `TEXT` 列，因此把同一个 context 指向 `UseNpgsql`
不会产出一个 PostgreSQL schema——它产出的会是一个用 PostgreSQL 语法拼写的 SQLite schema，
或者直接失败。因此 PostgreSQL 目标拥有自己的 context、自己的迁移和自己的模型快照。

## 这个切片交付什么，不交付什么

此处交付：

- `ReferencePostgreSqlDbContext`，只把共享的 `ReferenceWorkspace` 业务实体映射到
  `public.reference_workspaces`，存储类型为 `uuid` 和 `character varying(120)`。
- 一个迁移 `20260910000000_InitialReferencePostgreSqlWorkspace`，及其模型快照。
- `ReferencePostgreSqlMigrationExecutor`，一个 `IDatabaseMigrationExecutor`，其观察是只读的，
  其执行是仅涉及 schema 的。

**此处不交付，且本文档中的任何内容都不隐含：**示例的 host 不会在 PostgreSQL 上启动。没有
注册，没有启动协调器，没有目标准备，没有授权门，也没有安装状态。这个切片不会初始化任何安装、
完成 Setup 或写入任何行。此 executor 报告为兼容的 schema 只是一个 schema 观察，仅此而已——
它绝不是某个服务安装为 `Completed` 或 `Ready` 的证据。host 启动、目标准备授权、安装状态
初始化和失败恢复仍是未完成的工作。

由于没有 host 接线，下面的一切都是通过 executor 的公开 API 直接调用它来演练的。

## 所有权

调用方拥有目标数据库、连接字符串、context、每一次保存和每一个事务。调用方还拥有跨进程的
迁移序列化：此 executor 不注册任何锁，不获取任何锁，调用之间也不持有任何东西。

`InspectAsync` 从检查连接字符串打开它自己的连接，读取，然后关闭。`ExecuteAsync` 在被给予的
context 上恰好运行一次显式的 `MigrateAsync`。它不调用 `EnsureCreated`，不调用 `SaveChanges`，
除其自身迁移声明的表之外不触碰任何表。

预期的调用顺序是：调用方确保目标数据库存在，获取其部署拓扑所要求的任何锁，进行检查，然后才
决定是否执行。先检查不是可选的——`ExecuteAsync` 无条件应用迁移，不问任何问题。

## 有限的 schema 矩阵

观察覆盖目标数据库的 `public` schema，此外什么都不覆盖。它是一组有限的可读事实，不是一个
通用的 schema 兼容性算法。

两个排除项塑造了关系普查：

- **系统 schema** 不是应用状态，予以跳过：`pg_catalog`、`information_schema`，以及
  `pg_toast*` 和 `pg_temp*` 家族。
- **EF 历史表** `__EFMigrationsHistory` 在统计应用关系时被跳过，因为它是 executor 自己的
  证据，而不是应用拥有的表。

| 观察 | 结果 |
| --- | --- |
| `public` 存在且可读，没有应用关系，且没有历史表或历史表为空 | `Empty` |
| 历史记录持有此构建所知 id 的一个严格、无缺口的前缀 | `PendingMigration` |
| 历史记录恰好持有所知集合，`reference_workspaces` 是一个普通表，且 `Id` 和 `DisplayName` 可读 | `CurrentVersionCompatible` |
| 历史记录持有此构建不知道的 id | `VersionTooNew` |
| 历史记录存在，但已应用的 id 不是所知集合的前缀（有缺口） | `InspectionFailed` |
| 历史记录完全无法读取——缺少权限、意外的形状、服务器不可达、数据库缺失、`public` schema 缺失 | `InspectionFailed` |
| 应用关系存在，但没有历史表 | `InspectionFailed` |
| `reference_workspaces` 或其某个预期列缺失 | `InspectionFailed` |
| `public` 中的某个关系不是普通表——视图、物化视图、分区表或外部表 | `InspectionFailed` |
| 应用关系存在于 `public` 之外的任何非系统 schema 中 | `InspectionFailed` |

`VersionTooNew` 是根据那条未知的历史记录本身判定的。它刻意**不**通过询问
`GetPendingMigrations()` 是否为空来判定，一个空的工作区表也绝不会被当作采纳未知 schema 的
许可。

executor 用于比较的迁移集合可以通过三参数构造函数显式提供。它的存在使得上面前缀和缺口的
各行无需为示例添加第二个没有业务需求的生产迁移即可被覆盖。

## 不保证什么

- 上面的矩阵就是保证。任意物理 schema 的兼容性不是：类型 facet、约束、索引、触发器、存储
  过程，以及被管理员手工编辑得看起来正确的历史表，都在其之外。
- 检查在调用之间不持有锁，无法防止它读取的时刻与调用方执行的时刻之间的外部 DDL。
- 取消无法回滚服务器已经提交的 DDL。不承诺从任意外部 PostgreSQL 故障中恢复，也不给任何
  命令设定绝对的时间界限。
- `ExecuteAsync` 的失败以 `ReferencePostgreSqlMigrationFailedException` 的形式出现，携带
  固定消息、无内部异常、无 provider 文本。该否定性断言覆盖此 executor 自己产生的诊断、
  结果和异常，包括它们的 `ToString()`。它不覆盖调用方已启用的原始 EF 或 Npgsql 日志、
  调用方自己的配置或进程内存。
- 调用方取消以携带调用方自己 token 的 `OperationCanceledException` 的形式出现。普通失败
  绝不会被报告为取消。

## 运行测试

`ReferencePostgreSqlMigrationTests` 是一个真实数据库测试类。它带有共享的
`[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]` 分类，并使用共享的必需环境策略，
因此它是可选加入的，而且一旦选择加入，就不能通过跳过而静默通过：如果设置了
`RUN_SERVICEMANTLE_POSTGRES_TESTS=true` 而 Docker 或容器不可用，测试会失败。

```bash
dotnet build ServiceMantle.slnx -c Release
RUN_SERVICEMANTLE_POSTGRES_TESTS=true dotnet test \
  --project tests/ServiceMantle.ReferenceService.Tests -c Release --no-build --no-restore
```

`SERVICEMANTLE_POSTGRES_IMAGE` 覆盖容器镜像，默认为 `postgres:15-alpine`。
每个用例在容器上创建自己的数据库，因此没有用例会继承另一个用例的 schema。

`tests/ServiceMantle.ReferenceService.Tests` 的其余部分不需要容器，不受影响：示例的默认
行为仍然是完全没有任何数据库副作用，SQLite 部署验收仍然在 SQLite 上启动示例的真实入口点。

fixture 中的容器凭据是合成的，只在容器的生命周期内存在。测试断言它们不会出现在 executor
自己的诊断中，并且绝不打印完整的连接字符串、原始 SQL 错误或容器自己的日志。

`eng/packages.json` 把此测试项目登记为真实数据库项目，带有
`RUN_SERVICEMANTLE_POSTGRES_TESTS=true`，因此发布流水线会启用它而不是静默跳过它。

## 相关

- [`MIGRATION_ORCHESTRATION.md`](../../MIGRATION_ORCHESTRATION.md)——此 executor 接入的
  编排契约。
- [`reference-sqlite-deployment.md`](reference-sqlite-deployment.md)——示例的 host 实际启动的
  SQLite 部署。
