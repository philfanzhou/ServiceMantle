# 管理身份 Provider 调用：取消优先级与安全异常

`ManagementIdentityProviderInvoker.InvokeAsync` 是核心层调用 `IManagementIdentityProvider` 的唯一
安全边界。本文档说明调用方取消在哪些检查点上优先于 provider 的一切完成结果，安全取消异常长什么样，
以及哪些内容刻意留在承诺之外。

## 检查点

1. **入口。** `provider` 参数检查之后、调用 provider 之前观察取消；已经取消的调用方不会触发任何
   provider 调用。
2. **完成。** provider 调用正常落定之后（`Authenticated` / `Unauthenticated` / `Failed` / `null`）
   观察取消，再决定交付三态结果、还是把 null 归一为
   `WellKnownManagementIdentityErrorCodes.ProviderFailed`。
3. **异常。** provider 抛出的任何异常（普通异常、他人 token 的 `OperationCanceledException`、
   同步抛出或异步落定）与调用方取消同时到达时，调用方取消优先。

在任一检查点上观察到调用方取消时，调用以一个**新建**的 `OperationCanceledException` 结束：其
`CancellationToken` 是调用方自己的 token，`InnerException` 为 null，`Message` 使用固定英文文字，
不保留 provider 原始异常对象、其 message 或内部异常链中的任何材料。

| provider 的完成 | 调用方已取消 | 结果 |
| --- | --- | --- |
| Authenticated / Unauthenticated / Failed（含合法 provider code） | 是 | 安全 `OperationCanceledException`，调用方的 token |
| null | 是 | 安全 `OperationCanceledException`，调用方的 token |
| 普通异常（同步或异步落定） | 是 | 安全 `OperationCanceledException`，不保留原异常 |
| 他人 token 的 `OperationCanceledException` | 是 | 安全 `OperationCanceledException`，调用方的 token |
| 上述任意情况 | 否 | 三态结果原样交付；null、普通异常、内部取消归一为 `ProviderFailed` |

没有调用方取消时，一切不动：三态语义、`Identity` 原对象引用与 provider 自己的合法非秘密
error code 保持原样；provider 内部取消不会被冒充成调用方取消。

## 规则

- provider 每次调用至多被调用一次，并收到调用方的原始 token；invoker 不重试、不后台补偿、
  不写日志、不设 timeout。
- 两个独立调用各自持有 token：只取消其中一个不影响另一个调用的结果。
- 身份三态与 provider code 的信任边界不变：ServiceMantle 只产生
  `WellKnownManagementIdentityErrorCodes.ProviderFailed`，provider code 的非秘密形状由
  `ManagementIdentityResult.Failed` 自身校验。

## 不承诺的内容

- **检查点之后的窗口。** 完成检查点之后、返回之前到达的取消不会被捕获。检查点是边界，
  不是持续的守卫。
- **不强行中断。** 忽略其 token 的 provider 不会被强制中断，本层不设 timeout/retry/熔断，
  也不给同步校验或存储时长设上界。
- **不撤销副作用。** 取消不撤销 provider 已经产生的外部副作用（外部身份系统会话、日志等）。
- **安全范围。** 安全异常的干净性断言只覆盖 invoker 主动新建的异常；provider 自身日志或调用方
  明确标为非秘密的 code/identity 内容不在断言范围内。
- **并发。** 同一 provider 实例的并发调用没有新增承诺；仅保证独立调用的取消互不影响。

## 如何被覆盖

`ManagementIdentityCancellationTests`（核心，自有 provider doubles）驱动：

- 预取消入口：provider 零调用；`null` provider 仍先抛 `ArgumentNullException`。
- 完成检查点矩阵：Authenticated、Unauthenticated、Failed、null、普通异常（同步/异步）、他人 token
  的内部取消（含合成 inner）共 8 类完成在取消后全部替换为携带 caller token 的安全 OCE。
- 未取消对照：三态、`Identity` 同一性、合法 provider code 与 `ProviderFailed` 归一保持。
- 原始异常对象不复用：provider 抛出的 OCE 实例不出现在最终异常或其 `InnerException` 中。
- provider 至多一次调用、收到原 token、异步落定路径；两个独立并发调用只取消一个。
- 既有 `ManagementIdentityProviderTests` 覆盖三态区分与未取消的异常归一，继续通过。
