# 配置快照刷新：调用方取消优先级

一次 `ServiceSettingSnapshotLoader.RefreshAsync` 在持有刷新锁的整个过程中有固定的输出检查点。
本文档说明当调用方已经取消时，在这些检查点上会发生什么，以及哪些内容是刻意留在承诺之外的。

## 检查点

刷新的每个输出都经过以下三个边界之一：

1. **入口与等待刷新锁。** 已经取消的调用方在取得锁之前就以安全取消结束；等待锁期间取消同样如此。
   未取得锁被取消时不释放锁，已取得的锁始终在 `finally` 中释放。
2. **完成检查。** 所有普通失败结果（source 故障、物化失败、根密钥不可用、解密失败、注册表校验拒绝），
   以及无需发布的版本规则结果（stale、同版本冲突、同版本幂等成功），在返回给调用方之前观察取消。
3. **发布检查。** 需要激活的成功候选在原子 `Publish` **之前**观察取消。

## 规则

如果在上述检查点上，调用方的取消已被请求，则该刷新以一个 `OperationCanceledException` 结束，
其 `CancellationToken` 是调用方自己的 token，`InnerException` 为 null，`Message`/`ToString` 不携带
source、根密钥或 validator 注入的材料。这优先于该刷新已经计算出的所有其他结果：

| 刷新自身的结果 | 调用方已取消 | 结果 |
| --- | --- | --- |
| source 抛出普通异常 | 是 | 安全 `OperationCanceledException`，调用方的 token |
| source 抛出别人 token 的 `OperationCanceledException` | 是 | 安全 `OperationCanceledException`，调用方的 token |
| 根密钥源抛出普通异常或别人 token 的 `OperationCanceledException` | 是 | 安全 `OperationCanceledException`，调用方的 token |
| 约束拒绝或抛异常 | 是 | 安全 `OperationCanceledException`，调用方的 token |
| 组合校验器返回错误或抛异常 | 是 | 安全 `OperationCanceledException`，调用方的 token |
| 成功候选 | 是（发布检查观察到） | 安全 `OperationCanceledException`，不发布 |
| 上述任意情况 | 否 | 现有的有限错误分类，保持不变 |

没有调用方取消时，一切不动：普通 source 故障仍然是 `configuration.snapshot_load_failed`，
根密钥不可用仍然是 `configuration.snapshot_sensitive_key_unavailable`，约束与组合校验失败仍然
`configuration.snapshot_validation_failed` 系投影，别人 token 的内部 `OperationCanceledException`
按普通失败分类，不冒充调用方取消。正常新版本、同版本相同内容、同版本冲突与 stale 的版本语义不变。

取消与失败都不替换已激活的快照：首次失败或取消不激活；先激活 V1 后失败或取消，accessor 保留
同一个 V1 引用。`ServiceSettingQueryService.GetCurrentAsync` 直接转发 loader 的取消，传播同一个
调用方 token。

## 不承诺的内容

- **检查点之后的窗口。** 在发布检查与 `Publish` 之间、`Publish` 与返回之间到达的取消不会被捕获，
  已发布的快照不会被撤回。检查点是边界，不是持续的守卫。
- **不强行中断。** 忽略其 token、阻塞或在取消回调中抛异常的 source、根密钥源或同步 validator
  不会被强制中断，本层也不设置总耗时上界。
- **秘密断言的边界。** Message/`ToString`/`InnerException` 的否定性断言只覆盖本 loader 主动产生的
  异常与结果，不对第三方主动记录的内容或进程内存作任何说明。
- **所有权。** 调用方继续拥有源、根密钥源和 loader 的生命周期；共享 `DbContext` 的保存与事务
  职责不属于本 loader。

## 如何被覆盖

`ServiceSettingSnapshotLoaderTests` 驱动全部检查点：

- **完成检查**用一个参数化用例矩阵驱动：source 普通异常、source 内部取消、根密钥普通异常、根密钥
  内部取消、约束拒绝、约束抛异常、组合校验器返回错误、组合校验器抛异常，每种都在取消调用方自己的
  token 之后结束。断言携带原 token 的安全 OCE、异常材料不含注入秘密、已激活的 V1 引用保持不变。
- 未取消的对照用例断言根密钥普通异常与内部取消仍分类为 `configuration.snapshot_sensitive_key_unavailable`；
  source 的未取消对照在既有用例中保持。
- **入口与等待锁**用阻塞 source 驱动：第二个刷新在等待刷新锁期间取消，抛出携带自己 token 的安全
  OCE，第一个刷新正常完成，锁释放后的第三次刷新成功激活。
- **锁释放**另有一个用例：持锁刷新在完成检查上取消后，下一次合法刷新串行进入并成功激活新版本。
- 两个刷新串行化、读者只观察完整快照、首次失败不激活等既有用例继续成立。
- `ServiceSettingQueryServiceTests` 增加传播用例：取消优先于 source 普通失败时，
  `GetCurrentAsync` 抛出携带同一个调用方 token 的 OCE。
