# 参考服务 PostgreSQL executor 取消

`ReferencePostgreSqlMigrationExecutor` 是参考示例中由消费方持有的 PostgreSQL schema 边界。本文档
描述它的一个属性：何时观察到调用方的取消，以及 executor 对此作出承诺与拒绝承诺的内容。

这里的任何内容都不会把示例的 host 接到 PostgreSQL，而且一个被此 executor 判定为兼容的 schema
仍然不等于一个 `Completed` 或 `Ready` 的安装。有限的 schema 矩阵见
[reference-postgresql-migrations.md](reference-postgresql-migrations.md)。

## 最终化检查点

两个公开入口点都汇合到同一条规则：

1. 调用在开始之前读取调用方的 token。如果此时已经请求取消，则不会打开连接，也不会启动迁移。
2. 调用完成它自己的工作——只读观察，或那一次显式的 `MigrateAsync`。
3. 释放此调用拥有的资源。对观察而言是它自己的 `NpgsqlConnection` 和 reader；对执行而言是
   EF Core 为迁移打开的任何资源。
4. **只有在那之后**才会最后一次读取调用方的 token。如果在该检查点已请求取消，调用抛出
   `OperationCanceledException`，其 `CancellationToken` 是调用方自己的 token。

检查点优先于调用已经计算出的任何结果。在连接释放之前就已决出的有限观察不会被返回，一个正常
跑完的迁移也不会被报告为成功。`InspectAsync` 和 `ExecuteAsync` 各自汇合到自己的检查点；
两者都不借用对方的。

## 失败，以及不属于调用方的取消

| 情形 | 结果 |
| --- | --- |
| 观察失败，调用方未取消 | `MigrationObservationState.InspectionFailed` |
| 观察失败，调用方已取消 | 携带调用方 token 的 `OperationCanceledException` |
| 观察因某个其他 token 抛出 `OperationCanceledException`，调用方未取消 | `InspectionFailed` |
| 观察以任意有限状态完成，调用方已取消 | 携带调用方 token 的 `OperationCanceledException` |
| 执行失败，调用方未取消 | `ReferencePostgreSqlMigrationFailedException` |
| 执行失败，调用方已取消 | 携带调用方 token 的 `OperationCanceledException` |
| 执行因某个其他 token 抛出 `OperationCanceledException`，调用方未取消 | `ReferencePostgreSqlMigrationFailedException` |
| 执行正常完成，调用方已取消 | 携带调用方 token 的 `OperationCanceledException` |

内部取消绝不会被重新标记为调用方的取消。`ReferencePostgreSqlMigrationFailedException`
保持其固定消息，不携带内部异常，也不携带 provider 文本，因此连接机密或服务器消息无法通过它
到达诊断输出。

## 这并不保证什么

- **检查点之后的窗口。** 在结果已经传回调用方途中请求的取消不会被捕获。检查点是一个边界，
  不是持续的守卫。
- **打断不合作的 I/O。** 取消会传递给 provider 并在检查点被观察到。它不会强行中止一个忽略
  取消的驱动调用，也不会对调用返回所花的时间设定上限。
- **撤销 DDL。** 被取消的执行不是被回滚的执行。它不说明迁移有多少已经提交，也没有任何东西
  能恢复一个在迁移中途被杀死的进程。
- **任何跨调用的内容。** executor 不加锁，调用之间不持有任何东西，也无法阻止观察与执行之间
  的外部 DDL。序列化、对目标的授权、context、每一次保存和每一个事务仍由调用方拥有。
- **第三方日志。** 否定性断言覆盖此 executor 自己的结果、异常及其 `ToString()`。它们对调用方
  已启用的原始 EF Core 或 Npgsql 日志不作任何说明。

## 覆盖方式

`ReferencePostgreSqlCancellationCompletionTests` 在不依赖数据库的情况下确定性地覆盖该检查点：

- **执行**边界通过 EF Core 自己的公开可扩展性来驱动。测试在一个显式的内部 service provider
  之上构建 context，并放入它自己的 `IMigrator`，因此 `MigrateAsync` 可以在选定的时刻正常完成、
  失败或取消调用方的 source。连接字符串刻意不可达：该文件中的任何用例都不会联系服务器。
- **观察**边界通过一个受控的观察来驱动。executor 有一个构造函数重载，把只读观察作为一个
  operation 接收：

  ```csharp
  var executor = new ReferencePostgreSqlMigrationExecutor(context, async _ =>
  {
      await using var release = new CancelOnRelease(callerSource);
      return MigrationObservationState.CurrentVersionCompatible;
  });
  ```

  有限结果先被计算出来，release 在其之后运行，这正是数据库支撑的观察在返回前关闭其连接和
  reader 时的顺序。该重载只为这个边界而存在。它不改变数据库支撑的观察读取的内容，
  连接字符串构造函数仍是生产入口点，也没有为它添加任何库 SPI、`InternalsVisibleTo`
  或跨 provider 的辅助工具。

`ReferencePostgreSqlMigrationTests` 仍然是真实服务器行为的所有者：有限的 schema 矩阵、只读
检查、那一次显式迁移，以及在语句执行之前的取消。它需要 `RUN_SERVICEMANTLE_POSTGRES_TESTS=true`
和 Docker。
