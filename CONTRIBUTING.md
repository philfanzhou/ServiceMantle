# 贡献指南

## 交付范围

一个 PR 只关闭一个 task issue。实现，以及适用的失败、取消、安全和并发测试，都在同一个 PR 中交付。
当一个改动长出第二个独立的包、契约或 endpoint 组时，拆 issue，不要把 PR 撑大。

## Review 政策

下面三条规则的存在，是因为一个 PR 可以一路逼近它的验收标准，却始终关不掉。每条规则各自点出一种
具体的关不掉的方式。

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

### 3. 按严重程度给 review 轮次做预算

破坏已声明保证的发现，无论要几轮，都在本 PR 里修完。

不破坏已声明保证的发现——资源整形、纵深防御、内部结构——最多修三轮。之后它们连同意见原文一起转入
follow-up issue，PR 合并。推迟它们是排期决策，不是质量让步：它们的成本有界且在 tracker 里可见，
而一个开着的 PR 每多一轮就多积累一份 rebase、重新 review 和集成成本。

在 PR 描述里写明这次推迟了什么，并链接对应的 follow-up issue。

## 命名规范

目录与 namespace 表达类型属于哪个模块，类型名表达它做什么。两者不重复同一段信息。

### 1. namespace 由项目根 namespace 加功能子目录决定

C# 不会从文件夹推导 namespace，因此移动文件时必须同时改声明和 using。一个 namespace 不得跨越两个
程序集：`ServiceMantle.AspNetCore` 的类型放在 `ServiceMantle.AspNetCore.<子目录>` 下，不放进核心包的
`ServiceMantle.Logging`、`ServiceMantle.Management`。

### 2. 普通类型不重复产品名与所在模块名

namespace 已经写明产品和模块，类型名就只写职责：`ServiceMantle.AspNetCore.Http.CorrelationIdMiddleware`，
不是 `ServiceMantle.Http.ServiceMantleCorrelationIdMiddleware`。

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

### 7. provider 命名的 namespace 只放 provider 特有契约

namespace 回答「这个类型是什么」，不回答「哪个包发的」。判定一个公开类型该放哪里，看消费方在
**自己的源码**里是否必须点名它（实现接口、resolve 后调用、catch），以及它的契约里是否含有该
产品特有的模型：

- **中立契约不得住在 provider 命名的 namespace。** 消费方必须点名、且契约中没有 provider 成分
  的类型，放核心包。例如「从非机密名字解析出 Authorization header」对任何远程日志端点都成立，
  不属于某个后端产品。
- **provider 特有契约必须留在 provider 命名的 namespace。** 契约本身携带该产品的认证方式、端点
  形状或模板语法时，中立的名字会**隐藏**耦合而不是消除它：调用方看到 `RegistryClientConfiguration`
  会以为可移植，直到换实现时发现 `Token` 的语义对不上。命名要诚实暴露耦合。
- **注册入口放框架 namespace**，按 §3 保留 `AddServiceMantle*` 前缀，使消费方的组合根不需要
  provider 命名的 `using`。
- **协议名不是库名。** OTLP、Prometheus exposition 是标准而非实现，对应 namespace 不属于泄漏。

可断言的形式：消费方源码中不允许出现 provider 命名的 `using`，provider 的名字只允许出现在
`.csproj` 的 `PackageReference` 和组合根的一行 `Add*()`。`eng/tests/consumers` 下的消费项目
以编译失败的方式守住这条。

判据全文、三层改动模型与现有类型的分类清单见
[ADR 0007](docs/decisions/0007-provider-neutral-contract-boundary.md)。
