# 参考服务骨架

这是一个由消费方自有的验收宿主，不是生产模板。它刻意只暴露 `GET /`，返回
`status: skeleton`。默认情况下，启动不创建数据库、不执行迁移、不运行 setup contributor、
不预配管理员，也不启用管理/健康/遥测 endpoint。一个 opt-in 的启动部署 gate 可以显式打开；
除非下面每一项输入都已声明，否则它是关闭的。

```bash
dotnet run --project samples/ServiceMantle.ReferenceService -- --urls http://127.0.0.1:5080
```

本示例与外部消费方使用完全相同的公开 ServiceMantle 包项目。仓库内构建对这些可打包项目使用
`ProjectReference`；绝不导入库源文件或使用库内部成员/`InternalsVisibleTo`。以发布包方式消费
是单独的发布验收任务（#113）。样例及其冒烟测试包含在解决方案中；测试项目登记在
`eng/packages.json` 的 ASP.NET Core 之下，因此既有的 ReleaseTool 与 CI 会还原、构建并测试
两个项目，无需单独的样例清单。

| 消费方自有组件 | 当前边界 | 后续 |
| --- | --- | --- |
| `ReferenceApplication` | 公开的组合接缝，一条骨架路由 | 由集成任务共享 |
| `ReferenceDbContext` 与 `Data/Migrations` | 一张 workspace 表；迁移、保存与事务由调用方拥有 | #160 |
| `Database/Sqlite/` | opt-in 的 SQLite 启动部署 gate 与消费方自有的迁移 executor | #112 / #113 |
| `ReferenceSetupContributor` | 只读校验与仅 staging 的示例；启动时绝不调用 | [#175](https://github.com/philfanzhou/ServiceMantle/issues/175) |
| `ReferenceSettingDefinitions` | 只有默认值与约束；没有 store、HTTP 或激活 | #177 |
| `ReferenceReadinessContributor` | 返回 `reference.health_not_integrated`；绝不声称就绪 | #156 |
| `ExternalManagementIdentityPlaceholder` | 以安全的未配置 provider 错误码返回 Failed | 未来的外部身份集成 |
| `Logging/` | opt-in 的 Serilog Console 接线与一行已清理的请求日志 | 由 [#157](https://github.com/philfanzhou/ServiceMantle/issues/157) / [PR #307](https://github.com/philfanzhou/ServiceMantle/pull/307) 交付 |
| `Telemetry/` | opt-in 的基础 ASP.NET Core、HttpClient 与运行时插桩；没有 exporter | [#158](https://github.com/philfanzhou/ServiceMantle/issues/158) |

EF SQLite 是消费方的模型载体。启动 gate 关闭时，ServiceMantle 的 SQLite provider 完全不注册：
默认文件是内容根目录下的 `reference.db`，`ReferenceService:DatabasePath` 可以更改它，而且仅仅
启动宿主既不创建该文件也不迁移它。初始迁移与快照放在这里，绝不放在 ServiceMantle 中。

<a id="explicit-sqlite-startup-deployment"></a>

## 显式 SQLite 启动部署

```bash
dotnet run --project samples/ServiceMantle.ReferenceService -- \
  --ReferenceService:SqliteStartup:Enabled true \
  --ReferenceService:SqliteStartup:DeploymentMode SingleInstance \
  --ReferenceService:SqliteStartup:PrepareIfMissing true \
  --ReferenceService:DatabasePath /absolute/path/reference.db \
  --urls http://127.0.0.1:5080
```

`ReferenceService:SqliteStartup:Enabled` 默认为 `false`；只有显式的 `true` 才会激活该
gate。打开时：

- `DeploymentMode` 必须声明且必须是 `SingleInstance`。`Unspecified`、`MultiInstance` 以及任何
  无法解析的值都会被拒绝。是机器上唯一的进程、不持有任何锁，或者省略该设置，绝不会被当作授权。
- `ReferenceService:DatabasePath` 必须是绝对的本地普通文件路径。
- `ReferenceService:SqliteStartup:PrepareIfMissing` 默认为 `false`。只有显式的 `true` 才允许
  创建缺失的文件；无法解析的值会被拒绝。

不可用的输入会在任何 provider、文件或 EF 调用之前失败，且失败信息指出设置名，而不是它读到的
值。preparation 调用与等待进程内单实例回合共用一个固定的 5 秒预算；迁移本身与启动总时长没有
任何上界。样例构建自己的 SQLite 连接——非池化、私有缓存、没有管理连接或密码输入——并且 EF
使用 `Mode=ReadWrite`，因此 EF 默认的 `ReadWriteCreate` 绝不可能在 gate 背后创建文件。

gate 随后以固定顺序运行，每一步都失败关闭：部署模式先从捕获的能力声明校验；一次只读观察判定
目标是否存在；除非显式允许 preparation，缺失的目标会中止启动；消费方自己的 scoped executor
检查、至多迁移一次、再检查。只有成功的迁移才允许宿主完成启动——gate 在任何 hosted service
之前运行，因此绝不会有请求在未迁移的数据库上被服务。失败会关闭启动并记录一个固定的结果，
不含路径、连接字符串、provider 消息或异常文本。

检查刻意保守。空的可读数据库可以认领；完整的已知迁移集合加上可读的 workspace 表为当前版本；
严格前缀为待迁移；未知的历史记录被视为更新的 schema；没有历史记录的应用表、缺失的必需表或
不可读的历史都拒绝该数据库，而不是认领或修复它。检查打开自己的只读连接，绝不调用 `Migrate`、
`EnsureCreated` 或 `SaveChanges`。

没有跨进程或跨主机互斥：两个都声明 `SingleInstance` 的进程是本契约无法检测的部署错误。文件
发布与迁移之间没有任何事务性关联——已提交的迁移或已发布的空文件不会被后来的取消撤销，被中断
的数据库也不会自动修复。本样例是消费方示例；生产服务选择自己的迁移策略。

staging 示例在每次显式调用 `RegisterAsync` 时创建一个新 workspace，带一个生成的 ID 和一个
固定的演示显示名。它不是安装工作流，也不是幂等性契约。它要求单个调用方自有的 scoped
context。只有调用方能保存或提交这些 staged 变更；冒烟测试显式应用迁移并演示这一边界，包括
staging 之前的回滚与取消。它们不调用完整的 setup 编排。

身份占位符不会为不可用的外部系统编造未认证成功的故事，不发出凭据、不联系网络服务、也不创建
本地管理员。本样例中不存在本地管理员实体或预配路径。

配置管理事务审计仍在
[#177](https://github.com/philfanzhou/ServiceMantle/issues/177)，首次安装事务审计在
[#175](https://github.com/philfanzhou/ServiceMantle/issues/175)，遥测在
[#158](https://github.com/philfanzhou/ServiceMantle/issues/158)，Consul 在
[#159](https://github.com/philfanzhou/ServiceMantle/issues/159)。本骨架对 TLS、部署、反向
代理、生产安全加固、API 兼容性、最终管理路由或多实例 E2E 行为不做任何保证。它的启动状态不是
健康/就绪声明。

## 日志安全接线

`ReferenceService:Logging:Enabled` 是显式布尔开关，默认为 `false`。缺失或无法解析的值会使
ServiceMantle Serilog 宿主、敏感 Header 注册表与请求日志保持未注册；该值在 `Build` 之前固定，
绝不重载。开关关闭时宿主仍然启动、服务 `GET /`、正常停止与释放。

```bash
dotnet run --project samples/ServiceMantle.ReferenceService --   --ReferenceService:Logging:Enabled true --urls http://127.0.0.1:5080
```

打开时，样例注册既有的 `ServiceMantle.Serilog` Console 管道及其强制的结构化清理，通过
`AddSensitiveHeaders` 把样例自有的 `X-Reference-Secret` 加入内置拒绝 Header 名，并在
Problem Details 之外组合 Correlation ID，使一个标识符富化整个下游 scope。它唯一的请求日志行
只携带一个固定的消息模板、一个有界的结果分类（`success`、`client_error`、`server_error`、
`other`、`cancelled`、`faulted`）、状态码、收敛到框架已知 token 集合的请求方法（其他任何值
变成 `(other)`）、匹配的路由模式，以及由 DI 拥有的 `RequestHeaderDiagnosticProjector` 产生的
Header 图。原始路径、查询、body、连接设置或异常细节都不会被记录，也没有注册第二个清理器。

Header 值遵循库契约而不是允许列表：内置拒绝 Header 与样例自有的 `X-Reference-Secret` 被
redaction 标记整体替换，而拒绝列表之外的 Header——`User-Agent`、`Referer`、`X-Forwarded-For`
以及样例从未声明的任何调用方 Header——的值按
[结构化日志安全契约](../../LOGGING_SECURITY.md)的自由文本规则投影，因此确实会进入日志行。
只有该契约识别的形状才会在那里被 redact。通过 `AddSensitiveHeaders` 添加 Header 名可以把它的
值挡在外面。调用方取消保持为取消，绝不会被吞掉。phase gate、健康 endpoint、管理路由与速率
限制在这里保持未接线；基础遥测有自己的开关，见下文，两个开关互不改变。

安全边界就是[结构化日志安全契约](../../LOGGING_SECURITY.md)中记录的那个。被拒绝的结构化字段
名、被拒绝的 Header、受支持的敏感值类型以及显式识别的自由文本形状会被 redact；未标注的不透明
秘密、从不经过清理器或 projector 的值、第三方 provider、任意框架事件与远程系统不在覆盖范围
内。启用日志不创建数据库、不运行迁移、不调用 setup、也不保存消费方的 `DbContext`。测试使用的
失败、拒绝与异常路由由测试在公开的 `Build` 接缝上映射；运行中的样例只保留骨架路由。

## 基础遥测插桩

`ReferenceService:Telemetry:Enabled` 是显式布尔开关，默认为 `false`。只有能解析为 `true` 的
值才注册插桩；缺失、为空或无法解析的值使每个 ServiceMantle 拥有的 OpenTelemetry provider
保持未注册。该值在宿主 builder 创建之前读取，在 `Build` 之前固定，绝不重载。

```bash
dotnet run --project samples/ServiceMantle.ReferenceService -- \
  --ReferenceService:Telemetry:Enabled true --urls http://127.0.0.1:5080
```

打开时，样例调用 `AddOpenTelemetryInstrumentation`，此外什么都不做。这正是公开的
`ServiceMantle.OpenTelemetry` 包已经交付的固定集合：ASP.NET Core 请求跟踪、`HttpClient`
跟踪与 .NET 运行时指标。样例不在其上添加自己的选项系统。OpenTelemetry resource 恰好是
`AddServiceMantle` 已注册的身份——服务名、服务版本与实例 ID——因此样例不贡献任何属性、任何
高基数维度，也不读取任何 Header、body、查询或连接字段。

这个开关**不**做的事：不接 OTLP exporter、不接 Prometheus endpoint、不接 `ServiceMetrics`、
不接健康 endpoint、不接任何服务或安装阶段指标，也不伪造阶段。开关打开时 `/metrics`、
`/health` 与 `/management` 仍返回 404。这里不创建任何远程导出目标，因此没有注册 exporter 时
收集到的信号无处可去。那些能力仍属于
[#158](https://github.com/philfanzhou/ServiceMantle/issues/158) 及拥有它们的任务。

样例现在对 `ServiceMantle.OpenTelemetry` 及其插桩包是**静态**依赖：无论开关是否打开，它们都
在构建输出中。关闭开关会阻止 ServiceMantle 拥有的 provider 与 listener 被注册；它不会从发布
输出中移除那些程序集，也不构成 .NET 进程不运行任何线程、定时器或 socket 的主张。

验收矩阵以及本接线不保证的事项完整清单，见
[`docs/testing/reference-telemetry.md`](../../docs/testing/reference-telemetry.md)。

```bash
dotnet test --project tests/ServiceMantle.ReferenceService.Tests -c Release
```
