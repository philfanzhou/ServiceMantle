# 参考服务 SQLite 启动完成

`ReferenceSqliteStartupCoordinator` 运行示例可选启用的 SQLite 启动部署门，而
`ReferenceSqliteStartupHostedService` 是调用它的启动步骤。本文档描述这一对组件的一个性质：
有限的启动结果何时变得可见，以及该门对调用方的取消承诺什么——和拒绝承诺什么。

这个门是一个消费方示例。除非被显式开启，否则它是关闭的；它不提供跨进程互斥；`Ready` 结果
是一个 schema 门，永远不是 `InstallationStatus.Completed`。

## 一次启动调用遵循的顺序

1. 仅根据声明对部署模式进行授权，观察目标文件，并且只有在被显式允许时才准备它。这里不改变
   其中任何一步。
2. migration 在本次调用拥有的一个 scope 内运行，持有消费方自己的 context 和 executor。
3. **释放该 scope。** context 和其中的 executor 在任何东西被发布之前就被 dispose。
4. **只有到那时**才会最后一次读取调用方的 token。
5. 只有过了该检查点，有限的结果才会在 hosted service 上发布，并写入这个门产生的那一条
   日志行。

因此，如果调用方在 scope 正在被释放期间取消了，调用已经计算出的结果不会被发布；一次失败的
释放不会留下 `Ready` 结果，也不会留下成功记录。

## 一次调用报告什么

| 情形 | 结果 |
| --- | --- |
| 工作成功，释放成功，调用方未取消 | `Ready`，发布并记录一次 |
| 工作成功，释放成功，调用方在检查点前已取消 | 携带调用方 token 的 `OperationCanceledException`；不发布、不记录任何东西 |
| 工作成功，释放失败，调用方未取消 | `MigrationFailed`，记录一次，不携带任何释放异常文本 |
| 释放因某个其他 token 抛出 `OperationCanceledException`，调用方未取消 | `MigrationFailed` |
| 释放失败**且**调用方已取消 | 携带调用方 token 的 `OperationCanceledException`——取消优先 |
| migration 未成功 | `MigrationFailed`，在 scope 被释放之后 |
| 工作本身抛出异常 | 异常向上传播，在本次调用的 scope 已被释放之后；不发布也不记录任何东西 |

这个门的那一条日志行及其结果只携带有限结果——绝不携带路径、连接字符串、编排器消息或异常
自己的文本。

## 这不保证什么

- **检查点之后的窗口。** 在结果已经在返回调用方途中时请求的取消不会被捕获。
- **终止挂起的释放。** 一个永不返回的 `DisposeAsync` 不会被强制结束。没有整体启动超时、
  没有重试，也没有资源回收 SLA。
- **撤销任何东西。** 已发布的文件和已提交的 migration 保持原样；被取消的启动不是被回滚的
  启动。
- **这个门之外的诊断。** 上面的声明只覆盖这个 coordinator 和 hosted service 自己的结果、
  日志行和异常。第三方为自己写的日志不在覆盖范围内。
- **可重入的启动协议。** 每个 hosted 启动实例都按宿主使用它的方式被使用：每个实例一次
  `StartingAsync`。

调用方仍然拥有部署的合法性、目标文件以及每一个消费方事务。

## 如何覆盖

`ReferenceSqliteStartupCompletionTests` 通过真实的 `AddReferenceSqliteStartup` 注册组合出
这个门，在一个真实文件之上，使用真实的 SQLite 准备 provider，并调用真实的
`ReferenceSqliteStartupHostedService.StartingAsync`。只有 scoped 的
`IDatabaseMigrationExecutor` 被替换为一个适配器，该适配器报告一个兼容的 schema——因此不会
运行任何 migration——其释放过程由测试驱动：

```csharp
services.AddScoped<IDatabaseMigrationExecutor>(_ => new ControlledScopedExecutor(async () =>
{
    entered.SetResult();
    await release.Task;      // the scope is still being released here
}));
```

这足以在释放过程内部放置一个屏障，并断言 hosted service 的 `Result` 和这个门的最终日志记录
都还不存在，然后让释放取消调用方自己的 source、失败，或两者都做。该适配器从不运行 migration，
因此那个文件中没有任何用例能改变它所指向的文件，其中也没有任何内容依赖示例自己的 migration
executor 那个独立的取消边界。

`ReferenceSqliteStartupTests`、`ReferenceSqliteMigrationTests` 和
`ReferenceSqliteDeploymentEndToEndTests` 仍然是这个门其他行为的所有者：它默认关闭，未授权的
模式或不可用的输入在任何副作用发生之前被拒绝，未经授权不会创建缺失的目标文件，以及真实的
子进程部署契约成立。
