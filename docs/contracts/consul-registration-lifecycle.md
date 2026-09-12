# Consul 注册生命周期决策（#290）

状态：已由 #49 实现。本文档仍是规范性的状态、完成与停止矩阵；实现位于 `ServiceMantle.Consul`。

## 决策

Consul 生命周期消费来自核心 `ServiceMantle.Health` 命名空间的一个与 provider 无关的
`IServiceReadinessDecisionSource`。一个决策包含评估所使用的精确不可变 `ServiceHealthSnapshot`、
经过基础矩阵和有序 readiness contributor 之后的最终 Ready 值，以及仅在未 Ready 时的一个有界安全
错误码。Issue #321 拥有这个新的公开契约，并把 ASP.NET Core 健康 endpoint 改为消费其默认 adapter。
Issue #49 被 #321 阻塞。

这是生命周期唯一的 readiness 输入。`ServiceMantle.Consul` 不引用 ASP.NET Core、不向
`/health/ready` 发出 HTTP 请求、不重复 readiness 算法，也不接受独立提供的布尔值。健康 endpoint
请求绝不会启动或驱动注册。消费方为 HTTP 健康 surface 和可选的 Consul 生命周期注册同一个决策
来源；不同的调用可能采样到不同时刻，但它们使用同一个来源和同一套评估契约。

```text
consumer-owned base health state + registered contributors
                         |
                         v
             IServiceReadinessDecisionSource       (core contract)
                         |
             +-----------+----------------+
             |                            |
             v                            v
   ASP.NET Core health endpoints    Consul lifecycle owner
                                          |
                                          v
                              snapshot-bound Consul session
```

生命周期是一个托管 controller。只有其 owner 循环变更状态并发起 Consul 操作。一个单一的、不重叠的
readiness 采样器可以向该循环发布决策。采样器绝不执行 Consul 工作，所有远程完成都会返回给 owner
循环。一个实例在任一时刻至多有一个注册或注销操作处于活动状态。

## 启动与配置归属

`StartAsync` 检查其调用方 token，恰好调用一次 `ConsulClientProvider.CreateClient()`，并在启动
controller 之前再次检查调用方 token：

| Provider 结果 | 启动结果 | 拥有的资源 |
| --- | --- | --- |
| `null` | 进入终态 `Disabled`；成功返回 | 没有 client、timer、采样器或后台循环 |
| Session | 以远程存在性 `Absent` 进入 `NotReady`；启动一个采样器和 owner 循环 | 生命周期独占该 session 直至停止 |
| `SnapshotUnavailable` 或 `InvalidConfiguration` | 以既有的安全异常使启动失败 | 无后台工作；若已产生 session 则将其处置 |
| `ClientCreationFailed` | 以既有的安全异常使启动失败；不重试 | 无后台工作 |
| 启动之前或期间的调用方取消 | 传播携带原始 token 的 `OperationCanceledException` | 处置在观察到取消之前创建的任何 session |

session 捕获一个活动设置快照及其版本。所有 `discovery.*` 定义都绑定到重启。之后更新的活动快照版本
不会被监视、重新绑定或调和。endpoint、token、注册、disabled 标志或健康 URL 的变更只有在消费方
自行重启进程后才生效。生命周期不会再次调用 `CreateClient()`。

注册重试、清理注销和停止使用同一个 session 和注册 ID。生命周期在最终远程操作落定之后，或在协作
关闭预算耗尽之后，调用一次 `Dispose()`。处置失败会成为一条安全诊断，且不重试。处置不意味着注销。

## 设置键中立化与显式迁移（#436）

Consul adapter 注册的 8 个持久化设置键从 `consul.*` 改为 `discovery.*`。常量成员名、类型名与
namespace 保持不变，只有键值移动。诊断码（含 `consul.invalid_configuration`）、`X-Consul-Token`
认证 Header 与 HTTP wire model 本次不变。这是随新版本交付的外部契约变更，不覆盖历史包。

| 旧键 | 新键 |
| --- | --- |
| `consul.enabled` | `discovery.enabled` |
| `consul.endpoint` | `discovery.endpoint` |
| `consul.token` | `discovery.credential` |
| `consul.service-name` | `discovery.service-name` |
| `consul.address` | `discovery.address` |
| `consul.port` | `discovery.port` |
| `consul.health-path` | `discovery.health-path` |
| `consul.health-scheme` | `discovery.health-scheme` |

`discovery.credential` 表示 provider 定义的单一凭据字符串；当前 Consul adapter 仍将它作为 ACL
token 使用，保留 1–4096 个非空白可打印 ASCII 字符校验，不宣称支持其他 provider 的多字段认证。
持久化密文以设置键为保护 purpose，因此 `consul.token` 密文必须以旧 purpose 解密、以
`discovery.credential` 为 purpose 重新加密；密文行不能只改键名。

新版拒绝包含任何旧键的完整快照：loader 返回 `configuration.snapshot_unknown_key`，首次加载不
激活，包含旧键的“禁用”混合快照也不会静默成功。没有别名、读兼容或自动迁移。已激活快照之后的
失败刷新保留原快照引用，沿用既有契约。

停机升级步骤：

1. 用原版备份完整设置行及版本，然后停止读取或写入该设置存储的全部消费实例；禁止新旧版本混跑。
2. 消费方在自有工作单元内检查旧/新键冲突，逐项映射已有行；其他产品键保持。遇到目标键已存在、
   未知版本/类型或解密失败时终止，不覆盖已有数据。
3. 凭据密文以旧键 `consul.token` 为 purpose 解密，再以 `discovery.credential` 为 purpose 重新
   加密；使用既有 `SensitiveValueProtector`、相同 service_id 和正确外部根密钥。不输出明文、根
   密钥或密文。
4. 消费方一次性提交完整映射与一致的新设置版本，按自己的审计/事务约束执行；库不自动保存或提交
   消费方 DbContext。
5. 启动新版前确认设置中不再存在任何旧键；刷新完整快照成功后才创建 client。

新版管理 API 不是旧行迁移入口：未知旧键使完整加载/更新失败，不能靠新版 POST settings 清除旧键
后“边运行边迁移”。回滚必须在全部实例停止后恢复成套旧设置/版本备份及旧程序；仅回滚二进制不能
读取新 purpose 密文。未知提交结果由消费方核查，不提供自动补偿。

README（英文）中的 `ConsulDiscoverySettingMigration.TryConvert` 内存转换示例是经测试的事实源，
对应 `tests/ServiceMantle.Consul.Tests/ConsulDiscoverySettingMigrationTests.cs`：无凭据、合法凭据、
目标键冲突、错误根密钥、损坏密文与已取消 token 各有断言，失败与取消均不产出可提交的部分结果。
示例只做内存转换与输入检查，不证明消费方数据库提交的原子性、持久性或异常恢复；停机、备份、根
密钥、事务、版本、审计与回滚均由消费方负责。

## 状态模型

有限控制状态与一个保守的远程存在性观察配对：

| 状态 | 含义 | 允许的远程存在性 |
| --- | --- | --- |
| `Disabled` | 捕获的配置被禁用；直到重启前都是终态 | `Absent` |
| `NotReady` | 已启用，最新决策不是 Ready，且没有操作处于活动状态 | `Absent` 或 `Unknown` |
| `Registering` | 有一个注册操作处于活动状态 | `Absent` 或 `Unknown` |
| `Registered` | 在最新期望为存在时，一个注册操作返回了 `Success` | `Present` |
| `Deregistering` | 有一个注销操作处于活动状态 | `Present` 或 `Unknown` |
| `Backoff` | 没有远程操作处于活动状态；一个按意图区分的重试延迟处于活动状态 | `Unknown`，注册重试时为 `Absent` |
| `Stopping` | 停止具有优先权；不得开始新的注册 | `Absent`、`Present` 或 `Unknown` |

`Absent` 意味着自启动以来没有发生过成功的注册，或者最近一次完成的注销返回了 `Success`。`Present`
要求一次完成的注册 `Success` 之后没有跟随一次完成的注销 `Success`。`Unknown` 意味着超时、取消、
`Rejected`、`Unavailable`、未定义结果或未完成的操作可能已产生远程副作用。特别地，注册超时绝不
意味着"未注册"。

期望存在性仅由最新的 readiness 事件推导：Ready 意味着 `Present`；未 Ready、readiness 失败和停止
意味着 `Absent`。状态、期望存在性、远程存在性、尝试次数、操作代次和 session 归属只能由 owner
循环改变。

## 计时与重试策略

Issue #49 引入一个经过校验的生命周期选项对象，具有以下精确默认值和闭区间范围：

| 选项 | 默认值 | 最小值 | 最大值 | 用途 |
| --- | ---: | ---: | ---: | --- |
| readiness 轮询间隔 | 1 s | 100 ms | 30 s | 两次完成的 readiness 采样之间的延迟 |
| readiness 调用预算 | 10 s | 100 ms | 60 s | 一次决策来源调用的外层预算 |
| Consul 操作预算 | 10 s | 100 ms | 30 s | 传给一次注册/注销调用并等待其完成的预算 |
| 初始重试延迟 | 250 ms | 50 ms | 5 s | 第一次传输重试延迟 |
| 最大重试延迟 | 5 s | 初始延迟 | 30 s | 指数延迟上限 |
| 关闭总预算 | 15 s | 1 s | 60 s | 停止开始后的协作清理总时长 |

无效、非有限、冲突或超出范围的值会在 readiness 采样器、timer 或远程操作启动之前使主机启动失败。
时长使用 `TimeProvider`；测试使用 fake time。重试延迟为
`min(maximum, initial * 2^failureCount)`，并带溢出安全的饱和。没有 jitter。一次成功的操作或期望
存在性的变化会重置失败计数。

readiness 失败在固定的轮询间隔后再次采样，不使用传输退避。注册和注销的 `Rejected`、
`Unavailable`、未定义结果和内部超时使用指数传输退避。只要相应期望仍是最新的，重试就会继续，因此
在任意长的进程生命周期内的尝试次数被刻意不设上限。有界的是单次操作、每次延迟、并发和关闭工作。
不保证最终注册成功，也不保证最大恢复时间。

既有的默认 HTTP client 保留其自身的 10 秒超时。生命周期操作预算是一个额外的归属上界，同样适用于
替换 client。

关闭总预算和 Consul 操作预算运行在同一条时间线上，但结束的东西不同。关闭预算在停止开始时、owner
循环被唤醒之前启动，它约束清理注销：一个已在途的操作落定所花的时间会从清理的剩余时间中扣除，
且清理重试延迟会被剩余预算截断，而不是用全新的预算重新开始。它不会缩短已在途的操作，该操作自身
的取消期限仍是 Consul 操作预算；一个已经运行了该预算一部分的操作会在其余时间内落定，这比完整
预算是更紧的上界。因此，对于协作的 client 和决策来源，一次停止的结果上界是
`max(Consul operation budget, shutdown total budget)`，而不是仅关闭预算：合法组合
`Consul operation budget = 30 s` 与 `shutdown total budget = 1 s` 可能花费约 30 秒。该模型是协作
上界，不是精确的调度时间，也不是对任意清理代码的墙钟上界。

## 转换矩阵

下表是规范性的。"最新期望"包括在某个操作处于活动状态期间排队的任何 readiness 事件。

### readiness 与稳定状态

| 当前状态 | 事件 | 动作与下一状态 |
| --- | --- | --- |
| `Disabled` | 任何 readiness 或停止事件 | 忽略 readiness；停止已经完成；保持 `Disabled` |
| `NotReady/Absent` | 未 Ready、来源异常、null/无效决策或内部 readiness 超时 | 无远程调用；保持 `NotReady/Absent`，附带一条安全的 readiness 不可用诊断 |
| `NotReady/Absent` | Ready | 以新代次开始一次注册；进入 `Registering` |
| `NotReady/Unknown` | 未 Ready | 开始清理注销；进入 `Deregistering` |
| `NotReady/Unknown` | Ready | 为同一注册 ID 开始幂等注册；进入 `Registering` |
| `Registered/Present` | Ready | 无远程调用；保持 `Registered` |
| `Registered/Present` | 未 Ready 或 readiness 失败 | 立即开始注销；进入 `Deregistering` |
| 注册 `Backoff` | Ready | 延迟完成时开始一次注册；进入 `Registering` |
| 注册 `Backoff` | 未 Ready | 取消延迟；若存在性为 `Unknown` 则注销，否则进入 `NotReady/Absent` |
| 注销 `Backoff` | 未 Ready | 延迟完成时开始一次注销；进入 `Deregistering` |
| 注销 `Backoff` | Ready | 取消延迟；为同一 ID 开始注册；进入 `Registering` |

readiness 采样器是失败关闭的。来源异常、内部取消的调用、内部超时、null 决策或无效/未定义决策都
是一个未 Ready 事件。调用方对 `StartAsync` 或 `StopAsync` 的取消不会被转换为该事件；它保留其原始
token。

### 注册完成

| 结果 | 最新期望 | 存在性与下一状态 |
| --- | --- | --- |
| `Success` | 存在 | `Present`；进入 `Registered`；重置退避 |
| `Success` | 不存在 | `Present`；立即开始注销；绝不把 `Registered` 暴露为落定状态 |
| `Rejected`、`Unavailable` 或未定义 | 存在 | `Unknown`；进入注册 `Backoff` |
| `Rejected`、`Unavailable` 或未定义 | 不存在 | `Unknown`；立即开始清理注销 |
| 内部操作超时/取消 | 存在 | 请求取消，等待协作落定，然后 `Unknown` 并进入注册 `Backoff` |
| 内部操作超时/取消 | 不存在 | 请求取消，等待协作落定，然后 `Unknown` 并开始清理注销 |
| 调用方停止 | 任意 | 应用下文的停止规则；绝不再开始另一次注册 |

### 注销完成

| 结果 | 最新期望 | 存在性与下一状态 |
| --- | --- | --- |
| `Success` | 不存在 | `Absent`；进入 `NotReady`，或完成 `Stopping` |
| `Success` | 存在 | `Absent`；立即开始注册 |
| `Rejected`、`Unavailable` 或未定义 | 不存在 | `Unknown`；进入注销 `Backoff` |
| `Rejected`、`Unavailable` 或未定义 | 存在 | `Unknown`；只在注销落定之后才开始注册 |
| 内部操作超时/取消 | 任意 | 请求取消，等待协作落定，保留 `Unknown`，然后遵循最新期望 |
| 调用方停止 | 任意 | 在剩余关闭预算内继续清理 |

注销期间的 Ready 翻转绝不会开始一次重叠的注册。注销必须先落定；随后注册会重新建立期望的记录。
注册期间的未 Ready 翻转会请求取消该次尝试，但 controller 会等待其落定后再注销。这个顺序防止迟到
的注销删除更新的注册，也防止迟到的注册逃脱清理。

每个操作和 readiness 采样都有一个单调递增的代次。代次不再是活动代次的完成结果不能直接选择
`Registered` 或 `NotReady`。owner 只记录其保守的存在性含义，并应用最新的期望状态。停止完成之后
送达的事件会被忽略。

## 停止矩阵

停止首先取消采样器和每个退避延迟。它把期望存在性设为 `Absent` 并启动关闭总预算。此后不得开始
新的注册操作。

| 停止开始时的状态 | 关闭动作 |
| --- | --- |
| `NotReady/Absent` | 处置 session；完成停止 |
| `Registered/Present` | 在剩余关闭预算内开始注销并重试 |
| `Backoff` 且存在性为 `Present` 或 `Unknown` | 取消延迟；在剩余预算内开始注销 |
| `Registering` | 取消注册，等待落定，然后注销，因为存在性可能为 `Present` 或 `Unknown` |
| `Deregistering` | 在其自身操作预算下等待当前尝试；只在关闭预算仍有剩余时重试 |
| `NotReady/Unknown` | 在剩余预算内尝试注销 |

如果注销成功，生命周期处置 session 并完成。如果内部关闭预算耗尽，它停止创建操作，发出一条安全的
`shutdown_timeout` 分类，只在任何协作的在途操作落定之后才处置，并且在不声称远程不存在的情况下
完成。因此，关闭预算耗尽不会结束停止开始时已在途的操作：该次尝试按其自身操作预算落定，所以一次
停止最多可以超出关闭预算一个操作预算的时间。如果 `StopAsync` 调用方 token 先取消，则传播原始
token 且不开始新工作；尽力而为的归属清理遵循同样的不重叠规则。

## 失败与诊断分类

诊断仅是有限的分类和元数据：控制状态、尝试计数、捕获的快照版本、操作种类和安全的结果类别。它们
绝不包含 ACL token、注册 body、endpoint、健康 URL、地址、服务名、注册 ID、原始异常或响应 body。

| 输入 | 分类与行为 |
| --- | --- |
| readiness 来源失败、null/无效结果或内部取消 | `readiness_unavailable`；期望变为不存在 |
| readiness 调用预算耗尽 | `readiness_timeout`；期望变为不存在 |
| 注册 `Rejected` | `register_rejected`；存在性 `Unknown`；若仍为 Ready 则重试 |
| 注册 `Unavailable`、未定义结果或内部取消 | `register_unavailable`；存在性 `Unknown`；若仍为 Ready 则重试 |
| 注册操作预算耗尽 | `register_timeout`；存在性 `Unknown`；保持不重叠 |
| 注销 `Rejected` | `deregister_rejected`；存在性 `Unknown`；按最新期望重试 |
| 注销 `Unavailable`、未定义结果或内部取消 | `deregister_unavailable`；存在性 `Unknown`；按最新期望重试 |
| 注销操作预算耗尽 | `deregister_timeout`；存在性 `Unknown`；保持不重叠 |
| 关闭总预算耗尽 | `shutdown_timeout`；不声称不存在 |
| session 处置抛出异常 | `session_disposal_failed`；不重试，也不声称处置完成了注销 |

既有的 `ConsulClientSession` 已经把替换 client 异常、内部取消和未定义枚举转换为 `Unavailable`，
同时传播其所持 token 的取消。生命周期在选择分类之前会区分自身的超时 token 与
`StartAsync`/`StopAsync` 调用方 token。

同步的 `IConsulClientFactory.Create()` 和 `IDisposable.Dispose()` 无法被强制取消。替换的
`IConsulClient` 也可能忽略取消。ServiceMantle 不为这类不协作的代码承诺硬性墙钟上界。它绝不会仅仅
为了制造超时而开始重叠的远程操作，或在 client 的操作尚未完成时并发处置它。

## #49 的必需验证

所有生命周期测试使用 fake `TimeProvider`、脚本化的 readiness 决策、脚本化的 session 和操作屏障。
最小矩阵为：

- 禁用启动证明零 client 解析、采样器、timer、循环和远程调用；
- 每个基础非 Ready 矩阵值和每个 contributor 拒绝/失败证明零注册；
- Ready 注册一次，重复 Ready 不会重复注册，之后的未 Ready 决策注销同一 ID；
- 屏障在注册和注销期间翻转 readiness，并证明没有重叠的远程调用、最新期望处理和过期代次处理；
- 每个有限的传输结果、异常、内部取消、未定义枚举和超时证明存在性分类、精确重试延迟、在最大值处
  饱和以及安全诊断；
- 从每个状态执行停止，包括 backoff 和两种在途操作；它证明关闭总预算、调用方 token 优先、成功清理
  和未知存在性结果；
- session 创建、启动调用方取消、处置失败和不协作的替换 client 证明文档化的归属边界；
- 并发的生命周期通知保持串行化，记录的活动 Consul 操作最多为一个；
- 在更新的活动设置快照出现之后，捕获的快照版本和 session 保持固定；不断言任何热重载行为；
- 所有异常、结果、诊断、序列化和 `ToString()` 投影都对照一个哨兵 token 检查。

既有的单次调用传输测试仍是 HTTP 方法、路径、body、token Header、重定向行为和默认 10 秒 client
超时的证据。生命周期测试不声称针对真实 Consul 集群做过验证。

## 明确的非保证

- agent 响应成功不保证已传播到每个 catalog 或 DNS 读取方。
- 注销成功不证明所有业务流量已经排空。
- 进程终止、机器故障、网络分区、未知的远程结果或不协作的替换 client 无法被回滚或强制完成。
- 进程生命周期内的总重试尝试次数和恢复时间没有上界。单次调用、单次延迟、并发和协作关闭预算按上文
  规定是有界的。
- 关闭总预算不是 `StopAsync` 的硬性墙钟上界。对于协作的 client 和决策来源，上界是
  `max(Consul operation budget, shutdown total budget)`；忽略取消的 client、factory、scope 处置或
  `Dispose` 完全没有时间上界，且它们中任何一个都不会为了换取上界而被抛弃或重叠执行。
- 没有配置热重载、跨实例协调、超出稳定 Consul 注册 ID 之外的幂等键，或对未知结果的补偿。
- 生命周期不会把 readiness contributor 变成调度器，也不会触发 setup、migration、数据库创建、配置
  刷新或业务写入。
- token 保密覆盖 ServiceMantle 拥有的诊断和投影。它不覆盖自定义 factory、外部 HTTP instrumentation、
  调试器、进程内存或 Consul agent 日志的蓄意访问。
