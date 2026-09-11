# 参考服务 PostgreSQL setup staging

参考示例拥有一个小例子，展示服务 setup 的*业务*半边在 PostgreSQL 上的形态：

- `ReferencePostgreSqlSetupContributor`——一个 `Order = 100` 的 `IServiceSetupContributor`，
  只读地验证并 staging 一行工作区。
- `ReferencePostgreSqlSetupStagingScope`——同一 context 之上的 `IServiceSetupStagingScope`。

两者都接收调用方自己的 `ReferencePostgreSqlDbContext`。两者都不保存、不开启事务、不提交、
不回滚、不释放任何东西。

## 这个切片交付什么，不交付什么

此处交付：一个 contributor 和一个 staging scope，可以与真实的 `ServiceSetupOrchestrator`
组合，针对真实的 PostgreSQL 数据库使用。

**此处不交付，且下面的任何内容都不隐含：**host 启用、Setup Code、安装状态、初始配置、审计、
HTTP、新表或迁移、任何 DI 自动接线，以及任何锁。staging 一个工作区不是一个 `Completed`
的安装。示例的 host 不会在启动期间运行此 contributor，运行它也不会使正在运行的示例变为
`Ready`。

## 每个成员做什么

| 成员 | 行为 |
| --- | --- |
| `ValidateAsync` | 观察调用方的取消。不改变任何被跟踪的实体，不运行任何 SQL。 |
| `RegisterAsync` | 恰好 staging 一个 `ReferenceWorkspace`，带一个新的 `Guid` 和示例现有的默认显示名。它不保存任何东西。 |
| `HasPendingChanges` | 运行 `DetectChanges`，并报告 tracker 是否持有 `Added`、`Modified` 或 `Deleted` 条目。 |
| `DiscardPendingChangesAsync` | 清除变更 tracker。仅此而已。 |

与真实的 orchestrator 组合时，现有协议保持不变：脏 context 在入口处被拒绝，返回
`installation.dirty_context`，其待处理工作原样保留；每个验证都在任何注册之前运行；后续
contributor 的拒绝、异常或内部取消会清除未提交的 staging 并返回一个安全码
（`setup.contributor_failed`，或 contributor 自己的拒绝码）。

## 直接调用它

调用方端到端拥有工作单元：

```csharp
await using var context = new ReferencePostgreSqlDbContext(
    new DbContextOptionsBuilder<ReferencePostgreSqlDbContext>()
        .UseNpgsql(connectionString)
        .Options);

var orchestrator = new ServiceSetupOrchestrator(
    [new ReferencePostgreSqlSetupContributor(context)],
    new ReferencePostgreSqlSetupStagingScope(context));

await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
var result = await orchestrator.OrchestrateAsync(cancellationToken);
if (!result.Succeeded)
{
    await transaction.RollbackAsync(cancellationToken);
    return result.ErrorCode;
}

await context.SaveChangesAsync(cancellationToken);
await transaction.CommitAsync(cancellationToken);
return null;
```

在那次提交之前，其他连接什么都看不到，而回滚不留任何痕迹。

## 这并不保证什么

- **无幂等性。** 两次显式注册会 staging 两行，带两个不同的 id。没有重复安装保护，没有
  “已安装”检查，也没有跨实例的单一胜出者。Setup Code、已完成的安装状态和事务序列化属于
  完整的首次安装工作，不属于这里。
- **一个 context，一次调用。** `DbContext` 不支持并发使用。调用方必须为每次调用提供一个
  专用的干净 scope，并且在无法确定 context 干净时必须丢弃整个 scope。
- **清理仅限 tracker。** 丢弃待处理变更会清除已 staging 的工作。它无法回滚调用方已经提交的
  数据库操作。
- **无外部恢复承诺。** 这里没有任何内容设定时间界限或从数据库之外的故障中恢复。
- **无机密输入。** 没有产品输入，也没有可泄漏的机密。否定性断言覆盖此适配器和核心编排自己
  的结果；它们对调用方启用的原始 EF Core 或 Npgsql 日志不作任何说明。

## 覆盖方式

`ReferencePostgreSqlSetupStagingTests` 针对真实的 PostgreSQL 服务器运行，并通过在合并后的
context 上调用 EF 自己的 `MigrateAsync` 来准备 schema，因此它既不依赖示例的迁移 executor，
也不依赖任何其他适配器。它断言：验证不发出任何语句且不触碰 tracker；注册恰好 staging 一个
`Added` 的工作区，而一个独立连接仍然什么都看不到；只有调用方自己的提交才发布该行，回滚则
一行不留；orchestrator 的拒绝、失败、取消和脏 context 路径按上表所列行为；两个独立的
context 互不影响；以及重复的显式注册会 staging 两个不同的 id。

它遵循现有的真实数据库策略：`RUN_SERVICEMANTLE_POSTGRES_TESTS=true` 且 Docker 正在运行。
当该环境被显式要求而不可用时，测试会失败而不是跳过。
