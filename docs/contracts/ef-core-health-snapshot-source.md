# EF Core 数据库健康快照源

`ServiceMantle.Persistence.Relational` 包提供的 `EfCoreHealthSnapshotSource<TDbContext>` 是
`IServiceHealthSnapshotSource` 的通用实现：读取 `AddStartupDatabaseGate` 登记的进程内
`StartupDatabaseReceipt`，回执成功后对当次作用域的 `DbContext` 做只读探测。它替代消费方各自
手写的「读启动回执 + 探测数据库」快照源（#606）。

## 注册

注册入口 `AddServiceMantleEfCoreHealthSnapshotSource` 与仓库其他 `AddServiceMantle*` 入口一致，
住在 `Microsoft.Extensions.DependencyInjection`（ADR 0007 C 类）：组合根无需为注册本身引入
能力 namespace 的 `using`；探测模式枚举等其余类型仍在 `ServiceMantle.Persistence.Relational`。

```csharp
services.AddServiceMantleEfCoreHealthSnapshotSource<CatalogDbContext>(
    serviceId,
    new PostgreSqlDatabaseProbeFailureClassifier(),   // 来自 ServiceMantle.Database.PostgreSql
    EfCoreHealthSnapshotProbeMode.MappedSchema);      // 或 ConnectionOnly
```

- 快照源注册为 scoped：它解析当次作用域的 `TDbContext` 与单例 `StartupDatabaseReceipt`
  （后者由 `AddStartupDatabaseGate` 自动注册，无需额外动作）。
- 错误码前缀默认取 `ServiceId` 的归一化值，或经 `errorCodePrefix` 显式传入；前缀必须满足
  快照契约的 safe code 字符集，且保证完整错误码不超过 128 字符。
- 可替换的失败分类接口 `IServiceDatabaseProbeFailureClassifier`（核心包 `ServiceMantle.Health`）由调用方
  提供；PostgreSQL 实现是 `ServiceMantle.Database.PostgreSql` 的
  `PostgreSqlDatabaseProbeFailureClassifier`。核心包与持久化包不引用 Npgsql。

## 探测模式

| 模式 | 行为 |
| --- | --- |
| `ConnectionOnly` | 打开并关闭当前作用域的连接。只证明可达性，不发现表/列损坏。 |
| `MappedSchema`（默认） | 打开连接后，对 EF 编译模型映射为表的每个实体类型执行一条零行 `SELECT <映射列> FROM <表> WHERE 1 = 0`，再关闭连接。标识符由 provider 自己的 SQL 生成助手转义；表、列名只来自编译模型，绝不来自请求输入。 |

## 固定映射

| 观察到的事实 | 快照 |
| --- | --- |
| 回执 `NotStarted`/`Running` | `(PendingSetup, 回执状态, Unreachable, {前缀}.startup_incomplete)`，数据库访问次数为 0 |
| 回执 `Failed` | `(PendingSetup, Failed, Unreachable, {前缀}.startup_failed)`，数据库访问次数为 0 |
| 失败分类接口判定连接类失败 | `(Completed, Succeeded, Unreachable, {前缀}.database_unreachable)` |
| 失败分类接口判定 schema 不可读 | `(Completed, Failed, Reachable, {前缀}.schema_unavailable)` |
| 无法分类的异常 | 原样向上传播，由健康端点按既有规则输出安全错误码（fail closed） |
| 调用方取消 / 探测预算超时 | `OperationCanceledException` 携带收到的 token 原样传播，绝不报告为 `Unreachable` |

PostgreSQL 分类实现按 SQLSTATE 区分：类 08（连接异常）、53（资源不足）、57（运维干预）、
`3D000`（目录名无效）、`55P03`（锁不可用）与无 SQLSTATE 的传输级异常 → 连接类失败；类 42
（对象不存在/权限规则违反）→ schema 不可读；其余（含类 28 认证失败）→ 传播。

## 保证

- 回执未到 `Succeeded` 时不访问数据库。
- 探测只读：不执行 DDL、不追踪实体、不读写业务数据、不触发安装或迁移；快照源不写回执。
- 探测每次请求重新采样，不缓存。
- 快照源不记录日志；快照与其文本表示只含有限枚举和安全错误码。

## 不保证

- `MappedSchema` 只证明映射的表与列当前可读，不证明约束、索引、触发器或数据正确。
- 不约束数据库挂起时的探测耗时；由健康端点的探测预算通过 token 约束。
- 不支持多个 `DbContext` 的组合判定（消费方自行组合多个实例或自行实现）。
- 不提供 MySQL/SQL Server 等其他 provider 的 SQLSTATE 分类；未注册失败分类接口时无法分类的失败
  一律传播。
- 快照是单进程的一次采样，不是跨实例一致性保证。

## 如何被覆盖

- 单元测试 `EfCoreHealthSnapshotSourceTests`（SQLite + ADO 连接包装器）：回执三态零访问、两种
  探测模式、固定映射、取消传播（含驱动包装的取消）、前缀校验、DI 注册。
- 集成测试 `EfCoreHealthSnapshotSourcePostgreSqlTests`（Testcontainers）：正常、库停机、表被
  删除、列被删除、权限被收回五种快照结局，以及认证失败传播与调用方取消。
