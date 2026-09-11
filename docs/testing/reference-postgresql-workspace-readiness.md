# 参考服务 PostgreSQL 工作区就绪

`ReferencePostgreSqlWorkspaceReadinessContributor` 是一个由消费方持有的例子，展示 PostgreSQL
上的*业务*就绪否决。给定一个 base-ready 的快照，它只在至少一行能在 `public.reference_workspaces`
中被观察到时才接受。

## 这个切片交付什么，不交付什么

此处交付：一个 `Order = 100` 的 `IServiceReadinessContributor`，基于
`IDbContextFactory<ReferencePostgreSqlDbContext>`。

**此处不交付：**真实的安装阶段快照来源、live/ready/compatibility 健康 endpoint、host 激活和
注册。正在运行的示例没有变化——它旧的占位符仍然回答 `reference.health_not_integrated`，
这里没有任何内容使示例变为 `Ready`。测试中给此 contributor 的快照是它的输入矩阵，不是任何
来源会产出它们的证据。

## 决策

| 输入 | 结果 |
| --- | --- |
| 快照不是 base-ready（base 矩阵拒绝的任何 phase、migration 或 database 状态） | `reference.phase_not_ready`——**不创建 context，也不运行任何 SQL** |
| base-ready，且至少观察到一行工作区 | 就绪 |
| base-ready，且表为空 | `reference.workspace_missing` |
| base-ready，但创建 context、读取或释放失败——表缺失、读取被拒绝、服务器不可达 | `reference.workspace_probe_failed` |
| base-ready，且出现一个调用方并未请求的 `OperationCanceledException` | `reference.workspace_probe_failed` |
| 调用方在最终检查点之前已取消 | 携带调用方自己 token 的 `OperationCanceledException` |

读取是 `AsNoTracking().AnyAsync()`：仅存在性。没有工作区 id、显示名、行数或 provider 文本
会到达结果。

## 所有权与生命周期

每次评估都从 factory 创建它自己的 context，并在评估结束前释放它。该 contributor 在评估之间
不缓存任何东西，也绝不捕获调用方 scope 的 `DbContext`，因此之后把它注册为 singleton 是安全的。

```csharp
var factory = /* an IDbContextFactory<ReferencePostgreSqlDbContext> the caller owns */;
var contributor = new ReferencePostgreSqlWorkspaceReadinessContributor(factory);

var result = await contributor.EvaluateAsync(snapshot, cancellationToken);
if (!result.IsReady)
{
    return result.ErrorCode; // one of the fixed codes above
}
```

调用方的 token 在其拥有的 context 被释放后再读取一次，因此到那个时点为止请求的取消优先于
本次评估已经做出的观察。

## 这并不保证什么

- **就绪是一个时刻，不是一个状态。** 它不证明快照是权威的，不证明某个安装是 `Completed`，
  不证明任何行的字段有效，也不证明 schema、配置、审计、容量或签名密钥处于正常状态。没有任何
  东西阻止该行在被观察到之后立即被删除，任何传播延迟都没有界限。
- **没有自己的超时。** 此 contributor 不构建任何超时。combiner 的共享总预算拥有
  `health.contributor_timeout` 的分类。
- **无强制打断。** 取消会传递给 provider 并在最终检查点被观察到。不合作的 provider 不会被
  中止，在该检查点之后到达的取消不被覆盖。
- **每次评估一个 context。** `DbContext` 不支持并发操作；这就是每次评估拥有自己 context 的
  原因。
- **调用方启用的日志。** 否定性断言覆盖此 contributor 自己的结果及其 `ToString()`。它们对
  调用方打开的原始 EF Core 或 Npgsql 日志不作任何说明。

## 覆盖方式

`ReferencePostgreSqlWorkspaceReadinessTests` 针对真实的 PostgreSQL 服务器运行，并用 EF 自己的
`MigrateAsync` 准备 schema，因此它既不依赖示例的迁移 executor，也不依赖其 setup staging
适配器。它断言：每一行非 base-ready 的输入都在不创建 context、不发出任何语句的情况下被应答；
空表被拒绝而一行或多行被接受；表缺失、未授予任何权限的角色和服务器不可达都产生同一个固定的
拒绝；历史、schema 和行都未改变且不发出任何写语句；在 context 创建时、查询期间、释放时和
正常完成时的取消都保留调用方的 token，而预先取消的评估什么都不创建；以及每次评估都创建并
释放它自己的 context。

最后三个用例把该 contributor 与真实的 `ServiceReadinessContributorCombiner` 组合：它的成功和
拒绝被原样报告，一个受控的未完成探测被共享预算分类为 `health.contributor_timeout`，调用方的
取消以调用方自己的 token 报告。fixture 释放并 await 它阻塞的探测；测试自己的等待不是生产 SLA。

它遵循现有的真实数据库策略：`RUN_SERVICEMANTLE_POSTGRES_TESTS=true` 且 Docker 正在运行。
当该环境被显式要求而不可用时，测试会失败而不是跳过。
