# Setup 编排：失败清理完成后的调用方取消优先级

`ServiceSetupOrchestrator` 在任何失败入口都会经由汇合的失败清理 helper：以
`CancellationToken.None` 丢弃暂存变更，再做既有清洁复核，然后分类。本文档说明清理落定后的
完成检查点行为，以及哪些内容刻意留在承诺之外。

Setup Code、安装行、HTTP handler、Contributor 排序/协议、保存/提交/回滚事务不在本文档范围内。

## 规则

已经进入失败清理的本次编排，在 `DiscardPendingChangesAsync` 与既有清洁复核完成或异常落定的
终结检查点上，如果调用方 token 已被取消，则以新建的 `OperationCanceledException` 结束：

- 恰好携带调用方自己的 token；
- 固定英文消息 `Service setup was cancelled by the caller.`；
- 没有 `InnerException`，不携带清理异常或 contributor 异常的原始 message 或合成秘密；
- 不是重抛清理过程中的异常；它在普通异常归一（catch → `setup.cleanup_failed`）之外交付，
  因此不可能再次被归一成清理失败。

该检查点优先于所有失败分类：

| 清理前的分类 | 清理中/清理落定时调用方已取消 | 交付 |
| --- | --- | --- |
| `setup.validation_side_effect` | 是 | 调用方 `OperationCanceledException` |
| validation 抛异常且 scope 有脏状态（`setup.contributor_failed`） | 是 | 调用方 `OperationCanceledException` |
| registration 返回合法拒绝码 | 是 | 调用方 `OperationCanceledException` |
| registration 返回 null（`setup.contributor_failed`） | 是 | 调用方 `OperationCanceledException` |
| registration 抛普通异常（`setup.contributor_failed`） | 是 | 调用方 `OperationCanceledException` |
| registration 内部取消（`setup.contributor_failed`） | 是 | 调用方 `OperationCanceledException` |
| 清理后仍脏 / 清理抛异常（`setup.cleanup_failed`） | 是 | 调用方 `OperationCanceledException` |
| 上述任意情况 | 否 | 原分类，保持不变 |

没有调用方取消时，一切不动：

- 清理仍用 `CancellationToken.None` 且恰好一次；
- 清理成功且 scope 干净时保留原失败码（含合法拒绝码），否则 `setup.cleanup_failed` 优先；
- ValidationSideEffect/ContributorFailed 的既有优先级不变；
- 成功路径不新增任何清理；入口脏 scope 直接 `DirtyContext` 拒绝，不清除调用方既有工作；
- 预取消的调用零 Contributor 调用、零清理；
- 观察取消后不启动后续 Contributor 的注册，不通过再次 Discard 补偿，不保存或提交。

## 不承诺的内容

- **检查点之后的窗口。** 只保证失败清理落定后的完成检查点；之后才发生的取消不承诺被本次
  编排观察到。
- **不配合的同步成员。** 不中断不配合的同步 `HasPendingChanges` getter 或 Discard 实现，
  不新增 timeout。
- **scope 清洁性。** 清理失败后不承诺 scope 已干净；调用方必须丢弃不能确认干净的 scope。
- **回滚范围。** 清理仅丢弃暂存变更，不回滚已提交数据库或外部副作用；保存和事务仍归消费方。

## 如何被覆盖

`SetupCleanupCancellationTests`（专属 Contributor/scope doubles，不改共享 fixture）驱动：

- 6 种失败入口 × 4 种清理落定方式（取消后正常清理、清理后仍脏、抛普通异常、抛内部 OCE，
  取消均发生在清洁复核落定之前）共 24 例：全部交付携带原 caller token 的固定消息 OCE，
  无 inner、无秘密；清理恰一次且使用 `CancellationToken.None`；不启动后续注册、不二次 Discard。
- 同矩阵未取消对照 24 例：保留 ValidationSideEffect/ContributorFailed/合法拒绝码，以及
  `setup.cleanup_failed` 的优先级。
- 预取消零调用、入口脏 scope 不清除、成功路径零清理与稳定顺序、两个独立 scope 并发只取消
  一个而互不污染。
