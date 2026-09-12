# 迁移编排：租约释放完成后的调用方取消优先级

`DatabaseMigrationOrchestrator` 的真实租约编排（原 `OrchestrateMigrationAsync` 重载，以及显式
`DatabaseDeploymentMode.MultiInstance` 委托到它的入口）在持有租约期间有多个阶段检查点。本文档
说明最后一个检查点——租约释放完成边界——的行为，以及哪些内容刻意留在承诺之外。

SingleInstance 算法、锁 provider 的获取/释放实现和租约丢失判定模型不在本文档范围内。

## 规则

已经获取租约的本次编排调用，在 `IDatabaseMigrationLock.DisposeAsync` 落定（正常完成、抛普通
异常或抛内部 `OperationCanceledException` 均算落定）、即将交付主结果时，如果调用方 token 已被
取消，则以新建的 `OperationCanceledException` 结束：

- 携带调用方自己的 token；
- 固定英文消息 `Migration orchestration was cancelled by the caller.`；
- 没有 `InnerException`，不携带释放异常或阶段异常的原始 message 或合成秘密。

该检查点优先于所有主结果：

| 释放前的主结果 | 释放中/释放落定时调用方已取消 | 交付 |
| --- | --- | --- |
| 跳过执行成功 | 是 | 调用方 `OperationCanceledException` |
| 执行后成功 | 是 | 调用方 `OperationCanceledException` |
| `migration.version_too_new` | 是 | 调用方 `OperationCanceledException` |
| 初检失败 `migration.inspection_failed` | 是 | 调用方 `OperationCanceledException` |
| 执行失败 `migration.execution_failed` | 是 | 调用方 `OperationCanceledException` |
| 最终状态失败 `migration.final_state_invalid` | 是 | 调用方 `OperationCanceledException` |
| 已检测租约丢失 `migration.lock_failed` | 是 | 调用方 `OperationCanceledException` |
| 上述任意情况 | 否 | 原主结果，保持不变 |

没有调用方取消时，一切不动：

- 释放失败（普通异常或内部取消）继续不替换主结果，也不被重抛；
- 显式释放不引入新的租约丢失检查，正常释放不被当作丢租约；
- 释放恰好一次，不重试，不重复执行或检查；
- 既有阶段已经 caller-cancelled 的调用在释放落定后仍交付调用方取消；
- 预取消的调用不获取租约；获取失败且没有租约时不释放。

## 不承诺的内容

- **检查点之后的窗口。** 只保证释放操作落定后的最终检查点；检查点之后才发生的取消不承诺被
  本次编排观察到。
- **不配合的释放。** 不能中断忽略取消、阻塞在 `DisposeAsync` 里的 provider；不新增释放时限或
  整体时限。
- **回滚。** 取消不回滚已执行或已提交的 DDL；事务与失败恢复仍归消费方。
- **远端释放成功。** 释放失败不证明锁已在远端成功释放；provider 的既有释放责任不变。

## 如何被覆盖

`MigrationReleaseCancellationTests`（专属 lease/provider/executor doubles，不依赖真实数据库、
Sleep 或计时竞争）驱动：

- 7 种主结果 × 3 种释放落定方式的完整矩阵：取消后均不交付普通结果，新建 OCE 的 token、固定
  消息、无 inner、无秘密泄漏，释放恰一次，执行/检查次数与主结果一致；
- 同矩阵的未取消对照：保留原成功/错误码与 `ExecutorWasCalled`，释放失败不替换主结果；
- 既有阶段已取消、预取消、获取失败无租约、双实例隔离（只取消一个）以及显式 MultiInstance
  委托路径的同一不变量。
