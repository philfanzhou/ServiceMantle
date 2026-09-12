# Consul 启动：禁用与客户端创建失败的取消优先级

`ConsulRegistrationLifecycle.StartAsync` 在主机启动时解析一次客户端会话。本文档说明调用方取消
在启动的哪些边界上优先于 provider 的每类完成结果，被取消启动中已产生的会话如何处置，以及哪些
内容刻意留在承诺之外。本文档只约束启动路径；readiness 采样见 `consul-readiness-completion.md`，
停止与注销仍由生命周期总契约约束。

## 检查点

启动经过以下取消检查点：

1. **入口。** 已经取消的调用方不读取设置快照、不解析 client factory，直接以安全取消结束。
2. **CreateClient 完成。** `ConsulClientProvider.CreateClient()` 同步落定之后——无论它返回会话、
   返回 null（禁用），还是抛出有限配置分类异常——都观察取消。

在检查点上观察到调用方取消时，启动以一个 `OperationCanceledException` 结束：其 `CancellationToken`
是调用方自己的 token，`InnerException` 为 null，`Message` 使用固定英文文字。该安全取消优先于：

| CreateClient 的完成 | 调用方已取消 | 结果 |
| --- | --- | --- |
| 返回 null（配置禁用） | 是 | 安全 OCE，不进入 Disabled 状态 |
| 抛 `SnapshotUnavailable`（无快照 / accessor 失败） | 是 | 安全 OCE，不保留原分类异常 |
| 抛 `InvalidConfiguration`（快照身份/schema 无效） | 是 | 安全 OCE |
| 抛 `ClientCreationFailed`（factory 返回 null / 抛异常） | 是 | 安全 OCE |
| 返回会话 | 是 | 会话被处置恰好一次，然后安全 OCE |
| 上述任意情况 | 否 | 既有行为不变：会话接线、Disabled、原分类异常 |

启动入口 provider 与 factory 是同步依赖，accessor/factory 自身抛出的普通异常或他人 token 的
`OperationCanceledException` 已被 provider 归一为有限 `ConsulConfigurationException`（不含内部值
与 inner）；启动检查点在其落定后裁决，不保留原 message、inner 或 provider code。

## 资源处置

取消前已产生并**即将交给生命周期**的会话，由启动按既有规则处置恰好一次（处置不是注销）；
处置失败只记录既有 `session_disposal_failed` 有限诊断，不掩盖调用方取消。factory 抛异常时其
内部尚未交付的资源仍由 factory 负责，本层不回收、不新增 factory 资源协议。取消路径不启动
sampler、owner loop 或任何注册调用，`CreateClient` 保持无 token 的同步 SPI 与既有正常异常分类。

## 不承诺的内容

- **检查点之后的窗口。** 完成检查点与返回之间到达的取消不会被捕获；已开始的启动不会被撤回。
- **不强行中断。** 同步 accessor/factory/Dispose 不可强制取消，本层只在它们落定后的有限检查点
  决定结果，不设启动硬性时间上界。
- **并发。** 同一 lifecycle 的并发 Start/Stop 或多次 Start 不在支持面内；仅保证两个独立生命周期的
  取消互不影响。
- **无新行为。** 没有新的禁用含义、重试、后台任务、远端补偿或停止行为变化。

## 如何被覆盖

`ConsulStartupCancellationTests`（Consul 测试工程，自有 accessor/factory doubles，复用既有 fixture
物化真实快照与 scripted client seam）：

- 预取消入口：不读快照、不解析 factory、无采样调用。
- 完成矩阵 7 类（禁用快照 / 无快照 / accessor 普通失败 / accessor 内部取消 / factory null /
  factory 普通失败 / factory 成功会话）在取消后全部以 caller token 的安全 OCE 结束，不含 canary，
  无 sampler/owner/注册调用；成功会话被处置恰好一次，生命周期快照版本保持 0。
- 取消会话的处置失败：仍抛 caller OCE，恰一条 `session_disposal_failed` 诊断。
- 未取消对照：enabled 正常注册、Disabled、`SnapshotUnavailable` / `InvalidConfiguration` /
  `ClientCreationFailed` 保持既有分类。
- 两个独立生命周期只取消其中一个，另一个正常注册。

既有生命周期与启动测试（正常 enabled/disabled 启动、重复注册配置、停止回归）继续成立。
