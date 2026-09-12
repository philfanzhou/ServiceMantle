# 配置批量更新：调用方取消优先级与完成检查点

一次 `ServiceSettingUpdateService.UpdateAsync` 在调用方拥有的事务内验证、保护并暂存一个完整的
设置批次。本文档说明当调用方已经取消时，更新在哪些边界上以取消结束，普通结果何时不再交付，
以及哪些内容刻意留在承诺之外。

## 检查点

一次更新经过以下取消检查点：

1. **入口。** 已经取消的调用方在任何依赖被调用之前收到取消；参数无效的检查仍先于取消检查。
2. **Load 完成。** `LoadAsync` 正常返回后观察取消，再比较 service id 与版本。
3. **根密钥与校验完成。** 根密钥源、组合校验器与逐键保护循环的每次落定之后都观察取消。
4. **Apply 完成。** `ApplyAsync` 正常返回的**所有**结果（`Applied / ValidationFailed /
   VersionConflict / VersionExhausted / ProtectionFailed / StorageFailed / TransactionRequired /
   ContextNotClean`）在返回给调用方之前观察取消。这是本契约的收敛点：不按状态枚举逐个打补丁，
   任何在检查点上已取消的更新都不交付普通结果。

在任一检查点上观察到调用方取消时，更新以一个 `OperationCanceledException` 结束，其
`CancellationToken` 是调用方自己的 token，`InnerException` 为 null，`Message`/`ToString` 不携带
依赖注入的异常材料或秘密。该安全取消优先于更新已经计算出的所有其他结果与错误分类，包括
`Apply` 抛出的普通异常和别人 token 的内部 `OperationCanceledException`。

没有调用方取消时，一切不动：Load 的普通异常与内部取消仍分类为 `StorageFailed`，根密钥失败仍为
`ProtectionFailed`，校验失败仍返回仅含注册 key 与非秘密 code 的 `ValidationFailed`，版本比较仍按
原顺序产生 `StorageFailed / VersionConflict / VersionExhausted`。

## 保存但尚未提交

`Applied` 本来就只表示批次已经在调用方的事务内暂存，提交由调用方完成。完成检查点把已取消的
`Applied` 替换为安全取消，**不会**回滚 Apply 已经暂存的更改，也不推断数据库未保存或未提交。
收到安全取消的调用方必须像处理普通失败一样处置自己的事务（回滚并丢弃外层工作单元）；EF 适配器
在 Apply 内部失败或取消时恢复自身 savepoint 的既有行为不变。

## 不承诺的内容

- **检查点之后的窗口。** 完成检查点与 `return` 之间到达的取消不会被捕获。检查点是边界，
  不是持续的守卫。
- **不强行中断。** 忽略其 token 的 transaction、根密钥源或同步 validator 不会被强制中断，
  本层不设置总耗时上界。
- **不撤销副作用。** 取消不回滚自定义 transaction 已执行的工作，不触发快照发布，也不重试；
  每次 `UpdateAsync` 至多一次 `LoadAsync` 与一次 `ApplyAsync`。
- **并发使用。** 两个独立服务实例的取消互不影响；对同一 scoped transaction 的并发使用没有承诺。
- **秘密断言的边界。** Message/`ToString`/`InnerException` 的否定性断言只覆盖本服务主动产生的
  异常，不对第三方主动记录的内容作任何说明。

## 如何被覆盖

`ServiceSettingUpdateCancellationTests`（核心，自有 transaction/根密钥/validator doubles）驱动：

- 预取消入口：不调用 Load/Apply；`null` command 仍先抛 `ArgumentNullException`。
- **Apply 完成检查点**：fake Apply 在返回前取消调用方，8 个状态的正常结果全部被替换为携带原
  token 的安全 OCE；普通异常与别人 token 的内部取消在取消后不保留原始 message/inner。
- Load、根密钥、组合校验器的正常/异常完成取消矩阵；未取消对照保持 `StorageFailed /
  ProtectionFailed / ValidationFailed` 与版本分类。
- 两个独立实例只取消其中一个时，另一个正常返回 `Applied`，各自至多一次 Load/Apply。

EF `ServiceSettingUpdateTests`（SQLite 真实事务）继续覆盖成功路径、savepoint 回滚与调用方
commit/rollback 所有权；HTTP `SettingUpdateExecutor` 的取消转发契约不变。
