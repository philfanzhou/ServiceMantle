# Consul readiness 采样：完成检查点与超时拒绝

`ConsulRegistrationLifecycle` 的采样器用 `ReadinessCallBudget` 包住每次 readiness 决策调用。本文档
说明一次采样在什么边界上裁决其结果：预算在决策调用、作用域解析或异步释放中被耗尽时，正常返回的
Ready 决策不再被采纳，以及哪些内容刻意留在承诺之外。本文档只约束采样路径；启动（`StartAsync`）的
取消优先级见 `consul-startup-cancellation.md`，停止与注销仍由生命周期总契约约束。

## 完成检查点

一次采样解析一个 DI scope，从 scoped `IServiceReadinessDecisionSource` 取一次决策，然后**等待
scope 异步释放完成**，才在完成检查点上按固定优先级裁决：

1. **lifetime 取消。** 生命周期正在停止时，采样以“不存在”结束，不记录任何采样诊断。
2. **readiness 预算到期。** `ReadinessCallBudget` 已耗尽——无论是被决策调用、scope 创建/解析，
   还是被 scope 的异步 `DisposeAsync` 耗尽——采样都记一次既有 `readiness_timeout` 并以“不存在”
   结束。该裁决优先于决策内容与异常分类。
3. **决策或异常分类。** 预算未到期时：`Ready` 采纳为“期望存在”，`NotReady` 采纳为“期望不存在”，
   null 决策与任何普通异常/内部取消记一次 `readiness_unavailable` 并以“不存在”结束。

每次采样至多记录一个终结诊断：超时与不可用不会对同一次采样重复记录，诊断只携带既有有限元数据
（分类、状态、presence、attempt、快照版本），不携带 source 内容、健康快照明细或异常文本。

## 结果矩阵

| 采样的完成 | 预算 | lifetime | 结果 |
| --- | --- | --- | --- |
| 决策调用返回 Ready（调用中或释放中耗尽预算） | 到期 | 未停止 | `readiness_timeout`，不发布 Ready |
| 决策返回 NotReady / null，或抛普通异常/内部取消 | 到期 | 未停止 | `readiness_timeout`，不发布 Ready |
| 上述任意情况 | 到期 | 停止 | 静默“不存在”，无诊断 |
| Ready | 未到期 | 未停止 | 发布 Ready（既有行为） |
| NotReady | 未到期 | 未停止 | “不存在”（既有行为） |
| null / 普通异常 / 内部取消 / 释放失败 | 未到期 | 未停止 | `readiness_unavailable`（既有行为） |

首次采样超时不注册；实例已注册后，后续采样超时按既有 NotReady 路径注销同一 registration id。
采样循环在 scope 释放完成后才返回，下一次采样不与未完成的释放重叠。

## 不承诺的内容

- **检查点之前与之后的窗口。** 完成检查点裁决的是“scope 已落定”时刻已观察到的到期；检查点之后
  到达的取消或到期不改变已交付的结果，也不撤回已发布的期望。
- **不强行中断。** 忽略 token 的 source、scope 或 DI 释放不会被强制中断，本层不设硬性墙钟上界，
  不通过抛弃不协作任务制造时间界限；生命周期仍可等待依赖落定。
- **不泄漏内容。** `readiness_timeout` 只表示预算耗尽这一事实，不推断 source 的健康内容或异常细节。
- **无新协议。** 不新增定时、缓存、心跳或热更新语义；诊断码集合与采样频率配置不变。

## 如何被覆盖

`ConsulReadinessCompletionTests`（Consul 测试工程，自有 `CompletingDecisionSource` double，复用
既有 fake clock、fixture 与 scripted client seam）：

- 决策调用内耗尽预算后返回 Ready：0 次注册、`NotReady` 状态、恰一条 `readiness_timeout`。
- 决策先返回 Ready、scope 异步释放再耗尽预算：同样拒绝，覆盖“释放路径”seam。
- scope 创建期间（包装的 scope factory 内推进时钟）与 source 解析期间（scoped 注册委托内推进时钟）
  耗尽预算后返回 Ready：同样拒绝，覆盖“创建/解析”seam。
- 有限矩阵：Ready / NotReady / null / 普通异常 / 内部取消伴随到期，全部 `readiness_timeout` 且
  诊断不含合成秘密 canary；每次采样恰一条终结诊断。
- 未到期对照：正常 Ready 注册、正常 NotReady 无诊断无操作、释放失败与普通失败仍是
  `readiness_unavailable`。
- 已注册后下一次采样超时按既有 NotReady 路径注销同一 registration id。
- 停止与预算同时到达：用例将决策调用挂起在自有 gate 上（忽略采样 token），停止使 lifetime 取消、
  推进 fake time 使预算到期后再放行调用；断言 stop 返回前 scope 已释放未被遗弃、不发布新期望、
  不注册、不记录任何采样诊断。

既有生命周期测试（正常 source、失败、停止、注册/注销不重叠）继续成立。
