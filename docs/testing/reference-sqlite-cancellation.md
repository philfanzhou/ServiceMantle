# 参考服务 SQLite executor 取消

`ReferenceSqliteMigrationExecutor` 是参考示例中由消费方持有的、面向其 SQLite 工作区数据库的
migration 边界。本文档只描述它的一个性质：调用方的取消在何时被观察到，以及 executor 对此承诺
什么——和拒绝承诺什么。

这里的内容不改变启动门、部署声明或调用方仍然持有的文件准备工作，并且这个 executor 判定为兼容
的数据库仍然不是一次完成的安装。

## 最终检查点

两个公开入口点都汇聚到同一条规则：

1. 调用在开始前先读取调用方的 token。如果此刻已经请求了取消，则不会打开连接，也不会启动
   migration——不读任何东西，也不写任何东西。
2. 调用执行自己的工作——只读观察，或在消费方 context 上执行的那一次 `MigrateAsync`。
3. 释放本次调用自己拥有的资源。对观察而言，那就是它自己的只读 `SqliteConnection` 和 reader。
4. **只有到那时**才会最后一次读取调用方的 token。如果到该检查点时已经请求了取消，调用会抛出
   一个 `OperationCanceledException`，其 `CancellationToken` 是调用方自己的 token。

检查点的优先级高于调用已经计算出的任何结果。在连接释放之前就已经决定的有限观察结果不会被
返回，一个正常运行到完成的 migration 也不会被报告为成功。

## 失败，以及不属于调用方的取消

| 情形 | 结果 |
| --- | --- |
| 观察失败，调用方未取消 | `MigrationObservationState.InspectionFailed` |
| 观察失败，调用方已取消 | 携带调用方 token 的 `OperationCanceledException` |
| 观察因某个其他 token 抛出 `OperationCanceledException`，调用方未取消 | `InspectionFailed` |
| 观察以任意有限状态完成，调用方已取消 | 携带调用方 token 的 `OperationCanceledException` |
| 执行失败 | migration 自己的异常，原样不变 |
| 执行因某个其他 token 抛出 `OperationCanceledException` | 该异常原样不变——不会被重新标记为调用方的取消 |
| 执行正常完成，调用方已取消 | 携带调用方 token 的 `OperationCanceledException` |

这个 executor 刻意**不**对 migration 失败进行分类。它没有自己固定的失败类型，这次改动也没有
新增：执行失败时仍然原样呈现 migration 产生的异常。本文档不作“每个 provider 异常都会被脱敏”
这样的声明。

## 这不保证什么

- **检查点之后的窗口。** 在结果已经在返回调用方途中时请求的取消不会被捕获。
- **中断不合作的 I/O。** SQLite 的工作大体是同步的。取消会被向下传递并在检查点被观察到；
  它不会中止一个已经进入 provider 内部的调用，也不对调用返回所需的时间设置任何上限。
- **撤销 DDL。** 被取消的执行不是被回滚的执行，这里的内容也不能恢复一个在 migration 中途
  被杀死的进程。
- **任何跨进程的事情。** executor 不加锁，调用之间也不持有任何东西。调用方仍然拥有文件准备、
  部署声明、context 以及每一个事务。

## 如何覆盖

`ReferenceSqliteCancellationCompletionTests` 以确定性方式覆盖该检查点：

- **执行**边界通过 EF Core 自己的公开扩展点驱动。测试在一个显式的内部 service provider 之上
  构建 context，并把测试自己的 `IMigrator` 放进去，使 `MigrateAsync` 可以在选定的时刻正常
  完成、失败或取消调用方的 source。context 仍然使用示例自己的 `ReadWrite` 连接字符串，因此
  没有任何用例能创建该文件。
- **观察**边界通过一个受控观察驱动。executor 有一个构造函数重载，把只读观察作为一个操作
  接收：

  ```csharp
  var executor = new ReferenceSqliteMigrationExecutor(context, async _ =>
  {
      await using var release = new CancelOnRelease(callerSource);
      return MigrationObservationState.CurrentVersionCompatible;
  });
  ```

  有限结果先被决定，释放动作在其后运行，这正是基于文件的观察在返回前关闭其 reader 和连接时
  所具有的次序。该重载只为这个边界而存在。它不改变基于文件的观察所读取的内容，基于 options
  的构造函数仍是生产入口点，并且没有为它添加任何库内部机制、任何 `InternalsVisibleTo`，
  也没有任何跨 provider 的 SPI。

同一文件还保留了一个走真实文件路径的用例——目标文件缺失时被拒绝且不创建任何东西——因此这个
测试接缝不会悄悄变成唯一被测试的东西。`ReferenceSqliteMigrationTests` 和
`ReferenceSqliteStartupTests` 仍然是有限历史矩阵、启动门和部署契约的所有者。
