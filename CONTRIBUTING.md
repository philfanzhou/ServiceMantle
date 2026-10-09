# 贡献指南

## Review 政策

### 1. 先写非保证，再写代码

写成全称命题的验收标准——「任何 secret 都不会到达 sink」「任何输入都被安全处理」「遍历始终有界」
——没有天然的收敛点。Review 永远能再举出一种没被考虑到的输入形状，于是这个 PR 永远到不了作者可以
称之为「做完了」的状态。

在动手实现之前，安全或健壮性相关的 issue 必须以与「保证什么」同等的精度，写清它**不**保证什么。
`LOGGING_SECURITY.md` 是参照样式：在给出保证边界的同时，它声明了确切的自由文本上限、调用方可以
依赖的输出类型，以及那些遍历上限并不约束的开销。

一条声明出来的非保证，把「这条路径无界」从一个 review 发现变成一个已知且已接受的边界。缺了这一节，
同一个观察就会无限次地把 PR 重新打开。

### 2. 按不变量修，而不是按分支修

当一条意见指出某一条代码路径时，先问同样的缺陷是否也存在于到达同一输出的其他路径上。如果存在，
修复就应该落在这些路径的汇合处——如果不存在这样一个汇合点，那么造出一个就是修复本身。

`StructuredLogSanitizer.NormalizeSafeScalar` 是那个已经做过的例子：每一个安全标量都经由这一个方法
成为输出，因此一种没有任何 sink 能表达的值形状会被拒绝一次，而不是每条分支各拒绝一遍。只补报告里
那一条分支，往往是把缺陷挪了个位置而不是移除它，被挪走的缺陷会作为下一轮的意见回来。

这类修复要配一个对**一组输入**断言该不变量的测试，而不是只针对报告里的那一个用例。上面这个例子
对应的测试是 `Sanitized_output_is_always_serializable_by_the_default_serializer`。

涉及异步完成语义时，在目标契约范围内列出入口拒绝、正常完成、
异常完成、清理/释放后完成的适用检查点；不适用须说明理由。副作用是否已发生与最终返回何种结果分别
验证，不得以“已执行所以无需观察取消”替代已声明的完成语义。对高风险检查点采用必要的反向或变异
验证，确认错误检查位置会使测试失败；不要求机械地为每行代码做变异。

## 发布版本策略

自动选择下一发布版本时，以官方 NuGet feed 已发布的最近稳定版本为基线，**只递增三段版本号的
最后一位（patch）**，保持 major 和 minor 不变。新增能力、API 数量或破坏性变更都不构成自动
递增 major/minor 的授权；只有维护者明确指定其他版本号时才按该指令发布。

- 最近稳定版为 `0.3.0` 时，下一候选版本是 `0.3.1-rc.1`，不是 `0.4.0-rc.1`。
- 同一候选周期追加 RC 时保持 `0.3.1` 不变，仅递增后缀为 `rc.2`、`rc.3` 等；转正式版时
  去掉 RC 后缀，发布 `0.3.1`，不再递增 patch。
- `0.3.1` 正式发布后，下一个发布周期从 `0.3.2-rc.1` 开始。
- 同一版本的失败重跑保持原 tag、版本和源码提交，不算版本递增；草稿、未发布 tag 与 edge
  构建输出不作为稳定版本基线。已发布版本不得删除、覆盖或重新指向其他提交。

全部注册包继续使用同一版本和 tag；发布验证及 `nuget.org` environment 人工审批按
[发布指南](RELEASING.md) 执行。

## 命名规范

目录与 namespace 表达类型属于哪个模块，类型名表达它做什么。两者不重复同一段信息。

### 1. namespace 按能力组织，能力 namespace 归核心包

C# 不会从文件夹推导 namespace，因此移动文件时必须同时改声明和 using。可选适配包的自有类型放在
它实现的能力所对应的 namespace 或其子空间，而不是技术选型命名的 namespace：ASP.NET Core 适配包
的自有类型放 `ServiceMantle.Web` 及其子空间（如 `ServiceMantle.Web.Http`），Consul 适配包放
`ServiceMantle.Discovery.Registration` / `ServiceMantle.Discovery.Configuration`，Serilog 适配包放
`ServiceMantle.Logging.Pipeline` / `ServiceMantle.Logging.Remote`，OpenTelemetry 适配包放
`ServiceMantle.Diagnostics.Instrumentation` / `ServiceMantle.Diagnostics.Export.*`，EF Core 持久化包
放 `ServiceMantle.Persistence.Relational` 及其子空间。逐包固定映射见
[ADR 0007](docs/decisions/0007-provider-neutral-contract-boundary.md) 的「重新评估：能力命名空间」。

能力 namespace 本身归核心包所有：`ServiceMantle.Logging` 是核心包的 namespace，子空间
`ServiceMantle.Logging.Pipeline` 才是 Serilog 适配包的自有 namespace。一个 namespace 不得跨越两个
程序集，能力 namespace 与其子空间的所有权划分保证这一点。

公开契约上移核心包的判据是**签名只依赖核心包既有类型**：`IServiceStartupPhaseResolver` 的签名只
依赖 `ServiceStartupPhase` / `ServiceInstallationState`，可以上移；`IServiceDbContext` 的签名含
EF Core 的 `DbSet`，留在持久化适配包。名字看起来中立不构成上移理由。

六个数据库 provider 包（`ServiceMantle.Database.*`）的 namespace 与包名保持 provider 命名，不适用
本条。

### 2. 普通类型不重复产品名与所在模块名

namespace 已经写明产品和能力，类型名就只写职责：`ServiceMantle.Web.Http.CorrelationIdMiddleware`，
不是 `ServiceMantle.Web.Http.ServiceMantleCorrelationIdMiddleware`。

去前缀不是把名字压到最短。当模块词是调用方同时引用多个同类类型时的唯一区分（`SerilogOptions`、
`GrafanaLokiOptions`、`OtlpOptions`），保留它；不要把所有配置类都缩成 `Options`，那会把负担转嫁成
每个调用方都要写全限定名。

### 3. 框架扩展入口保留产品前缀

`Microsoft.Extensions.DependencyInjection`、`Microsoft.Extensions.Hosting`、`Microsoft.AspNetCore.*`
下的扩展类和扩展方法保留 `AddServiceMantle*`、`UseServiceMantle*`、`MapServiceMantle*`、
`WithServiceMantle*` 及对应的 `ServiceMantle*Extensions` 类名。这些 namespace 不含产品信息，名字是
它与框架自带 API 的唯一区分，也是避免与其他库冲突的手段。

### 4. 与框架或上游库同名时按职责改名，而不是靠别名硬撑

去掉前缀后若与调用方默认可见的类型冲突，改成能说明职责的名字，并在迁移说明里记下理由。已知例子：
`ForwardedHeadersTrustOptions`（避开 `Microsoft.AspNetCore.Builder.ForwardedHeadersOptions`）、
`ServiceHeaderNames`（避开 `Microsoft.Net.Http.Headers.HeaderNames`）、`ServiceMetrics`（避开
`System.Diagnostics.Metrics`）、`RuntimeLoggerProvider`（避开 `Serilog.Extensions.Logging.SerilogLoggerProvider`）。
类型别名和全限定名只用于确实无法避免的单点消歧，例如包装同名上游类型的那一行。

### 5. 文件名与主要类型名一致

同一契约族的公开类型可以同文件，文件按该族的主类型命名（既有风格见 `ServiceSettingPersistence.cs`、
`BootstrapManagementModels.cs`）。彼此独立的公开类型拆成同名文件。程序集属性文件名用
`AssemblyInfo.cs`，不用重复完整程序集名的 `<程序集名>.Internals.cs`；`src` 下的可打包项目放在
`Properties/AssemblyInfo.cs`。

### 6. 改名不得移动外部契约

日志分类、诊断码、配置键、HTTP 路由与 Header、JSON 字段、数据库表列、Data Protection purpose、
认证方案名、指标名、程序集属性字符串都是外部契约，不随类型改名变化。改名时逐条核对字符串字面量，
不做无差别文本替换。确因类型全名改变而变化的反射或诊断输出，单独列明并更新针对性验证。

公开类型改名同时是源码和二进制破坏性变更：完整映射写入 `NAMING_MIGRATION.md`，在后续新版本交付，
不覆盖历史版本。

### 7. provider 特有契约留在适配包，由类型名诚实暴露

namespace 回答「这个类型是什么」，不回答「哪个包发的」。技术选型（Consul、Serilog、
OpenTelemetry、EF Core、ASP.NET Core）出现在包名和必要的类型名里，不出现在适配包的自有
namespace 里。判定一个公开类型该放哪里，看消费方在**自己的源码**里是否必须点名它（实现接口、
resolve 后调用、catch），以及它的契约里是否含有该产品特有的模型：

- **中立契约进核心包。** 消费方必须点名、且契约中没有 provider 成分的类型，放核心包的能力
  namespace，上移判据按 §1 的签名检查执行。例如「从非机密名字解析出 Authorization header」对
  任何远程日志端点都成立，不属于某个后端产品。
- **provider 特有契约留在适配包，类型名保留产品词。** 契约本身携带该产品的认证方式、端点形状
  或模板语法时，中立的名字会**隐藏**耦合而不是消除它：调用方看到 `RegistryClientConfiguration`
  会以为可移植，直到换实现时发现 `Token` 的语义对不上。命名要诚实暴露耦合；这些类型住在能力
  namespace 的适配包子空间（如 `ServiceMantle.Discovery.Registration`），不因 namespace 中立而
  伪装成可移植。
- **注册入口放框架 namespace**，按 §3 保留 `AddServiceMantle*` 前缀，使消费方的组合根不需要
  为注册本身增加 `using`。
- **协议名不是库名。** OTLP、Prometheus exposition 是标准而非实现，对应 namespace 不属于泄漏，
  随能力命名住在 `ServiceMantle.Diagnostics.Export.*`。

可断言的形式：消费方源码中不允许出现适配包自有 namespace 的 `using`。#570 之后适配包自有
namespace 已按能力命名，因此测试检查以下命名空间名称集合：已退役的技术选型命名 namespace
（`ServiceMantle.AspNetCore`、`ServiceMantle.Consul`、`ServiceMantle.Serilog`、
`ServiceMantle.OpenTelemetry`、`ServiceMantle.Persistence.EntityFrameworkCore`）不得复活，且归
ASP.NET Core 适配包独有的 `ServiceMantle.Web` 前缀（核心包无此裸 namespace 或子空间类型）同样
不得出现在中立消费方源码中——包 ID 已随 #585 对齐能力命名；适配器的名字只出现在 `.csproj` 的
`PackageReference`、组合根的一行 `Add*()`，以及 provider 特有类型与成员的名字
（`ConsulClientConfiguration`、`SerilogOptions.OutputTemplate`）里。`eng/tests/consumers` 下的
消费项目以编译失败的方式守住这条，provider-neutral 消费项目的源边界断言在 CI 里以同一名字
集合 grep 实现。

替换 provider 特有传输或凭据的扩展点（[ADR 0007](docs/decisions/0007-provider-neutral-contract-boundary.md)
的 B 类 SPI，例如 Consul 传输）属于 provider 特有代码，不在该断言范围内。

判据全文、三层改动模型、现有类型的分类清单与逐包映射见
[ADR 0007](docs/decisions/0007-provider-neutral-contract-boundary.md)。
