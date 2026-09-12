# ADR 0007：provider 中立性判据与契约归属边界

- 日期：2026-09-12；状态：决策已固定，待本 PR 合并；实现由独立 task 交付
- 决策 issue：[#432](https://github.com/philfanzhou/ServiceMantle/issues/432)
- 代码基线：`7d1f98edc7b6a91db77bc4a8ca38312e3c02dfef`
- 本决策补充 `CONTRIBUTING.md` 的「命名规范」，不改变既有的包边界规则。

## 背景

ServiceMantle 的初衷是让消费方拿到通用的底层能力，而不必关心底层选了哪个开源库。
`ServiceMantle.Consul`、`ServiceMantle.OpenTelemetry` 和 `ServiceMantle.Serilog` 是否
把具体实现泄漏给了消费方，此前没有可判定的标准，只能逐例争论。

本 ADR 固定一条可判定、可断言的判据，并据此把现有公开类型分类。它不承诺「换底层实现
零改动」——那个目标本身不可达，因为消费方总得在某处声明选了谁。

## 判据

换底层实现时，消费方要改的东西分三层：

| 层 | 内容 | 可接受的改动量 |
| --- | --- | --- |
| L1 业务代码 | 记日志、埋点、调用服务发现 | 必须为 0 |
| L2 组合根 | `Add*()` 一行 + `PackageReference` 一行 | ≥1，这是特性不是缺陷 |
| L3 持久化契约 | 设置键、管理 API 响应、运维手册 | 必须中立 |

**命名空间回答「这个类型是什么」，不回答「哪个包发的」。** 由此得到三类归属：

### A 类：中立契约住在 provider namespace —— 泄漏，必须迁移

判定：消费方必须在**自己的源码**里点名该类型（实现接口、resolve 后调用、catch），
而该类型的契约中没有任何 provider 特有成分。

### B 类：provider 特有契约住在 provider namespace —— 正确，不得迁移

判定：契约本身携带该产品的模型（认证方式、端点形状、模板语法）。

把 B 类改成中立名字会**更糟**：它隐藏耦合。消费方看到 `ConsulClientConfiguration`
就知道自己在跟 Consul 打交道；看到 `RegistryClientConfiguration` 反而会以为可移植，
直到换 Nacos 时发现 `Token` 的语义对不上（Nacos 用 user/pass）。
命名要诚实暴露耦合，不是用中立的名字把耦合藏起来。

### C 类：注册入口住在框架 namespace —— 已全部做到，保持

7 个入口全部位于 `Microsoft.Extensions.DependencyInjection` /
`Microsoft.Extensions.Hosting`，`AddServiceMantleSerilog` 甚至在签名里写全限定名
`ServiceMantle.Serilog.SerilogOptions` 以避免逼出 `using`。`samples/` 与 `docs/` 中
provider 命名的 `using` 数量为 0。

### 协议名不是库名

`ServiceMantle.OpenTelemetry.Otlp` 与 `.Prometheus` 不是泄漏。OTLP 是 CNCF 线协议，
Prometheus exposition 是格式标准。换掉 OTel SDK，OTLP 仍在；换掉 Serilog，Serilog
模板语法就没了。同理 `Meter`/`ActivitySource` 是 BCL，OTel SDK 只是其 listener，
`ServiceMantle.OpenTelemetry` 指的是「OTel SDK 绑定」，名副其实。

但「namespace 可接受」不等于「住在这里的每个类型都免迁」。判据看的是**契约**，不是
namespace 拼写：协议名让 `OtlpOptions` / `OtlpProtocol` 这类**配置契约**留在 `.Otlp`
名正言顺（B 类），却不豁免住在同一 namespace 的**中立扩展点**。一个消费方必须实现、
契约里没有任何协议特有成分的中立 resolver，即便住在协议命名的 `.Otlp` 下仍是 A 类——
`IOtlpAuthenticationHeaderResolver` 与 `.GrafanaLoki` 下的 `ILokiAuthorizationHeaderResolver`
同型（见下 A 类清单）。

## 分类结论

### A 类清单（迁移目标）

| 现状 | 消费方为何点名 | 目标归属 |
| --- | --- | --- |
| `ServiceMantle.OpenTelemetry.ServiceMetrics` | resolve 后调 `SetPhase()` | `ServiceMantle.Diagnostics` |
| `ServiceMantle.OpenTelemetry.Otlp.IOtlpAuthenticationHeaderResolver` / `OtlpAuthenticationHeader` | **必须实现**才能用带认证的 OTLP 导出 | `ServiceMantle.Diagnostics` |
| `ServiceMantle.Serilog.GrafanaLoki.ILokiAuthorizationHeaderResolver` | **必须实现**才能用带认证的远程 sink | `ServiceMantle.Logging` |
| `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiDiagnostics` | resolve 后读投递失败计数 | `ServiceMantle.Logging` |
| `ServiceMantle.Consul.IConsulClient` / `IConsulClientFactory` | 替换传输时实现 | `ServiceMantle.Discovery` |
| `ServiceMantle.Consul.ConsulLifecycleOptions` | 调整超时与退避 | `ServiceMantle.Discovery` |

`ServiceMetrics` 只使用 `System.Diagnostics.Metrics.Meter`；该程序集位于
`Microsoft.NETCore.App.Ref/10.0.11/ref/net10.0`，属共享框架，移入核心包
**不新增任何 `PackageReference`**。`ILokiAuthorizationHeaderResolver`、
`GrafanaLokiDiagnostics`、`IOtlpAuthenticationHeaderResolver` 与 `OtlpAuthenticationHeader`
同理（接口与 POCO）。

`eng/tests/consumers/serilog/Program.cs:3` 的 `using ServiceMantle.Serilog.GrafanaLoki;`
和 `eng/tests/consumers/composed/Program.cs` 的 `Report<ServiceMetrics>()` 是仓库自身
已经记录在案的泄漏证据。`IOtlpAuthenticationHeaderResolver` 当前尚无消费项目实现，泄漏
形状由判据直接推出：任何要做带认证 OTLP 导出的消费方都得实现它，从而被钉在 `.Otlp`。

### B 类清单（保持不动）

| 类型 | 携带的 provider 模型 |
| --- | --- |
| `ConsulClientConfiguration` | `Endpoint` + `GetToken()` 即 Consul agent HTTP API 与 ACL token |
| `ConsulServiceRegistration` | `Id = service:instance` + `HealthUri` 即 agent check 模型 |
| `SerilogOptions.OutputTemplate` | Serilog 模板语法 |
| `GrafanaLokiOptions` / `OtlpOptions` / `PrometheusOptions` | `CONTRIBUTING.md` §2 已认定模块词是调用方唯一区分，保留 |
| `WellKnownGrafanaLokiErrorCodes` 及 `loki.*` 取值 | 诊断码是外部契约，`CONTRIBUTING.md` §6 |

`OutputTemplate` 保留，但须在 XML 文档注释中明确声明它是 sink 实现相关的逃生舱，
换实现时不保证兼容。本 ADR 不为它引入中立枚举：目前没有第二个实现来验证该枚举设计。

### 已考虑并暂缓：中立凭据与寻址模型

曾考虑把 `ConsulClientConfiguration` 一并迁入 `ServiceMantle.Discovery`，设计一个能同时承载
Consul 的单个 ACL token、Nacos 的用户名/密码或 accessKey/secretKey 对、以及 etcd 的客户端证书
的中立凭据模型。**暂缓，不是否决。**

理由是中立化会把编译错误换成静默的语义错误。`GetToken()` 只能返回一个字符串；换到用户名/密码
模型后，中立化的类型仍然存在、仍然编译通过，错误要到运行时连接失败才暴露。保留
`ConsulClientConfiguration` 这个名字，换 provider 时该类型不存在会当场构建失败，迫使调用方去看
新 provider 实际需要什么。**那个构建失败正是这条规则的价值**：诚实的名字把静默的语义缺陷变成
可见的编译错误。

该模型现在也无法验证——仓库只有 Consul 一个实现，等真正做第二个 provider 时才知道设计是否
成立，而那时它已是公开 API。重新评估的触发条件是**第二个 registry provider 进入实现**，届时用
两个真实实现的公约数来定这个模型，而不是现在猜。

## 生命周期状态机不在本次范围

`ConsulRegistrationLifecycle`（等 readiness → 注册 → 指数退避重试 → 关停预算内注销）
确实与 Consul 无关，Nacos/etcd/Eureka 需要同一台机器。但**消费方从不点名它**——它由
`Add*()` 注册。因此它是代码复用问题，不是中立性问题，两者动机不同，按范围纪律分开决策。

上提它会迫使核心包新增 `Microsoft.Extensions.Hosting.Abstractions`（核心包当前
`IHostedService` 数量为 0），让所有核心消费方承担一个只有服务发现路径才用得上的依赖。
等第二个 provider 实际出现、能验证这台状态机的公约数时再单独评估。

## 抽象上限

`IServiceRegistrar` 只保证「一次性注册 / 注销」这条最小公约数。K8s 根本不需要服务注册
（平台代劳），Eureka 需要续租心跳，Nacos 有 namespace/group。心跳续租由 provider 在
自己的 `IServiceRegistrar` 实现内部处理。**本次不预先设计
`IServiceRegistrationHeartbeat`**：没有第二个实现验证的抽象就是猜。

## 明确不包含与不保证

- 保证：A 类类型迁移后，消费方源码中不再需要 provider 命名的 `using` 即可实现自定义
  registrar、实现远程日志授权解析、实现远程遥测认证解析、以及发布安装阶段指标。
- 不保证：不保证「换任意底层库零改动」。L2 的 `Add*()` 与 `PackageReference` 必须改。
  不保证 B 类类型在换实现后语义等价。不保证 `IServiceRegistrar` 覆盖心跳续租、
  namespace/group、或平台代劳注册的模型。
- 调用方责任：在组合根声明选择；B 类类型的可移植性由调用方自行评估。

## 后果与成本

1. A 类迁移是**源码与二进制破坏性变更**。完整映射写入 `NAMING_MIGRATION.md`，在后续
   新版本交付，不覆盖历史版本（`CONTRIBUTING.md` §6）。
2. `consul.*` 设置键改为中立键属于**外部契约变更**，不是改名。它需要独立的迁移路径，
   单独成 issue，不与类型迁移同 PR。
3. `eng/tests/consumers` 目前有 aspnetcore / composed / opentelemetry / serilog，**没有
   consul**。AGENTS.md 要求消费项目覆盖本次改名涉及的每个包，须补齐。
4. 判据须落成 CI 可挡的断言，而不只是文档里的一句话：新增一个消费项目，实现自定义
   registrar + 远程日志授权解析 + 远程遥测认证解析 + 读安装阶段指标，断言其编译不需要
   任何 provider 命名的 `using`。
5. 成本随消费方采用面扩大而上升。当前 #27 将可选 Consul 列为 P2；本 ADR 不改变排期，
   但把「越晚做越贵」这一点显式记录下来，由维护者决定是否提前。

## 拆分的实现 issue

本 ADR 不含产品代码。实现按契约拆分，每个 issue 对应一个 PR：

| Issue | 层 | 内容 | Blocked by |
| --- | --- | --- | --- |
| [#433](https://github.com/philfanzhou/ServiceMantle/issues/433) | L02 | `ServiceMetrics` 迁入核心包 `ServiceMantle.Diagnostics` | #432 |
| [#434](https://github.com/philfanzhou/ServiceMantle/issues/434) | L02 | 远程日志投递中立契约迁入 `ServiceMantle.Logging` | #432 |
| [#443](https://github.com/philfanzhou/ServiceMantle/issues/443) | L02 | OTLP 认证解析的中立契约迁入核心包 `ServiceMantle.Diagnostics` | #432 |
| [#435](https://github.com/philfanzhou/ServiceMantle/issues/435) | L02 | 服务注册中立契约迁入 `ServiceMantle.Discovery` | #432 |
| [#438](https://github.com/philfanzhou/ServiceMantle/issues/438) | L02 | `SerilogOptions` 级别与 scope 选项改用 MEL 词汇 | #432 |
| [#436](https://github.com/philfanzhou/ServiceMantle/issues/436) | L03 | discovery 设置键中立化与迁移路径 | #435 |
| [#437](https://github.com/philfanzhou/ServiceMantle/issues/437) | L04 | 中立性消费验证项目与 CI 断言 | #433 #434 #435 #436 #443 |

#436 与 #435 分开，是因为设置键是外部契约而非类型改名（`CONTRIBUTING.md` §6），需要自带
迁移路径。#437 是把判据落成 CI 会挡的断言，否则本 ADR 只是文档里的一句话。
#438 是盘点 #434 时发现的既有债务，与类型归属无关，按邻近债务规则独立成 issue。
#443 是 #434 的遥测侧同型：分类盘点时发现 `IOtlpAuthenticationHeaderResolver` 与 Loki 的
授权 resolver 形状一致，补入 A 类清单并独立成 issue，避免漏掉遥测侧扩展点。
