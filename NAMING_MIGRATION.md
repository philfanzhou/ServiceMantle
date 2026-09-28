# 命名迁移说明

ServiceMantle 现在由目录和 namespace 表达一个类型属于哪个模块，由类型名表达它做什么。普通类型和
文件名里不再重复产品名。

对每一个被改名的类型，这都是源码与二进制破坏性变更。除此之外没有任何变化：签名、默认值、运行时
行为、包边界、程序集名和包 ID 都不变。已发布的版本不会被覆盖，改名在后续新版本中交付，本文完整
列出全部变更。

## 规则

1. 类型的 namespace 按能力归属组织：能力 namespace 本身归核心包所有，适配包的自有类型放在对应
   能力 namespace 的子空间（`ServiceMantle.Logging` 属核心包，`ServiceMantle.Logging.Pipeline`
   属 Serilog 适配包），框架扩展入口除外（见第 2 条）。普通类型不带产品前缀，因为 namespace
   已经表达了产品与能力。本条按 [#570](https://github.com/philfanzhou/ServiceMantle/issues/570)
   固定的能力命名空间政策修订；本文此前条目按当时「项目根 namespace 加功能子目录」的规则交付，
   历史映射保持原样，#570 各迁移切片的映射随后续切片在本文件增补。
2. 扩展入口保留它们的框架 namespace（`Microsoft.Extensions.DependencyInjection`、
   `Microsoft.Extensions.Hosting`、`Microsoft.AspNetCore.*`），并且类名与方法名都保留产品前缀——
   `AddServiceMantle*`、`UseServiceMantle*`、`MapServiceMantle*`、`WithServiceMantle*`。这些
   namespace 不含任何产品信息，名字是把 ServiceMantle 的入口与框架自带 API 区分开的唯一手段。
3. 文件按其主类型命名。属于同一契约族的公开类型仍然放在该族的文件里，与改名前一致。

## 例外及其理由

| 类型 | 保留或改为 | 理由 |
| --- | --- | --- |
| `ServiceMantle.AspNetCore.ServiceMantleBuilder` | 不变 | 它是 `AddServiceMantle` 的返回类型，也是每个 `AddServiceMantle*` 扩展挂靠的接收者，因此与那些入口一样以产品命名。另外 `Builder` 会与 `Microsoft.AspNetCore.Builder` namespace 撞名。 |
| `ServiceMantleHeaderNames` | `ServiceHeaderNames` | `HeaderNames` 与 `Microsoft.Net.Http.Headers.HeaderNames` 撞名。`Service*` 是本仓库既有的「本宿主服务的 X」命名族（`ServiceLogContext`、`ServiceLogFieldNames`、`ServiceHealthSnapshot`）。 |
| `ServiceMantleMetrics` | `ServiceMetrics` | `Metrics` 与 `System.Diagnostics.Metrics` namespace 撞名。 |
| `ServiceMantleForwardedHeadersOptions` | `ForwardedHeadersTrustOptions` | `ForwardedHeadersOptions` 与 `Microsoft.AspNetCore.Builder.ForwardedHeadersOptions` 撞名，而 Web 应用会隐式 using 那个 namespace。新名字直接说明职责：显式的信任边界。因为两个名字只差一个词，文档里原本就指向框架类型的 `ForwardedHeadersOptions` 已逐处核对，保持原样未改名——例如 `README.md` 里描述 `ForwardedHeadersSnapshotProvider` 交给框架中间件的那个实例。 |
| `IServiceMantleDbContext` | `IServiceDbContext` | `IDbContext` 是消费方应用常见的自定义名字。 |
| `ServiceMantleDataProtectionBuilderExtensions` | `EfCoreDataProtectionExtensions` | `DataProtectionBuilderExtensions` 与 `Microsoft.AspNetCore.DataProtection.DataProtectionBuilderExtensions` 撞名，而调用方要拿到 `IDataProtectionBuilder` 就必须 `using Microsoft.AspNetCore.DataProtection;`，显式写类名的调用会得到 CS0104。新名字沿用同目录 `EfCoreDataProtectionKeyRepository` 的 `EfCore*` 命名族，说明这组扩展把密钥环落到哪里。方法名 `PersistKeysToServiceMantleEfCore` 不变。 |
| `ServiceMantleRegistration` | `HostRegistration` | 单独一个 `Registration` 什么也没说明；这个 record 记录的是 `AddServiceMantle` 固定下来的宿主身份。 |
| `ServiceMantleSerilogLoggerProvider` | `RuntimeLoggerProvider` | `SerilogLoggerProvider` 与它所包装的 `Serilog.Extensions.Logging.SerilogLoggerProvider` 撞名。 |
| `IServiceMantleStructuredLogSanitizer` / `ServiceMantleStructuredLogSanitizer` | `ILogFieldSanitizer` / `LogFieldSanitizer` | `StructuredLogSanitizer` 是它们所适配的核心类型。 |

## 没有变化的部分

以下都是外部契约。类型改名不得移动它们，本次也确实一个都没有移动：

- 日志分类字符串 `ServiceMantle.Http.CorrelationId`、`ServiceMantle.Http.ProblemDetails`、
  `ServiceMantle.Http.RateLimiting`。
- 认证方案与策略名 `ServiceMantle.ManagementCookie`、`ServiceMantle.ManagementAdmin`、
  `ServiceMantle.ManagementSession`。
- Data Protection purpose `ServiceMantle.Management:<serviceId>`。
- meter 名 `ServiceMantle`、它的版本号，以及全部指标名。
- OTLP 配置节名 `ServiceMantle.Otlp.Traces` 与 `ServiceMantle.Otlp.Metrics`。
- 全部 HTTP 路由、Header 名、JSON 字段、错误码、配置键、数据库表与列、迁移锁键前缀、包 ID、
  程序集名和 `InternalsVisibleTo` 目标。

有两处诊断确实以名字指向类型，因此随类型一起变化：对 `HealthStartupValidator` 与
`SensitiveHeaderStartupValidator` 的按名字反射断言。这两个都是程序集内部的注册，不是已发布契约。

## namespace 迁移

| 原 namespace | 新 namespace |
| --- | --- |
| `ServiceMantle.Http` | `ServiceMantle.AspNetCore.Http` |
| `ServiceMantle.AspNetCore`（`Http/` 下的类型） | `ServiceMantle.AspNetCore.Http` |
| `ServiceMantle.AspNetCore`（`Logging/` 下的类型） | `ServiceMantle.AspNetCore.Logging` |
| `ServiceMantle.Logging`（位于 `ServiceMantle.AspNetCore` 的类型） | `ServiceMantle.AspNetCore.Logging` |
| `ServiceMantle.Management`（位于 `ServiceMantle.AspNetCore` 的类型） | `ServiceMantle.AspNetCore.Management`、`ServiceMantle.AspNetCore.ManagementApi[.<分组>]` |
| `ServiceMantle.AspNetCore`（`ManagementApi/` 下的类型） | `ServiceMantle.AspNetCore.ManagementApi[.<分组>]` |
| `ServiceMantle.AspNetCore`（`PhaseGate/` 下的类型） | `ServiceMantle.AspNetCore.PhaseGate` |
| `ServiceMantle.AspNetCore`（`RateLimiting/` 下的类型） | `ServiceMantle.AspNetCore.RateLimiting` |

`ServiceMantle.Logging` 与 `ServiceMantle.Management` 仍然存在：它们是核心包自己的 namespace。
只有原先与它们共用同一 namespace 的 `ServiceMantle.AspNetCore` 类型移走了，因此不再有一个
namespace 跨越两个程序集。

核心包、Consul、各数据库 provider 与 EF Core 持久化包的 namespace 不变。

## 公开 API 映射

| 原完整类型名 | 新完整类型名 |
| --- | --- |
| `Microsoft.Extensions.DependencyInjection.ServiceSettingSnapshotServiceCollectionExtensions` | `Microsoft.Extensions.DependencyInjection.ServiceMantleSettingSnapshotServiceCollectionExtensions` |
| `ServiceMantle.AspNetCore.Health.ServiceMantleHealthOptions` | `ServiceMantle.AspNetCore.Health.HealthOptions` |
| `ServiceMantle.AspNetCore.ServiceMantleForwardedHeadersConfigurationException` | `ServiceMantle.AspNetCore.Http.ForwardedHeadersConfigurationException` |
| `ServiceMantle.AspNetCore.ServiceMantleForwardedHeadersOptions` | `ServiceMantle.AspNetCore.Http.ForwardedHeadersTrustOptions` |
| `ServiceMantle.Http.ServiceMantleProblemDetailsDefaults` | `ServiceMantle.AspNetCore.Http.ProblemDetailsDefaults` |
| `ServiceMantle.AspNetCore.ServiceMantleSecurityResponseHeadersMetadata` | `ServiceMantle.AspNetCore.Http.SecurityResponseHeadersMetadata` |
| `ServiceMantle.Http.ServiceMantleHeaderNames` | `ServiceMantle.AspNetCore.Http.ServiceHeaderNames` |
| `ServiceMantle.AspNetCore.ServiceMantleRequestHeaderDiagnosticProjector` | `ServiceMantle.AspNetCore.Logging.RequestHeaderDiagnosticProjector` |
| `ServiceMantle.AspNetCore.ServiceMantleSensitiveHeaderConfigurationException` | `ServiceMantle.AspNetCore.Logging.SensitiveHeaderConfigurationException` |
| `ServiceMantle.AspNetCore.ServiceMantleSensitiveHeaderRegistry` | `ServiceMantle.AspNetCore.Logging.SensitiveHeaderRegistry` |
| `ServiceMantle.AspNetCore.ServiceMantleSensitiveHeadersOptions` | `ServiceMantle.AspNetCore.Logging.SensitiveHeadersOptions` |
| `ServiceMantle.Management.ServiceMantleManagementCookieOptions` | `ServiceMantle.AspNetCore.Management.ManagementCookieOptions` |
| `ServiceMantle.Management.ServiceMantleManagementSessionDefaults` | `ServiceMantle.AspNetCore.Management.ManagementSessionDefaults` |
| `ServiceMantle.Management.ServiceMantleManagementApiDefaults` | `ServiceMantle.AspNetCore.ManagementApi.ManagementApiDefaults` |
| `ServiceMantle.Management.ServiceMantleManagementApiOptions` | `ServiceMantle.AspNetCore.ManagementApi.ManagementApiOptions` |
| `ServiceMantle.Management.ServiceMantleManagementApiResults` | `ServiceMantle.AspNetCore.ManagementApi.ManagementApiResults` |
| `ServiceMantle.Management.ServiceMantleManagementEntryDefaults` | `ServiceMantle.AspNetCore.ManagementApi.Entries.ManagementEntryDefaults` |
| `ServiceMantle.Management.ServiceMantleManagementEntryKind` | `ServiceMantle.AspNetCore.ManagementApi.Entries.ManagementEntryKind` |
| `ServiceMantle.Management.ServiceMantleManagementLoginAdapter` | `ServiceMantle.AspNetCore.ManagementApi.Session.ManagementLoginAdapter` |
| `ServiceMantle.Management.ServiceMantleManagementSessionOptions` | `ServiceMantle.AspNetCore.ManagementApi.Session.ManagementSessionOptions` |
| `ServiceMantle.Management.ServiceMantleSettingUpdateExecutor` | `ServiceMantle.AspNetCore.ManagementApi.SettingUpdates.SettingUpdateExecutor` |
| `ServiceMantle.Management.ServiceMantleSetupCompletionResult` | `ServiceMantle.AspNetCore.ManagementApi.Setup.SetupCompletionResult` |
| `ServiceMantle.Management.ServiceMantleSetupCompletionStatus` | `ServiceMantle.AspNetCore.ManagementApi.Setup.SetupCompletionStatus` |
| `ServiceMantle.Management.ServiceMantleSetupExecutor` | `ServiceMantle.AspNetCore.ManagementApi.Setup.SetupExecutor` |
| `ServiceMantle.AspNetCore.ServiceMantleManagementSurface` | `ServiceMantle.AspNetCore.PhaseGate.ManagementSurface` |
| `ServiceMantle.AspNetCore.ServiceMantlePhaseGateOptions` | `ServiceMantle.AspNetCore.PhaseGate.PhaseGateOptions` |
| `ServiceMantle.AspNetCore.ServiceMantleRateLimitPolicyOptions` | `ServiceMantle.AspNetCore.RateLimiting.RateLimitPolicyOptions` |
| `ServiceMantle.AspNetCore.ServiceMantleRateLimitingConfigurationException` | `ServiceMantle.AspNetCore.RateLimiting.RateLimitingConfigurationException` |
| `ServiceMantle.AspNetCore.ServiceMantleRateLimitingDefaults` | `ServiceMantle.AspNetCore.RateLimiting.RateLimitingDefaults` |
| `ServiceMantle.AspNetCore.ServiceMantleRateLimitingOptions` | `ServiceMantle.AspNetCore.RateLimiting.RateLimitingOptions` |
| `ServiceMantle.Database.Sqlite.ServiceMantleSqlitePackage` | `ServiceMantle.Database.Sqlite.SqlitePackage` |
| `ServiceMantle.Consul.ConsulLifecycleOptions` | `ServiceMantle.Discovery.ServiceRegistrationLifecycleOptions` |
| `ServiceMantle.OpenTelemetry.ServiceMantleOpenTelemetryOptions` | `ServiceMantle.OpenTelemetry.OpenTelemetryOptions` |
| `ServiceMantle.OpenTelemetry.ServiceMantleMetrics` | `ServiceMantle.OpenTelemetry.ServiceMetrics` |
| `ServiceMantle.OpenTelemetry.ServiceMetrics` | `ServiceMantle.Diagnostics.ServiceMetrics` |
| `ServiceMantle.OpenTelemetry.Otlp.IServiceMantleOtlpAuthenticationHeaderResolver` | `ServiceMantle.OpenTelemetry.Otlp.IOtlpAuthenticationHeaderResolver` |
| `ServiceMantle.OpenTelemetry.Otlp.IOtlpAuthenticationHeaderResolver` | `ServiceMantle.Diagnostics.IRemoteTelemetryAuthenticationResolver` |
| `ServiceMantle.OpenTelemetry.Otlp.ServiceMantleOtlpAuthenticationHeader` | `ServiceMantle.OpenTelemetry.Otlp.OtlpAuthenticationHeader` |
| `ServiceMantle.OpenTelemetry.Otlp.OtlpAuthenticationHeader` | `ServiceMantle.Diagnostics.RemoteTelemetryAuthenticationHeader` |
| `ServiceMantle.OpenTelemetry.Otlp.ServiceMantleOtlpConfigurationException` | `ServiceMantle.OpenTelemetry.Otlp.OtlpConfigurationException` |
| `ServiceMantle.OpenTelemetry.Otlp.ServiceMantleOtlpMetricOptions` | `ServiceMantle.OpenTelemetry.Otlp.OtlpMetricOptions` |
| `ServiceMantle.OpenTelemetry.Otlp.ServiceMantleOtlpOptions` | `ServiceMantle.OpenTelemetry.Otlp.OtlpOptions` |
| `ServiceMantle.OpenTelemetry.Otlp.ServiceMantleOtlpProtocol` | `ServiceMantle.OpenTelemetry.Otlp.OtlpProtocol` |
| `ServiceMantle.OpenTelemetry.Otlp.ServiceMantleOtlpSignalOptions` | `ServiceMantle.OpenTelemetry.Otlp.OtlpSignalOptions` |
| `ServiceMantle.OpenTelemetry.Otlp.ServiceMantleOtlpTraceOptions` | `ServiceMantle.OpenTelemetry.Otlp.OtlpTraceOptions` |
| `ServiceMantle.OpenTelemetry.Otlp.WellKnownServiceMantleOtlpErrorCodes` | `ServiceMantle.OpenTelemetry.Otlp.WellKnownOtlpErrorCodes` |
| `ServiceMantle.OpenTelemetry.Prometheus.ServiceMantlePrometheusConfigurationException` | `ServiceMantle.OpenTelemetry.Prometheus.PrometheusConfigurationException` |
| `ServiceMantle.OpenTelemetry.Prometheus.ServiceMantlePrometheusDefaults` | `ServiceMantle.OpenTelemetry.Prometheus.PrometheusDefaults` |
| `ServiceMantle.OpenTelemetry.Prometheus.ServiceMantlePrometheusOptions` | `ServiceMantle.OpenTelemetry.Prometheus.PrometheusOptions` |
| `ServiceMantle.OpenTelemetry.Prometheus.WellKnownServiceMantlePrometheusErrorCodes` | `ServiceMantle.OpenTelemetry.Prometheus.WellKnownPrometheusErrorCodes` |
| `ServiceMantle.Persistence.EntityFrameworkCore.ServiceMantleDataProtectionBuilderExtensions` | `ServiceMantle.Persistence.EntityFrameworkCore.EfCoreDataProtectionExtensions` |
| `ServiceMantle.Persistence.EntityFrameworkCore.IServiceMantleDbContext` | `ServiceMantle.Persistence.EntityFrameworkCore.IServiceDbContext` |
| `ServiceMantle.Serilog.ServiceMantleSerilogConfigurationException` | `ServiceMantle.Serilog.SerilogConfigurationException` |
| `ServiceMantle.Serilog.ServiceMantleSerilogDefaults` | `ServiceMantle.Serilog.SerilogDefaults` |
| `ServiceMantle.Serilog.ServiceMantleSerilogOptions` | `ServiceMantle.Serilog.SerilogOptions` |
| `ServiceMantle.Serilog.ServiceMantleSerilogPackage` | `ServiceMantle.Serilog.SerilogPackage` |
| `ServiceMantle.Serilog.GrafanaLoki.ServiceMantleGrafanaLokiDefaults` | `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiDefaults` |
| `ServiceMantle.Serilog.GrafanaLoki.ServiceMantleGrafanaLokiDiagnostics` | `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiDiagnostics` |
| `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiDiagnostics` | `ServiceMantle.Logging.RemoteLogDeliveryDiagnostics` |
| `ServiceMantle.Serilog.GrafanaLoki.ServiceMantleGrafanaLokiOptions` | `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiOptions` |
| `ServiceMantle.Serilog.GrafanaLoki.IServiceMantleLokiAuthorizationHeaderResolver` | `ServiceMantle.Serilog.GrafanaLoki.ILokiAuthorizationHeaderResolver` |
| `ServiceMantle.Serilog.GrafanaLoki.ILokiAuthorizationHeaderResolver` | `ServiceMantle.Logging.IRemoteLogAuthorizationResolver` |
| `ServiceMantle.Serilog.GrafanaLoki.WellKnownServiceMantleGrafanaLokiErrorCodes` | `ServiceMantle.Serilog.GrafanaLoki.WellKnownGrafanaLokiErrorCodes` |

## 成员级变更

`SerilogOptions` 的两个成员在类型迁移之外改用了 MEL 词汇。成员的属性类型变更同样是源码与二进制破坏性变更，随新版本交付：

| 原成员 | 新成员 |
| --- | --- |
| `SerilogOptions.MinimumLevel`（`string`，Serilog 级别名，默认 `"Information"`） | `SerilogOptions.MinimumLevel`（`Microsoft.Extensions.Logging.LogLevel`，默认 `LogLevel.Information`） |
| `SerilogOptions.EnricherNames`（`IEnumerable<string>`，仅支持 `FromLogContext`） | `SerilogOptions.IncludeScopes`（`bool`，默认 `true`，语义即 MEL scope 传播） |
| `SerilogDefaults.MinimumLevel`（`const string`） | `SerilogDefaults.MinimumLevel`（`const LogLevel`） |
| `SerilogDefaults.EnricherNames` | 移除；由 `SerilogDefaults.IncludeScopes`（`const bool`）取代 |

`LogLevel` 到 Serilog 级别的映射固定为：`Trace`→`Verbose`、`Debug`→`Debug`、`Information`→`Information`、`Warning`→`Warning`、`Error`→`Error`、`Critical`→`Fatal`；该映射在两端边界值上不是双射。`LogLevel.None` 与未定义的枚举值不被接受，仍以 `serilog.minimum_level_invalid` 在 Host 启动前失败。默认有效行为不变：默认最低级别仍对应 Information，scope 传播默认启用。原来的 `serilog.enricher_names_invalid` 错误码随 `EnricherNames` 的移除不再发出；其余 `serilog.*` 错误码取值不变。`OutputTemplate` 保持 Serilog 模板语法不变，其 XML 文档已声明它是 sink 实现相关的逃生舱，更换 sink 实现时不保证兼容。

## 内部类型映射

以下都是程序集内部类型。之所以列出，是因为 `InternalsVisibleTo` 的测试程序集和按名字反射的诊断
会看到它们。

| 原完整类型名 | 新完整类型名 |
| --- | --- |
| `ServiceMantle.AspNetCore.ServiceMantleRegistration` | `ServiceMantle.AspNetCore.HostRegistration` |
| `ServiceMantle.AspNetCore.Health.ServiceMantleHealthRegistration` | `ServiceMantle.AspNetCore.Health.HealthRegistration` |
| `ServiceMantle.AspNetCore.Health.ServiceMantleHealthStartupValidator` | `ServiceMantle.AspNetCore.Health.HealthStartupValidator` |
| `ServiceMantle.AspNetCore.Health.ServiceMantleReadinessDecisionSource` | `ServiceMantle.AspNetCore.Health.ReadinessDecisionSource` |
| `ServiceMantle.Http.ServiceMantleCorrelationIdMiddleware` | `ServiceMantle.AspNetCore.Http.CorrelationIdMiddleware` |
| `ServiceMantle.Http.ServiceMantleExceptionMapping` | `ServiceMantle.AspNetCore.Http.ExceptionMapping` |
| `ServiceMantle.Http.ServiceMantleExceptionMappingRegistration` | `ServiceMantle.AspNetCore.Http.ExceptionMappingRegistration` |
| `ServiceMantle.Http.ServiceMantleExceptionMappingRegistry` | `ServiceMantle.AspNetCore.Http.ExceptionMappingRegistry` |
| `ServiceMantle.AspNetCore.ServiceMantleForwardedHeadersMiddleware` | `ServiceMantle.AspNetCore.Http.ForwardedHeadersMiddleware` |
| `ServiceMantle.AspNetCore.ServiceMantleForwardedHeadersRegistration` | `ServiceMantle.AspNetCore.Http.ForwardedHeadersRegistration` |
| `ServiceMantle.AspNetCore.ServiceMantleForwardedHeadersSnapshotProvider` | `ServiceMantle.AspNetCore.Http.ForwardedHeadersSnapshotProvider` |
| `ServiceMantle.AspNetCore.ServiceMantleForwardedHeadersStartupValidator` | `ServiceMantle.AspNetCore.Http.ForwardedHeadersStartupValidator` |
| `ServiceMantle.Http.IServiceMantleExceptionMappingRegistration` | `ServiceMantle.AspNetCore.Http.IExceptionMappingRegistration` |
| `ServiceMantle.AspNetCore.ServiceMantlePipelineComposition` | `ServiceMantle.AspNetCore.Http.PipelineComposition` |
| `ServiceMantle.Http.ServiceMantleProblemDetailsMiddleware` | `ServiceMantle.AspNetCore.Http.ProblemDetailsMiddleware` |
| `ServiceMantle.Http.ServiceMantleProblemDetailsStartupValidator` | `ServiceMantle.AspNetCore.Http.ProblemDetailsStartupValidator` |
| `ServiceMantle.Http.ServiceMantleProblemExtensionFactory` | `ServiceMantle.AspNetCore.Http.ProblemExtensionFactory` |
| `ServiceMantle.Http.ServiceMantleProblemValue` | `ServiceMantle.AspNetCore.Http.ProblemValue` |
| `ServiceMantle.AspNetCore.ServiceMantleSecurityResponseHeadersMiddleware` | `ServiceMantle.AspNetCore.Http.SecurityResponseHeadersMiddleware` |
| `ServiceMantle.AspNetCore.ServiceMantleSecurityResponseHeadersRegistration` | `ServiceMantle.AspNetCore.Http.SecurityResponseHeadersRegistration` |
| `ServiceMantle.AspNetCore.ServiceMantleSensitiveHeaderRegistration` | `ServiceMantle.AspNetCore.Logging.SensitiveHeaderRegistration` |
| `ServiceMantle.AspNetCore.ServiceMantleSensitiveHeaderSanitizer` | `ServiceMantle.AspNetCore.Logging.SensitiveHeaderSanitizer` |
| `ServiceMantle.AspNetCore.ServiceMantleSensitiveHeaderStartupValidator` | `ServiceMantle.AspNetCore.Logging.SensitiveHeaderStartupValidator` |
| `ServiceMantle.Management.ServiceMantleManagementCookieEvents` | `ServiceMantle.AspNetCore.Management.ManagementCookieEvents` |
| `ServiceMantle.Management.ServiceMantleManagementCookieRegistration` | `ServiceMantle.AspNetCore.Management.ManagementCookieRegistration` |
| `ServiceMantle.Management.ServiceMantleManagementCookieStartupValidator` | `ServiceMantle.AspNetCore.Management.ManagementCookieStartupValidator` |
| `ServiceMantle.AspNetCore.ServiceMantleManagementApiMetadata` | `ServiceMantle.AspNetCore.ManagementApi.ManagementApiMetadata` |
| `ServiceMantle.Management.ServiceMantleManagementApiProblemResult` | `ServiceMantle.AspNetCore.ManagementApi.ManagementApiProblemResult` |
| `ServiceMantle.AspNetCore.ServiceMantleManagementApiRegistration` | `ServiceMantle.AspNetCore.ManagementApi.ManagementApiRegistration` |
| `ServiceMantle.AspNetCore.ServiceMantleManagementApiStartupValidator` | `ServiceMantle.AspNetCore.ManagementApi.ManagementApiStartupValidator` |
| `ServiceMantle.AspNetCore.ServiceMantleManagementApiState` | `ServiceMantle.AspNetCore.ManagementApi.ManagementApiState` |
| `ServiceMantle.AspNetCore.ServiceMantleAuditQueryHandlers` | `ServiceMantle.AspNetCore.ManagementApi.AuditQueries.AuditQueryHandlers` |
| `ServiceMantle.AspNetCore.ServiceMantleAuditQueryMapping` | `ServiceMantle.AspNetCore.ManagementApi.AuditQueries.AuditQueryMapping` |
| `ServiceMantle.AspNetCore.ServiceMantleAuditQueryResult` | `ServiceMantle.AspNetCore.ManagementApi.AuditQueries.AuditQueryResult` |
| `ServiceMantle.AspNetCore.ServiceMantleBootstrapHandlers` | `ServiceMantle.AspNetCore.ManagementApi.Bootstrap.BootstrapHandlers` |
| `ServiceMantle.AspNetCore.ServiceMantleBootstrapManagementRegistration` | `ServiceMantle.AspNetCore.ManagementApi.Bootstrap.BootstrapManagementRegistration` |
| `ServiceMantle.AspNetCore.ServiceMantleBootstrapManagementStartupValidator` | `ServiceMantle.AspNetCore.ManagementApi.Bootstrap.BootstrapManagementStartupValidator` |
| `ServiceMantle.AspNetCore.ServiceMantleBootstrapMapping` | `ServiceMantle.AspNetCore.ManagementApi.Bootstrap.BootstrapMapping` |
| `ServiceMantle.AspNetCore.ServiceMantleBootstrapRequest` | `ServiceMantle.AspNetCore.ManagementApi.Bootstrap.BootstrapRequest` |
| `ServiceMantle.AspNetCore.ServiceMantleBootstrapRequestParser` | `ServiceMantle.AspNetCore.ManagementApi.Bootstrap.BootstrapRequestParser` |
| `ServiceMantle.AspNetCore.ServiceMantleBootstrapResult` | `ServiceMantle.AspNetCore.ManagementApi.Bootstrap.BootstrapResult` |
| `ServiceMantle.Management.ServiceMantleManagementEntryDefinition` | `ServiceMantle.AspNetCore.ManagementApi.Entries.ManagementEntryDefinition` |
| `ServiceMantle.Management.ServiceMantleManagementEntryMetadata` | `ServiceMantle.AspNetCore.ManagementApi.Entries.ManagementEntryMetadata` |
| `ServiceMantle.AspNetCore.ServiceMantleManagementEntryStartupValidator` | `ServiceMantle.AspNetCore.ManagementApi.Entries.ManagementEntryStartupValidator` |
| `ServiceMantle.AspNetCore.ServiceMantleManagementEntryState` | `ServiceMantle.AspNetCore.ManagementApi.Entries.ManagementEntryState` |
| `ServiceMantle.AspNetCore.ServiceMantleUnsafeRequestFilter` | `ServiceMantle.AspNetCore.ManagementApi.Entries.UnsafeRequestFilter` |
| `ServiceMantle.Management.ServiceMantleUnsafeRequestGuardMetadata` | `ServiceMantle.AspNetCore.ManagementApi.Entries.UnsafeRequestGuardMetadata` |
| `ServiceMantle.AspNetCore.ServiceMantleRuntimeInfoMapping` | `ServiceMantle.AspNetCore.ManagementApi.RuntimeInfo.RuntimeInfoMapping` |
| `ServiceMantle.AspNetCore.ServiceMantleRuntimeInfoResult` | `ServiceMantle.AspNetCore.ManagementApi.RuntimeInfo.RuntimeInfoResult` |
| `ServiceMantle.AspNetCore.ServiceMantleManagementSessionBodyAdmission` | `ServiceMantle.AspNetCore.ManagementApi.Session.ManagementSessionBodyAdmission` |
| `ServiceMantle.AspNetCore.ServiceMantleManagementSessionBodyStatus` | `ServiceMantle.AspNetCore.ManagementApi.Session.ManagementSessionBodyStatus` |
| `ServiceMantle.AspNetCore.ServiceMantleManagementSessionHandlers` | `ServiceMantle.AspNetCore.ManagementApi.Session.ManagementSessionHandlers` |
| `ServiceMantle.AspNetCore.ServiceMantleManagementSessionMapping` | `ServiceMantle.AspNetCore.ManagementApi.Session.ManagementSessionMapping` |
| `ServiceMantle.AspNetCore.ServiceMantleManagementSessionResult` | `ServiceMantle.AspNetCore.ManagementApi.Session.ManagementSessionResult` |
| `ServiceMantle.AspNetCore.ServiceMantleSettingQueryHandlers` | `ServiceMantle.AspNetCore.ManagementApi.SettingQueries.SettingQueryHandlers` |
| `ServiceMantle.AspNetCore.ServiceMantleSettingQueryMapping` | `ServiceMantle.AspNetCore.ManagementApi.SettingQueries.SettingQueryMapping` |
| `ServiceMantle.AspNetCore.ServiceMantleSettingQueryResult` | `ServiceMantle.AspNetCore.ManagementApi.SettingQueries.SettingQueryResult` |
| `ServiceMantle.AspNetCore.ServiceMantleSettingUpdateHandlers` | `ServiceMantle.AspNetCore.ManagementApi.SettingUpdates.SettingUpdateHandlers` |
| `ServiceMantle.AspNetCore.ServiceMantleSettingUpdateMapping` | `ServiceMantle.AspNetCore.ManagementApi.SettingUpdates.SettingUpdateMapping` |
| `ServiceMantle.AspNetCore.ServiceMantleSettingUpdateRequestParser` | `ServiceMantle.AspNetCore.ManagementApi.SettingUpdates.SettingUpdateRequestParser` |
| `ServiceMantle.AspNetCore.ServiceMantleSettingUpdateResult` | `ServiceMantle.AspNetCore.ManagementApi.SettingUpdates.SettingUpdateResult` |
| `ServiceMantle.AspNetCore.ServiceMantleSetupHandlers` | `ServiceMantle.AspNetCore.ManagementApi.Setup.SetupHandlers` |
| `ServiceMantle.AspNetCore.ServiceMantleSetupMapping` | `ServiceMantle.AspNetCore.ManagementApi.Setup.SetupMapping` |
| `ServiceMantle.AspNetCore.ServiceMantleSetupRequestParser` | `ServiceMantle.AspNetCore.ManagementApi.Setup.SetupRequestParser` |
| `ServiceMantle.AspNetCore.ServiceMantleSetupResult` | `ServiceMantle.AspNetCore.ManagementApi.Setup.SetupResult` |
| `ServiceMantle.AspNetCore.ServiceMantleBootstrapRestartLatch` | `ServiceMantle.AspNetCore.ManagementApi.Status.BootstrapRestartLatch` |
| `ServiceMantle.AspNetCore.ServiceMantleBootstrapStatusReader` | `ServiceMantle.AspNetCore.ManagementApi.Status.BootstrapStatusReader` |
| `ServiceMantle.AspNetCore.IServiceMantleBootstrapStatusReader` | `ServiceMantle.AspNetCore.ManagementApi.Status.IBootstrapStatusReader` |
| `ServiceMantle.AspNetCore.ServiceMantleInstallationStatusHandler` | `ServiceMantle.AspNetCore.ManagementApi.Status.InstallationStatusHandler` |
| `ServiceMantle.AspNetCore.ServiceMantleInstallationStatusMapping` | `ServiceMantle.AspNetCore.ManagementApi.Status.InstallationStatusMapping` |
| `ServiceMantle.AspNetCore.ServiceMantleInstallationStatusResult` | `ServiceMantle.AspNetCore.ManagementApi.Status.InstallationStatusResult` |
| `ServiceMantle.AspNetCore.ServiceMantleManagementSurfaceMetadata` | `ServiceMantle.AspNetCore.PhaseGate.ManagementSurfaceMetadata` |
| `ServiceMantle.AspNetCore.ServiceMantlePhaseGateMiddleware` | `ServiceMantle.AspNetCore.PhaseGate.PhaseGateMiddleware` |
| `ServiceMantle.AspNetCore.ServiceMantlePhaseGateRegistration` | `ServiceMantle.AspNetCore.PhaseGate.PhaseGateRegistration` |
| `ServiceMantle.AspNetCore.ServiceMantlePhaseGateStartupValidator` | `ServiceMantle.AspNetCore.PhaseGate.PhaseGateStartupValidator` |
| `ServiceMantle.AspNetCore.ServiceMantlePhaseGateState` | `ServiceMantle.AspNetCore.PhaseGate.PhaseGateState` |
| `ServiceMantle.AspNetCore.ServiceMantlePhaseHealthMetadata` | `ServiceMantle.AspNetCore.PhaseGate.PhaseHealthMetadata` |
| `ServiceMantle.AspNetCore.ServiceMantleRateLimitPolicySnapshot` | `ServiceMantle.AspNetCore.RateLimiting.RateLimitPolicySnapshot` |
| `ServiceMantle.AspNetCore.ServiceMantleRateLimitingPolicy` | `ServiceMantle.AspNetCore.RateLimiting.RateLimitingPolicy` |
| `ServiceMantle.AspNetCore.ServiceMantleRateLimitingRegistration` | `ServiceMantle.AspNetCore.RateLimiting.RateLimitingRegistration` |
| `ServiceMantle.AspNetCore.ServiceMantleRateLimitingSnapshot` | `ServiceMantle.AspNetCore.RateLimiting.RateLimitingSnapshot` |
| `ServiceMantle.AspNetCore.ServiceMantleRateLimitingSnapshotProvider` | `ServiceMantle.AspNetCore.RateLimiting.RateLimitingSnapshotProvider` |
| `ServiceMantle.AspNetCore.ServiceMantleRateLimitingStartupValidator` | `ServiceMantle.AspNetCore.RateLimiting.RateLimitingStartupValidator` |
| `ServiceMantle.OpenTelemetry.ServiceMantleOpenTelemetryRegistration` | `ServiceMantle.OpenTelemetry.OpenTelemetryRegistration` |
| `ServiceMantle.OpenTelemetry.ServiceMantleOpenTelemetryRegistrationValidator` | `ServiceMantle.OpenTelemetry.OpenTelemetryRegistrationValidator` |
| `ServiceMantle.OpenTelemetry.Otlp.ServiceMantleOtlpNames` | `ServiceMantle.OpenTelemetry.Otlp.OtlpNames` |
| `ServiceMantle.OpenTelemetry.Otlp.ServiceMantleOtlpOptionsConfigurator` | `ServiceMantle.OpenTelemetry.Otlp.OtlpOptionsConfigurator` |
| `ServiceMantle.OpenTelemetry.Otlp.ServiceMantleOtlpRegistration` | `ServiceMantle.OpenTelemetry.Otlp.OtlpRegistration` |
| `ServiceMantle.OpenTelemetry.Otlp.ServiceMantleOtlpRuntime` | `ServiceMantle.OpenTelemetry.Otlp.OtlpRuntime` |
| `ServiceMantle.OpenTelemetry.Otlp.ServiceMantleOtlpSignal` | `ServiceMantle.OpenTelemetry.Otlp.OtlpSignal` |
| `ServiceMantle.OpenTelemetry.Otlp.ServiceMantleOtlpSignalConfiguration` | `ServiceMantle.OpenTelemetry.Otlp.OtlpSignalConfiguration` |
| `ServiceMantle.OpenTelemetry.Otlp.ServiceMantleOtlpSignalRegistration` | `ServiceMantle.OpenTelemetry.Otlp.OtlpSignalRegistration` |
| `ServiceMantle.OpenTelemetry.Otlp.ServiceMantleOtlpStartupValidator` | `ServiceMantle.OpenTelemetry.Otlp.OtlpStartupValidator` |
| `ServiceMantle.OpenTelemetry.Prometheus.ServiceMantlePrometheusEndpointMetadata` | `ServiceMantle.OpenTelemetry.Prometheus.PrometheusEndpointMetadata` |
| `ServiceMantle.OpenTelemetry.Prometheus.ServiceMantlePrometheusEndpointState` | `ServiceMantle.OpenTelemetry.Prometheus.PrometheusEndpointState` |
| `ServiceMantle.OpenTelemetry.Prometheus.ServiceMantlePrometheusExporterOptionsPolicy` | `ServiceMantle.OpenTelemetry.Prometheus.PrometheusExporterOptionsPolicy` |
| `ServiceMantle.OpenTelemetry.Prometheus.ServiceMantlePrometheusRegistration` | `ServiceMantle.OpenTelemetry.Prometheus.PrometheusRegistration` |
| `ServiceMantle.OpenTelemetry.Prometheus.ServiceMantlePrometheusScrapeGate` | `ServiceMantle.OpenTelemetry.Prometheus.PrometheusScrapeGate` |
| `ServiceMantle.OpenTelemetry.Prometheus.ServiceMantlePrometheusSnapshot` | `ServiceMantle.OpenTelemetry.Prometheus.PrometheusSnapshot` |
| `ServiceMantle.OpenTelemetry.Prometheus.ServiceMantlePrometheusSnapshotProvider` | `ServiceMantle.OpenTelemetry.Prometheus.PrometheusSnapshotProvider` |
| `ServiceMantle.OpenTelemetry.Prometheus.ServiceMantlePrometheusStartupValidator` | `ServiceMantle.OpenTelemetry.Prometheus.PrometheusStartupValidator` |
| `ServiceMantle.Serilog.ServiceMantleConsoleSinkFactory` | `ServiceMantle.Serilog.ConsoleSinkFactory` |
| `ServiceMantle.Serilog.IServiceMantleStructuredLogSanitizer` | `ServiceMantle.Serilog.ILogFieldSanitizer` |
| `ServiceMantle.Serilog.IServiceMantleSerilogSinkFactory` | `ServiceMantle.Serilog.ISerilogSinkFactory` |
| `ServiceMantle.Serilog.ServiceMantleStructuredLogSanitizer` | `ServiceMantle.Serilog.LogFieldSanitizer` |
| `ServiceMantle.Serilog.ServiceMantleSanitizingSink` | `ServiceMantle.Serilog.SanitizingSink` |
| `ServiceMantle.Serilog.ServiceMantleSerilogConfiguration` | `ServiceMantle.Serilog.SerilogConfiguration` |
| `ServiceMantle.Serilog.ServiceMantleSerilogLifecycle` | `ServiceMantle.Serilog.SerilogLifecycle` |
| `ServiceMantle.Serilog.ServiceMantleSerilogLoggerProvider` | `ServiceMantle.Serilog.RuntimeLoggerProvider` |
| `ServiceMantle.Serilog.ServiceMantleSerilogMarker` | `ServiceMantle.Serilog.SerilogMarker` |
| `ServiceMantle.Serilog.ServiceMantleSerilogRegistration` | `ServiceMantle.Serilog.SerilogRegistration` |
| `ServiceMantle.Serilog.ServiceMantleSerilogRuntime` | `ServiceMantle.Serilog.SerilogRuntime` |
| `ServiceMantle.Serilog.GrafanaLoki.ServiceMantleGrafanaLokiCompositeSink` | `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiCompositeSink` |
| `ServiceMantle.Serilog.GrafanaLoki.ServiceMantleGrafanaLokiConfiguration` | `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiConfiguration` |
| `ServiceMantle.Serilog.GrafanaLoki.ServiceMantleGrafanaLokiConfigurationProvider` | `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiConfigurationProvider` |
| `ServiceMantle.Serilog.GrafanaLoki.ServiceMantleGrafanaLokiDeliveryCounter` | `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiDeliveryCounter` |
| `ServiceMantle.Serilog.GrafanaLoki.ServiceMantleGrafanaLokiFailureListener` | `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiFailureListener` |
| `ServiceMantle.Serilog.GrafanaLoki.ServiceMantleGrafanaLokiLifecycle` | `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiLifecycle` |
| `ServiceMantle.Serilog.GrafanaLoki.ServiceMantleGrafanaLokiRegistration` | `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiRegistration` |
| `ServiceMantle.Serilog.GrafanaLoki.ServiceMantleGrafanaLokiRemoteSink` | `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiRemoteSink` |
| `ServiceMantle.Serilog.GrafanaLoki.ServiceMantleGrafanaLokiRuntime` | `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiRuntime` |
| `ServiceMantle.Serilog.GrafanaLoki.ServiceMantleGrafanaLokiSinkFactory` | `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiSinkFactory` |
| `ServiceMantle.Serilog.GrafanaLoki.IServiceMantleLokiHttpMessageHandlerFactory` | `ServiceMantle.Serilog.GrafanaLoki.ILokiHttpMessageHandlerFactory` |
| `ServiceMantle.Serilog.GrafanaLoki.ServiceMantleLokiDeliveryException` | `ServiceMantle.Serilog.GrafanaLoki.LokiDeliveryException` |
| `ServiceMantle.Serilog.GrafanaLoki.ServiceMantleLokiHttpMessageHandler` | `ServiceMantle.Serilog.GrafanaLoki.LokiHttpMessageHandler` |
| `ServiceMantle.Serilog.GrafanaLoki.ServiceMantleLokiHttpMessageHandlerFactory` | `ServiceMantle.Serilog.GrafanaLoki.LokiHttpMessageHandlerFactory` |

## Consul 入口补充映射

`ServiceMantle.Consul` 的注册入口与核心包的注册入口同型：它住在框架 namespace 里，类名是唯一的
产品标记，因此按同一条规则补齐前缀。方法体不变；带 configure 参数的重载随后续生命周期时间选项
迁移（见下节）把参数类型换成了 `Action<ServiceRegistrationLifecycleOptions>?`。

| 原完整类型名 | 新完整类型名 |
| --- | --- |
| `Microsoft.Extensions.DependencyInjection.ConsulServiceCollectionExtensions` | `Microsoft.Extensions.DependencyInjection.ServiceMantleConsulServiceCollectionExtensions` |

## 生命周期时间选项迁移（#435）

ADR 0007 判定为 A 类的 `ConsulLifecycleOptions` 迁入核心包，成为 provider 中立的
`ServiceRegistrationLifecycleOptions`（`src/ServiceMantle/Discovery/`）。这是源码与二进制破坏性
变更，只在后续新版本交付，不覆盖历史版本；旧公开类型与旧公开属性不留兼容壳。

- 属性 `ConsulOperationBudget` 同时改名为 `OperationBudget`；其余五个属性名、默认值与允许区间
  不变。调用方若把 options 当作 JSON/配置绑定模型使用，须自行迁移属性名，本仓库不提供旧属性别名。
- 校验仍发生在 Consul 注册入口写入 descriptor 之前：原先 options 上的 internal `Validate()` 移入
  Consul 内部 `ConsulLifecycleSettings.FromOptions(ServiceRegistrationLifecycleOptions)`。核心包 POCO
  只承载数据，不验证、不创建 timer；`ConsulConfigurationException`、内部 settings 与生命周期状态机
  都不进入核心包。内部 `ConsulLifecycleSettings` 的字段名 `ConsulOperationBudget` 保持，映射自新的
  `OperationBudget`。
- 契约族文件改名为 `src/ServiceMantle.Consul/ConsulLifecycleState.cs`；其中的类型名、可见性、成员与
  字符串（诊断码、设置键、HTTP、wire 字段、程序集标识、`InternalsVisibleTo`）全部不变。
- 不变式：单次参数无效仍在注册调用中失败且不写入 descriptor；重复调用参数冲突仍在生命周期首次解析
  时失败。`BuildServiceProvider` / 任意形式的 `Host.Build()` 并不必然解析生命周期，不能把它们当作
  冲突检测点。

## 测试类映射

| 原完整类型名 | 新完整类型名 |
| --- | --- |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleAuditQueryEndpointTests` | `ServiceMantle.AspNetCore.Tests.AuditQueryEndpointTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleBootstrapManagementTests` | `ServiceMantle.AspNetCore.Tests.BootstrapManagementTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleBootstrapRequestTests` | `ServiceMantle.AspNetCore.Tests.BootstrapRequestTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleBootstrapUpdateEntryAuthorizationTests` | `ServiceMantle.AspNetCore.Tests.BootstrapUpdateEntryAuthorizationTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleCorrelationIdTests` | `ServiceMantle.AspNetCore.Tests.CorrelationIdTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleForwardedHeadersTests` | `ServiceMantle.AspNetCore.Tests.ForwardedHeadersTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleHealthCancellationTests` | `ServiceMantle.AspNetCore.Tests.HealthCancellationTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleHealthEndpointTests` | `ServiceMantle.AspNetCore.Tests.HealthEndpointTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleRegistrationTests` | `ServiceMantle.AspNetCore.Tests.HostRegistrationTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleInstallationStatusTests` | `ServiceMantle.AspNetCore.Tests.InstallationStatusTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleManagementApiCancellationTests` | `ServiceMantle.AspNetCore.Tests.ManagementApiCancellationTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleManagementApiTests` | `ServiceMantle.AspNetCore.Tests.ManagementApiTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleManagementEntryTests` | `ServiceMantle.AspNetCore.Tests.ManagementEntryTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleManagementSessionBodyAdmissionTests` | `ServiceMantle.AspNetCore.Tests.ManagementSessionBodyAdmissionTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleManagementSessionCookieRollbackTests` | `ServiceMantle.AspNetCore.Tests.ManagementSessionCookieRollbackTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleManagementSessionEndpointTests` | `ServiceMantle.AspNetCore.Tests.ManagementSessionEndpointTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleManagementSessionOutcomeTests` | `ServiceMantle.AspNetCore.Tests.ManagementSessionOutcomeTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantlePhaseGateCancellationTests` | `ServiceMantle.AspNetCore.Tests.PhaseGateCancellationTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantlePhaseGateTests` | `ServiceMantle.AspNetCore.Tests.PhaseGateTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantlePipelineTests` | `ServiceMantle.AspNetCore.Tests.PipelineTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleProblemDetailsTests` | `ServiceMantle.AspNetCore.Tests.ProblemDetailsTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleRateLimitingTests` | `ServiceMantle.AspNetCore.Tests.RateLimitingTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleRuntimeInfoTests` | `ServiceMantle.AspNetCore.Tests.RuntimeInfoTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleSecurityResponseHeadersTests` | `ServiceMantle.AspNetCore.Tests.SecurityResponseHeadersTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleSensitiveHeaderTests` | `ServiceMantle.AspNetCore.Tests.SensitiveHeaderTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleSettingQueryEndpointTests` | `ServiceMantle.AspNetCore.Tests.SettingQueryEndpointTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleSettingUpdateEndpointTests` | `ServiceMantle.AspNetCore.Tests.SettingUpdateEndpointTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleSetupCancellationPriorityTests` | `ServiceMantle.AspNetCore.Tests.SetupCancellationPriorityTests` |
| `ServiceMantle.AspNetCore.Tests.ServiceMantleSetupEndpointTests` | `ServiceMantle.AspNetCore.Tests.SetupEndpointTests` |
| `ServiceMantle.OpenTelemetry.Otlp.Tests.ServiceMantleOtlpRegistrationTests` | `ServiceMantle.OpenTelemetry.Otlp.Tests.OtlpRegistrationTests` |
| `ServiceMantle.OpenTelemetry.Prometheus.Tests.ServiceMantlePrometheusRegistrationTests` | `ServiceMantle.OpenTelemetry.Prometheus.Tests.PrometheusRegistrationTests` |
| `ServiceMantle.OpenTelemetry.Tests.ServiceMantleOpenTelemetryRegistrationTests` | `ServiceMantle.OpenTelemetry.Tests.OpenTelemetryRegistrationTests` |
| `ServiceMantle.OpenTelemetry.Tests.ServiceMantleOpenTelemetryStartupValidationTests` | `ServiceMantle.OpenTelemetry.Tests.OpenTelemetryStartupValidationTests` |
| `ServiceMantle.OpenTelemetry.Tests.ServiceMantleMetricsTests` | `ServiceMantle.OpenTelemetry.Tests.ServiceMetricsTests` |
| `ServiceMantle.OpenTelemetry.Tests.ServiceMantleTelemetryPipelineTests` | `ServiceMantle.OpenTelemetry.Tests.TelemetryPipelineTests` |
| `ServiceMantle.Serilog.GrafanaLoki.Tests.ServiceMantleGrafanaLokiTests` | `ServiceMantle.Serilog.GrafanaLoki.Tests.GrafanaLokiTests` |
| `ServiceMantle.Serilog.Tests.ServiceMantleCoreOptionalCompositionTests` | `ServiceMantle.Serilog.Tests.CoreOptionalCompositionTests` |
| `ServiceMantle.Serilog.Tests.ServiceMantleSerilogConsoleCollection` | `ServiceMantle.Serilog.Tests.SerilogConsoleCollection` |
| `ServiceMantle.Serilog.Tests.ServiceMantleSerilogHostTests` | `ServiceMantle.Serilog.Tests.SerilogHostTests` |

## 核心契约上移（#572）

[#570](https://github.com/philfanzhou/ServiceMantle/issues/570) 能力命名空间切片之一：与
ASP.NET Core 无关、签名只依赖核心包既有类型的两个公开接口，从 `ServiceMantle.AspNetCore` 包
上移进核心包。这是源码与二进制破坏性变更，只在后续新版本交付，不覆盖历史版本；旧公开类型与
旧文件位置不留兼容壳。

| 原完整类型名 | 新完整类型名 |
| --- | --- |
| `ServiceMantle.AspNetCore.IServiceStartupPhaseResolver` | `ServiceMantle.Installation.IServiceStartupPhaseResolver` |
| `ServiceMantle.AspNetCore.Health.IServiceHealthSnapshotSource` | `ServiceMantle.Health.IServiceHealthSnapshotSource` |

- 接口成员与 XML 文档语义不变。内部默认实现的完整类型名不变（仍为
  `ServiceMantle.AspNetCore.DefaultServiceStartupPhaseResolver`，改放同名独立文件），注册行为仍由
  AspNetCore 包的 `AddServiceMantle` 完成；消费方观察到的 DI 结果不变。
- 核心包 csproj 的依赖与框架引用零变化；诊断码、配置键、日志分类、`InternalsVisibleTo` 等字符串
  契约零变化。

## Consul 能力命名空间迁移（#574）

[#570](https://github.com/philfanzhou/ServiceMantle/issues/570) 能力命名空间切片之一：
`ServiceMantle.Consul` 包的自有 namespace 迁往能力 namespace。包 ID、程序集名
`ServiceMantle.Consul`、依赖与运行时行为不变；`discovery.*` 设置键、诊断码、HTTP 路径与
Header、wire 字段、`InternalsVisibleTo` 全部零变化。这是源码与二进制破坏性变更，只在后续
新版本交付，不覆盖历史版本。

### namespace 迁移

| 原 namespace | 新 namespace |
| --- | --- |
| `ServiceMantle.Consul`（10 个文件：client 契约、client、异常、生命周期状态机、快照绑定） | `ServiceMantle.Discovery.Registration` |
| `ServiceMantle.Consul`（`ConsulSettingDefinitions.cs`，设置目录与校验） | `ServiceMantle.Discovery.Configuration` |

`Microsoft.Extensions.DependencyInjection.ServiceMantleConsulServiceCollectionExtensions` 不变
（框架 namespace 入口，类名保留产品前缀）。目录随 namespace 调整为
`src/ServiceMantle.Consul/Registration/` 与 `src/ServiceMantle.Consul/Configuration/`，
`<RootNamespace>` 改为 `ServiceMantle.Discovery.Registration`。

### 公开 API 映射

| 原完整类型名 | 新完整类型名 |
| --- | --- |
| `ServiceMantle.Consul.ConsulServiceRegistration` | `ServiceMantle.Discovery.Registration.ConsulServiceRegistration` |
| `ServiceMantle.Consul.ConsulClientConfiguration` | `ServiceMantle.Discovery.Registration.ConsulClientConfiguration` |
| `ServiceMantle.Consul.ConsulClientResult` | `ServiceMantle.Discovery.Registration.ConsulClientResult` |
| `ServiceMantle.Consul.IConsulClient` | `ServiceMantle.Discovery.Registration.IConsulClient` |
| `ServiceMantle.Consul.IConsulClientFactory` | `ServiceMantle.Discovery.Registration.IConsulClientFactory` |
| `ServiceMantle.Consul.ConsulClientProvider` | `ServiceMantle.Discovery.Registration.ConsulClientProvider` |
| `ServiceMantle.Consul.ConsulClientSession` | `ServiceMantle.Discovery.Registration.ConsulClientSession` |
| `ServiceMantle.Consul.ConsulConfigurationError` | `ServiceMantle.Discovery.Registration.ConsulConfigurationError` |
| `ServiceMantle.Consul.ConsulConfigurationException` | `ServiceMantle.Discovery.Registration.ConsulConfigurationException` |
| `ServiceMantle.Consul.ConsulHttpClientFactory` | `ServiceMantle.Discovery.Registration.ConsulHttpClientFactory` |
| `ServiceMantle.Consul.ConsulLifecycleState` | `ServiceMantle.Discovery.Registration.ConsulLifecycleState` |
| `ServiceMantle.Consul.ConsulRemotePresence` | `ServiceMantle.Discovery.Registration.ConsulRemotePresence` |
| `ServiceMantle.Consul.ConsulSettingDefinitions` | `ServiceMantle.Discovery.Configuration.ConsulSettingDefinitions` |

### 内部类型映射

| 原完整类型名 | 新完整类型名 |
| --- | --- |
| `ServiceMantle.Consul.ConsulInstanceAdvertisement` | `ServiceMantle.Discovery.Registration.ConsulInstanceAdvertisement` |
| `ServiceMantle.Consul.ConsulLifecycleSettings` | `ServiceMantle.Discovery.Registration.ConsulLifecycleSettings` |
| `ServiceMantle.Consul.ConsulLifecycleDiagnostics` | `ServiceMantle.Discovery.Registration.ConsulLifecycleDiagnostics` |
| `ServiceMantle.Consul.ConsulLifecycleDiagnostic` | `ServiceMantle.Discovery.Registration.ConsulLifecycleDiagnostic` |
| `ServiceMantle.Consul.ConsulLifecycleObserver` | `ServiceMantle.Discovery.Registration.ConsulLifecycleObserver` |
| `ServiceMantle.Consul.ConsulRegistrationLifecycle` | `ServiceMantle.Discovery.Registration.ConsulRegistrationLifecycle` |
| `ServiceMantle.Consul.ConsulSnapshotBinding` | `ServiceMantle.Discovery.Registration.ConsulSnapshotBinding` |
| `ServiceMantle.Consul.ConsulSnapshotSourceSelection` | `ServiceMantle.Discovery.Registration.ConsulSnapshotSourceSelection` |

类型名全部保留（含 `Consul` 产品词，B 类契约由类型名诚实暴露耦合）。

## Serilog 能力命名空间迁移（#575）

[#570](https://github.com/philfanzhou/ServiceMantle/issues/570) 能力命名空间切片之一：
`ServiceMantle.Serilog` 包的自有 namespace 迁往能力 namespace 的适配包子空间。包 ID、程序集名
`ServiceMantle.Serilog`、依赖与运行时行为不变；`serilog.*` / `loki.*` 错误码取值、配置节名、
`InternalsVisibleTo` 全部零变化。这是源码与二进制破坏性变更，只在后续新版本交付，不覆盖历史版本。

### namespace 迁移

| 原 namespace | 新 namespace |
| --- | --- |
| `ServiceMantle.Serilog`（6 个文件：选项、默认值、异常、包标记、运行时、净化 sink） | `ServiceMantle.Logging.Pipeline` |
| `ServiceMantle.Serilog.GrafanaLoki`（6 个文件：远程 sink 全部契约） | `ServiceMantle.Logging.Remote` |

`Microsoft.Extensions.Hosting` 的两个入口（`ServiceMantleSerilogHostApplicationBuilderExtensions`、
`ServiceMantleGrafanaLokiHostApplicationBuilderExtensions`）不变；签名与方法体中的全限定名随新
namespace 同步（如 `ServiceMantle.Logging.Pipeline.SerilogOptions`）。目录调整为
`src/ServiceMantle.Serilog/Pipeline/` 与 `src/ServiceMantle.Serilog/Remote/`，
`<RootNamespace>` 改为 `ServiceMantle.Logging.Pipeline`。能力 namespace 层次：`ServiceMantle.Logging`
归核心包，子空间 `Pipeline` / `Remote` 归 Serilog 适配包，互不跨越程序集。

### 公开 API 映射

| 原完整类型名 | 新完整类型名 |
| --- | --- |
| `ServiceMantle.Serilog.SerilogConfigurationException` | `ServiceMantle.Logging.Pipeline.SerilogConfigurationException` |
| `ServiceMantle.Serilog.SerilogOptions` | `ServiceMantle.Logging.Pipeline.SerilogOptions` |
| `ServiceMantle.Serilog.SerilogDefaults` | `ServiceMantle.Logging.Pipeline.SerilogDefaults` |
| `ServiceMantle.Serilog.SerilogPackage` | `ServiceMantle.Logging.Pipeline.SerilogPackage` |
| `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiOptions` | `ServiceMantle.Logging.Remote.GrafanaLokiOptions` |
| `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiDefaults` | `ServiceMantle.Logging.Remote.GrafanaLokiDefaults` |
| `ServiceMantle.Serilog.GrafanaLoki.WellKnownGrafanaLokiErrorCodes` | `ServiceMantle.Logging.Remote.WellKnownGrafanaLokiErrorCodes` |

### 内部类型映射

| 原完整类型名 | 新完整类型名 |
| --- | --- |
| `ServiceMantle.Serilog.ILogFieldSanitizer` | `ServiceMantle.Logging.Pipeline.ILogFieldSanitizer` |
| `ServiceMantle.Serilog.LogFieldSanitizer` | `ServiceMantle.Logging.Pipeline.LogFieldSanitizer` |
| `ServiceMantle.Serilog.ISerilogSinkFactory` | `ServiceMantle.Logging.Pipeline.ISerilogSinkFactory` |
| `ServiceMantle.Serilog.ConsoleSinkFactory` | `ServiceMantle.Logging.Pipeline.ConsoleSinkFactory` |
| `ServiceMantle.Serilog.SanitizingSink` | `ServiceMantle.Logging.Pipeline.SanitizingSink` |
| `ServiceMantle.Serilog.SerilogRegistration` | `ServiceMantle.Logging.Pipeline.SerilogRegistration` |
| `ServiceMantle.Serilog.SerilogConfiguration` | `ServiceMantle.Logging.Pipeline.SerilogConfiguration` |
| `ServiceMantle.Serilog.SerilogMarker` | `ServiceMantle.Logging.Pipeline.SerilogMarker` |
| `ServiceMantle.Serilog.SerilogRuntime` | `ServiceMantle.Logging.Pipeline.SerilogRuntime` |
| `ServiceMantle.Serilog.RuntimeLoggerProvider` | `ServiceMantle.Logging.Pipeline.RuntimeLoggerProvider` |
| `ServiceMantle.Serilog.SerilogLifecycle` | `ServiceMantle.Logging.Pipeline.SerilogLifecycle` |
| `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiRegistration` | `ServiceMantle.Logging.Remote.GrafanaLokiRegistration` |
| `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiConfiguration` | `ServiceMantle.Logging.Remote.GrafanaLokiConfiguration` |
| `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiConfigurationProvider` | `ServiceMantle.Logging.Remote.GrafanaLokiConfigurationProvider` |
| `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiRuntime` | `ServiceMantle.Logging.Remote.GrafanaLokiRuntime` |
| `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiLifecycle` | `ServiceMantle.Logging.Remote.GrafanaLokiLifecycle` |
| `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiSinkFactory` | `ServiceMantle.Logging.Remote.GrafanaLokiSinkFactory` |
| `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiFailureListener` | `ServiceMantle.Logging.Remote.GrafanaLokiFailureListener` |
| `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiCompositeSink` | `ServiceMantle.Logging.Remote.GrafanaLokiCompositeSink` |
| `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiRemoteSink` | `ServiceMantle.Logging.Remote.GrafanaLokiRemoteSink` |
| `ServiceMantle.Serilog.GrafanaLoki.ILokiHttpMessageHandlerFactory` | `ServiceMantle.Logging.Remote.ILokiHttpMessageHandlerFactory` |
| `ServiceMantle.Serilog.GrafanaLoki.LokiHttpMessageHandlerFactory` | `ServiceMantle.Logging.Remote.LokiHttpMessageHandlerFactory` |
| `ServiceMantle.Serilog.GrafanaLoki.LokiHttpMessageHandler` | `ServiceMantle.Logging.Remote.LokiHttpMessageHandler` |
| `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiDeliveryCounter` | `ServiceMantle.Logging.Remote.GrafanaLokiDeliveryCounter` |
| `ServiceMantle.Serilog.GrafanaLoki.LokiDeliveryException` | `ServiceMantle.Logging.Remote.LokiDeliveryException` |

类型名全部保留（`SerilogOptions` 等模块词是调用方同时引用多个同类 options 时的唯一区分，
`OutputTemplate` 等 B 类模型保留 Serilog 模板语义）。

## EF Core 持久化能力命名空间迁移（#577）

[#570](https://github.com/philfanzhou/ServiceMantle/issues/570) 能力命名空间切片之一：
`ServiceMantle.Persistence.EntityFrameworkCore` 包的自有 namespace 迁往
`ServiceMantle.Persistence.Relational` 及其子空间。包 ID、程序集名
`ServiceMantle.Persistence.EntityFrameworkCore`、依赖与运行时行为不变；数据库表列名、迁移锁键
前缀、Data Protection purpose、错误码、`InternalsVisibleTo` 全部零变化。这是源码与二进制破坏性
变更，只在后续新版本交付，不覆盖历史版本。

### namespace 迁移

| 原 namespace | 新 namespace |
| --- | --- |
| `ServiceMantle.Persistence.EntityFrameworkCore`（`IServiceDbContext.cs`，消费方实现的根契约） | `ServiceMantle.Persistence.Relational` |
| `ServiceMantle.Persistence.EntityFrameworkCore`（实体与模型映射 9 个文件） | `ServiceMantle.Persistence.Relational.Mapping` |
| `ServiceMantle.Persistence.EntityFrameworkCore`（读写服务与 store 7 个文件） | `ServiceMantle.Persistence.Relational.Stores` |
| `ServiceMantle.Persistence.EntityFrameworkCore`（Data Protection 密钥环 5 个文件） | `ServiceMantle.Persistence.Relational.DataProtection` |

目录随 namespace 调整为 `Mapping/`、`Stores/`、`DataProtection/`（契约文件留包根），
`<RootNamespace>` 改为 `ServiceMantle.Persistence.Relational`。`PersistKeysToServiceMantleEfCore`
方法名与 `EfCoreDataProtectionExtensions` 类名保持（本文件例外表先例）；`IServiceDbContext` 不上移
核心包（签名含 EF Core `DbSet`）。

### 公开 API 映射

| 原完整类型名 | 新完整类型名 |
| --- | --- |
| `ServiceMantle.Persistence.EntityFrameworkCore.IServiceDbContext` | `ServiceMantle.Persistence.Relational.IServiceDbContext` |
| `ServiceMantle.Persistence.EntityFrameworkCore.ModelBuilderExtensions` | `ServiceMantle.Persistence.Relational.Mapping.ModelBuilderExtensions` |
| `ServiceMantle.Persistence.EntityFrameworkCore.ServiceInstallationEntity` | `ServiceMantle.Persistence.Relational.Mapping.ServiceInstallationEntity` |
| `ServiceMantle.Persistence.EntityFrameworkCore.ServiceSettingModelBuilderExtensions` | `ServiceMantle.Persistence.Relational.Mapping.ServiceSettingModelBuilderExtensions` |
| `ServiceMantle.Persistence.EntityFrameworkCore.ManagementAuditModelBuilderExtensions` | `ServiceMantle.Persistence.Relational.Mapping.ManagementAuditModelBuilderExtensions` |
| `ServiceMantle.Persistence.EntityFrameworkCore.ManagementAuditDatabaseDialect` | `ServiceMantle.Persistence.Relational.Mapping.ManagementAuditDatabaseDialect` |
| `ServiceMantle.Persistence.EntityFrameworkCore.DataProtectionKeyModelBuilderExtensions` | `ServiceMantle.Persistence.Relational.DataProtection.DataProtectionKeyModelBuilderExtensions` |
| `ServiceMantle.Persistence.EntityFrameworkCore.WellKnownDataProtectionKeyRepositoryErrorCodes` | `ServiceMantle.Persistence.Relational.DataProtection.WellKnownDataProtectionKeyRepositoryErrorCodes` |
| `ServiceMantle.Persistence.EntityFrameworkCore.DataProtectionKeyRepositoryException` | `ServiceMantle.Persistence.Relational.DataProtection.DataProtectionKeyRepositoryException` |
| `ServiceMantle.Persistence.EntityFrameworkCore.EfCoreDataProtectionExtensions` | `ServiceMantle.Persistence.Relational.DataProtection.EfCoreDataProtectionExtensions` |
| `ServiceMantle.Persistence.EntityFrameworkCore.EfCoreDataProtectionKeyRepository` | `ServiceMantle.Persistence.Relational.DataProtection.EfCoreDataProtectionKeyRepository` |
| `ServiceMantle.Persistence.EntityFrameworkCore.EfCoreManagementAuditQueryService` | `ServiceMantle.Persistence.Relational.Stores.EfCoreManagementAuditQueryService` |
| `ServiceMantle.Persistence.EntityFrameworkCore.EfCoreManagementAuditWriter` | `ServiceMantle.Persistence.Relational.Stores.EfCoreManagementAuditWriter` |
| `ServiceMantle.Persistence.EntityFrameworkCore.EfCoreServiceInstallationStore` | `ServiceMantle.Persistence.Relational.Stores.EfCoreServiceInstallationStore` |
| `ServiceMantle.Persistence.EntityFrameworkCore.EfCoreServiceSettingStore` | `ServiceMantle.Persistence.Relational.Stores.EfCoreServiceSettingStore` |
| `ServiceMantle.Persistence.EntityFrameworkCore.EfCoreServiceSettingUpdateTransaction` | `ServiceMantle.Persistence.Relational.Stores.EfCoreServiceSettingUpdateTransaction` |
| `ServiceMantle.Persistence.EntityFrameworkCore.EfCoreServiceSetupCodeStore` | `ServiceMantle.Persistence.Relational.Stores.EfCoreServiceSetupCodeStore` |

### 内部类型映射

| 原完整类型名 | 新完整类型名 |
| --- | --- |
| `ServiceMantle.Persistence.EntityFrameworkCore.ServiceInstallationEntityStateMapper` | `ServiceMantle.Persistence.Relational.Mapping.ServiceInstallationEntityStateMapper` |
| `ServiceMantle.Persistence.EntityFrameworkCore.ServiceSettingEntity` | `ServiceMantle.Persistence.Relational.Mapping.ServiceSettingEntity` |
| `ServiceMantle.Persistence.EntityFrameworkCore.ManagementAuditEntityMapper` | `ServiceMantle.Persistence.Relational.Mapping.ManagementAuditEntityMapper` |
| `ServiceMantle.Persistence.EntityFrameworkCore.ManagementAuditLogEntity` | `ServiceMantle.Persistence.Relational.Mapping.ManagementAuditLogEntity` |
| `ServiceMantle.Persistence.EntityFrameworkCore.ManagementAuditDatabaseFunctions` | `ServiceMantle.Persistence.Relational.Mapping.ManagementAuditDatabaseFunctions` |
| `ServiceMantle.Persistence.EntityFrameworkCore.DataProtectionKeyEntity` | `ServiceMantle.Persistence.Relational.DataProtection.DataProtectionKeyEntity` |
| `ServiceMantle.Persistence.EntityFrameworkCore.ManagementAuditBoundedRow` | `ServiceMantle.Persistence.Relational.Stores.ManagementAuditBoundedRow` |
| `ServiceMantle.Persistence.EntityFrameworkCore.ManagementAuditContinuationCursor` | `ServiceMantle.Persistence.Relational.Stores.ManagementAuditContinuationCursor` |

### 名称敏感反射与快照

- `tests/ServiceMantle.Tests` 的 SignaCore 遗留迁移清单测试以
  `typeof(ServiceMantle.Persistence.EntityFrameworkCore.EfCoreManagementAuditWriter<>)` 定位替换程序集，
  随类型全名同步为 `ServiceMantle.Persistence.Relational.Stores.EfCoreManagementAuditWriter<>`；清单
  数据本身不含旧 namespace。
- ReferenceService 的 EF Core 迁移历史文件（`Migrations/*.cs`）中的实体全名字符串是消费方拥有的
  历史记录，按「已应用迁移不回改」保持原样；表名与列名为显式映射，relational 模型按表名比较，实体
  CLR 全名变化不产生 schema 差异。

## AspNetCore 能力命名空间迁移（#573）

[#570](https://github.com/philfanzhou/ServiceMantle/issues/570) 能力命名空间切片之一：
`ServiceMantle.AspNetCore` 包的自有 namespace 迁往 `ServiceMantle.Web` 及其子空间（子空间名不变，
仅包自有前缀替换）。包 ID、程序集名 `ServiceMantle.AspNetCore`、依赖与运行时行为不变；日志分类
`ServiceMantle.Http.CorrelationId|ProblemDetails|RateLimiting`、认证方案
`ServiceMantle.ManagementCookie|ManagementAdmin|ManagementSession`、Data Protection purpose
`ServiceMantle.Management:<serviceId>`、meter 名与全部指标名、HTTP 路由与 Header、JSON 字段、配置键、
`InternalsVisibleTo` 全部零变化。这是源码与二进制破坏性变更，只在后续新版本交付，不覆盖历史版本。

### namespace 迁移

| 原 namespace | 新 namespace |
| --- | --- |
| `ServiceMantle.AspNetCore`（根 3 个文件：builder、宿主注册、上移契约的默认实现） | `ServiceMantle.Web` |
| `ServiceMantle.AspNetCore.Health` | `ServiceMantle.Web.Health` |
| `ServiceMantle.AspNetCore.Http` | `ServiceMantle.Web.Http` |
| `ServiceMantle.AspNetCore.Logging` | `ServiceMantle.Web.Logging` |
| `ServiceMantle.AspNetCore.Management` | `ServiceMantle.Web.Management` |
| `ServiceMantle.AspNetCore.ManagementApi` | `ServiceMantle.Web.ManagementApi` |
| `ServiceMantle.AspNetCore.ManagementApi.AuditQueries` | `ServiceMantle.Web.ManagementApi.AuditQueries` |
| `ServiceMantle.AspNetCore.ManagementApi.Bootstrap` | `ServiceMantle.Web.ManagementApi.Bootstrap` |
| `ServiceMantle.AspNetCore.ManagementApi.Entries` | `ServiceMantle.Web.ManagementApi.Entries` |
| `ServiceMantle.AspNetCore.ManagementApi.RuntimeInfo` | `ServiceMantle.Web.ManagementApi.RuntimeInfo` |
| `ServiceMantle.AspNetCore.ManagementApi.Session` | `ServiceMantle.Web.ManagementApi.Session` |
| `ServiceMantle.AspNetCore.ManagementApi.SettingQueries` | `ServiceMantle.Web.ManagementApi.SettingQueries` |
| `ServiceMantle.AspNetCore.ManagementApi.SettingUpdates` | `ServiceMantle.Web.ManagementApi.SettingUpdates` |
| `ServiceMantle.AspNetCore.ManagementApi.Setup` | `ServiceMantle.Web.ManagementApi.Setup` |
| `ServiceMantle.AspNetCore.ManagementApi.Status` | `ServiceMantle.Web.ManagementApi.Status` |
| `ServiceMantle.AspNetCore.PhaseGate` | `ServiceMantle.Web.PhaseGate` |
| `ServiceMantle.AspNetCore.RateLimiting` | `ServiceMantle.Web.RateLimiting` |

框架 namespace 入口（`Microsoft.AspNetCore.Builder` 19 个、`Microsoft.AspNetCore.Http` 1 个、
`Microsoft.Extensions.DependencyInjection` 7 个、`Microsoft.Extensions.Hosting` 1 个文件）全部不变，
类名与方法名保留 `ServiceMantle*` 产品前缀；仅其中的 `using` 行随新 namespace 同步。目录与文件名
保持，`<RootNamespace>` 改为 `ServiceMantle.Web`。`ServiceMantle.AspNetCore.Health` 下
`ServiceMantleHealthEndpointRouteBuilderExtensions.cs` 属框架 namespace，位置与类名均不动。#572 上移
核心包的两个契约（`ServiceMantle.Installation.IServiceStartupPhaseResolver`、
`ServiceMantle.Health.IServiceHealthSnapshotSource`）不在本切片范围；其包内默认实现
`DefaultServiceStartupPhaseResolver` 的完整类型名随本切片变为
`ServiceMantle.Web.DefaultServiceStartupPhaseResolver`。

### 公开 API 映射

| 原完整类型名 | 新完整类型名 |
| --- | --- |
| `ServiceMantle.AspNetCore.ServiceMantleBuilder` | `ServiceMantle.Web.ServiceMantleBuilder` |
| `ServiceMantle.AspNetCore.Health.HealthOptions` | `ServiceMantle.Web.Health.HealthOptions` |
| `ServiceMantle.AspNetCore.Health.WellKnownServiceHealthErrorCodes` | `ServiceMantle.Web.Health.WellKnownServiceHealthErrorCodes` |
| `ServiceMantle.AspNetCore.Http.ForwardedHeadersConfigurationException` | `ServiceMantle.Web.Http.ForwardedHeadersConfigurationException` |
| `ServiceMantle.AspNetCore.Http.ForwardedHeadersTrustOptions` | `ServiceMantle.Web.Http.ForwardedHeadersTrustOptions` |
| `ServiceMantle.AspNetCore.Http.ProblemDetailsDefaults` | `ServiceMantle.Web.Http.ProblemDetailsDefaults` |
| `ServiceMantle.AspNetCore.Http.SecurityResponseHeadersMetadata` | `ServiceMantle.Web.Http.SecurityResponseHeadersMetadata` |
| `ServiceMantle.AspNetCore.Http.ServiceHeaderNames` | `ServiceMantle.Web.Http.ServiceHeaderNames` |
| `ServiceMantle.AspNetCore.Http.WellKnownForwardedHeadersConfigurationErrorCodes` | `ServiceMantle.Web.Http.WellKnownForwardedHeadersConfigurationErrorCodes` |
| `ServiceMantle.AspNetCore.Logging.RequestHeaderDiagnosticProjector` | `ServiceMantle.Web.Logging.RequestHeaderDiagnosticProjector` |
| `ServiceMantle.AspNetCore.Logging.SensitiveHeaderConfigurationException` | `ServiceMantle.Web.Logging.SensitiveHeaderConfigurationException` |
| `ServiceMantle.AspNetCore.Logging.SensitiveHeaderRegistry` | `ServiceMantle.Web.Logging.SensitiveHeaderRegistry` |
| `ServiceMantle.AspNetCore.Logging.SensitiveHeadersOptions` | `ServiceMantle.Web.Logging.SensitiveHeadersOptions` |
| `ServiceMantle.AspNetCore.Logging.ServiceLogContext` | `ServiceMantle.Web.Logging.ServiceLogContext` |
| `ServiceMantle.AspNetCore.Logging.ServiceLogFieldNames` | `ServiceMantle.Web.Logging.ServiceLogFieldNames` |
| `ServiceMantle.AspNetCore.Logging.WellKnownSensitiveHeaderConfigurationErrorCodes` | `ServiceMantle.Web.Logging.WellKnownSensitiveHeaderConfigurationErrorCodes` |
| `ServiceMantle.AspNetCore.Management.ManagementAuthorizationDefaults` | `ServiceMantle.Web.Management.ManagementAuthorizationDefaults` |
| `ServiceMantle.AspNetCore.Management.ManagementCookieOptions` | `ServiceMantle.Web.Management.ManagementCookieOptions` |
| `ServiceMantle.AspNetCore.Management.ManagementPermissionAuthorizationHandler` | `ServiceMantle.Web.Management.ManagementPermissionAuthorizationHandler` |
| `ServiceMantle.AspNetCore.Management.ManagementPermissionRequirement` | `ServiceMantle.Web.Management.ManagementPermissionRequirement` |
| `ServiceMantle.AspNetCore.Management.ManagementSessionAuthorizationHandler` | `ServiceMantle.Web.Management.ManagementSessionAuthorizationHandler` |
| `ServiceMantle.AspNetCore.Management.ManagementSessionDefaults` | `ServiceMantle.Web.Management.ManagementSessionDefaults` |
| `ServiceMantle.AspNetCore.Management.ManagementSessionRequirement` | `ServiceMantle.Web.Management.ManagementSessionRequirement` |
| `ServiceMantle.AspNetCore.ManagementApi.Bootstrap.BootstrapManagementOptions` | `ServiceMantle.Web.ManagementApi.Bootstrap.BootstrapManagementOptions` |
| `ServiceMantle.AspNetCore.ManagementApi.Entries.ManagementEntryDefaults` | `ServiceMantle.Web.ManagementApi.Entries.ManagementEntryDefaults` |
| `ServiceMantle.AspNetCore.ManagementApi.Entries.ManagementEntryKind` | `ServiceMantle.Web.ManagementApi.Entries.ManagementEntryKind` |
| `ServiceMantle.AspNetCore.ManagementApi.ManagementApiDefaults` | `ServiceMantle.Web.ManagementApi.ManagementApiDefaults` |
| `ServiceMantle.AspNetCore.ManagementApi.ManagementApiOptions` | `ServiceMantle.Web.ManagementApi.ManagementApiOptions` |
| `ServiceMantle.AspNetCore.ManagementApi.ManagementApiResults` | `ServiceMantle.Web.ManagementApi.ManagementApiResults` |
| `ServiceMantle.AspNetCore.ManagementApi.Session.ManagementLoginAdapter` | `ServiceMantle.Web.ManagementApi.Session.ManagementLoginAdapter` |
| `ServiceMantle.AspNetCore.ManagementApi.Session.ManagementSessionOptions` | `ServiceMantle.Web.ManagementApi.Session.ManagementSessionOptions` |
| `ServiceMantle.AspNetCore.ManagementApi.SettingUpdates.SettingUpdateExecutor` | `ServiceMantle.Web.ManagementApi.SettingUpdates.SettingUpdateExecutor` |
| `ServiceMantle.AspNetCore.ManagementApi.Setup.SetupCompletionResult` | `ServiceMantle.Web.ManagementApi.Setup.SetupCompletionResult` |
| `ServiceMantle.AspNetCore.ManagementApi.Setup.SetupCompletionStatus` | `ServiceMantle.Web.ManagementApi.Setup.SetupCompletionStatus` |
| `ServiceMantle.AspNetCore.ManagementApi.Setup.SetupExecutor` | `ServiceMantle.Web.ManagementApi.Setup.SetupExecutor` |
| `ServiceMantle.AspNetCore.ManagementApi.Setup.SetupInput` | `ServiceMantle.Web.ManagementApi.Setup.SetupInput` |
| `ServiceMantle.AspNetCore.ManagementApi.Setup.SetupInputExecutor` | `ServiceMantle.Web.ManagementApi.Setup.SetupInputExecutor` |
| `ServiceMantle.AspNetCore.PhaseGate.ManagementSurface` | `ServiceMantle.Web.PhaseGate.ManagementSurface` |
| `ServiceMantle.AspNetCore.PhaseGate.PhaseGateOptions` | `ServiceMantle.Web.PhaseGate.PhaseGateOptions` |
| `ServiceMantle.AspNetCore.RateLimiting.RateLimitPolicyOptions` | `ServiceMantle.Web.RateLimiting.RateLimitPolicyOptions` |
| `ServiceMantle.AspNetCore.RateLimiting.RateLimitingConfigurationException` | `ServiceMantle.Web.RateLimiting.RateLimitingConfigurationException` |
| `ServiceMantle.AspNetCore.RateLimiting.RateLimitingDefaults` | `ServiceMantle.Web.RateLimiting.RateLimitingDefaults` |
| `ServiceMantle.AspNetCore.RateLimiting.RateLimitingOptions` | `ServiceMantle.Web.RateLimiting.RateLimitingOptions` |

### 内部类型映射

| 原完整类型名 | 新完整类型名 |
| --- | --- |
| `ServiceMantle.AspNetCore.DefaultServiceStartupPhaseResolver` | `ServiceMantle.Web.DefaultServiceStartupPhaseResolver` |
| `ServiceMantle.AspNetCore.HostRegistration` | `ServiceMantle.Web.HostRegistration` |
| `ServiceMantle.AspNetCore.Health.HealthRegistration` | `ServiceMantle.Web.Health.HealthRegistration` |
| `ServiceMantle.AspNetCore.Health.HealthStartupValidator` | `ServiceMantle.Web.Health.HealthStartupValidator` |
| `ServiceMantle.AspNetCore.Health.ReadinessDecisionSource` | `ServiceMantle.Web.Health.ReadinessDecisionSource` |
| `ServiceMantle.AspNetCore.Http.CorrelationIdMiddleware` | `ServiceMantle.Web.Http.CorrelationIdMiddleware` |
| `ServiceMantle.AspNetCore.Http.CorrelationIdRequestSlot` | `ServiceMantle.Web.Http.CorrelationIdRequestSlot` |
| `ServiceMantle.AspNetCore.Http.CorrelationIdValue` | `ServiceMantle.Web.Http.CorrelationIdValue` |
| `ServiceMantle.AspNetCore.Http.ExceptionMapping` | `ServiceMantle.Web.Http.ExceptionMapping` |
| `ServiceMantle.AspNetCore.Http.ExceptionMappingRegistration` | `ServiceMantle.Web.Http.ExceptionMappingRegistration` |
| `ServiceMantle.AspNetCore.Http.ExceptionMappingRegistry` | `ServiceMantle.Web.Http.ExceptionMappingRegistry` |
| `ServiceMantle.AspNetCore.Http.ForwardedHeadersMiddleware` | `ServiceMantle.Web.Http.ForwardedHeadersMiddleware` |
| `ServiceMantle.AspNetCore.Http.ForwardedHeadersRegistration` | `ServiceMantle.Web.Http.ForwardedHeadersRegistration` |
| `ServiceMantle.AspNetCore.Http.ForwardedHeadersSnapshotProvider` | `ServiceMantle.Web.Http.ForwardedHeadersSnapshotProvider` |
| `ServiceMantle.AspNetCore.Http.ForwardedHeadersStartupValidator` | `ServiceMantle.Web.Http.ForwardedHeadersStartupValidator` |
| `ServiceMantle.AspNetCore.Http.IExceptionMappingRegistration` | `ServiceMantle.Web.Http.IExceptionMappingRegistration` |
| `ServiceMantle.AspNetCore.Http.PipelineComposition` | `ServiceMantle.Web.Http.PipelineComposition` |
| `ServiceMantle.AspNetCore.Http.ProblemDetailsMiddleware` | `ServiceMantle.Web.Http.ProblemDetailsMiddleware` |
| `ServiceMantle.AspNetCore.Http.ProblemDetailsStartupValidator` | `ServiceMantle.Web.Http.ProblemDetailsStartupValidator` |
| `ServiceMantle.AspNetCore.Http.ProblemExtensionFactory` | `ServiceMantle.Web.Http.ProblemExtensionFactory` |
| `ServiceMantle.AspNetCore.Http.ProblemValue` | `ServiceMantle.Web.Http.ProblemValue` |
| `ServiceMantle.AspNetCore.Http.SecurityResponseHeadersMiddleware` | `ServiceMantle.Web.Http.SecurityResponseHeadersMiddleware` |
| `ServiceMantle.AspNetCore.Http.SecurityResponseHeadersRegistration` | `ServiceMantle.Web.Http.SecurityResponseHeadersRegistration` |
| `ServiceMantle.AspNetCore.Logging.SensitiveHeaderRegistration` | `ServiceMantle.Web.Logging.SensitiveHeaderRegistration` |
| `ServiceMantle.AspNetCore.Logging.SensitiveHeaderSanitizer` | `ServiceMantle.Web.Logging.SensitiveHeaderSanitizer` |
| `ServiceMantle.AspNetCore.Logging.SensitiveHeaderStartupValidator` | `ServiceMantle.Web.Logging.SensitiveHeaderStartupValidator` |
| `ServiceMantle.AspNetCore.Management.ManagementCookieEvents` | `ServiceMantle.Web.Management.ManagementCookieEvents` |
| `ServiceMantle.AspNetCore.Management.ManagementCookieRegistration` | `ServiceMantle.Web.Management.ManagementCookieRegistration` |
| `ServiceMantle.AspNetCore.Management.ManagementCookieStartupValidator` | `ServiceMantle.Web.Management.ManagementCookieStartupValidator` |
| `ServiceMantle.AspNetCore.ManagementApi.AuditQueries.AuditQueryHandlers` | `ServiceMantle.Web.ManagementApi.AuditQueries.AuditQueryHandlers` |
| `ServiceMantle.AspNetCore.ManagementApi.AuditQueries.AuditQueryMapping` | `ServiceMantle.Web.ManagementApi.AuditQueries.AuditQueryMapping` |
| `ServiceMantle.AspNetCore.ManagementApi.AuditQueries.AuditQueryResult` | `ServiceMantle.Web.ManagementApi.AuditQueries.AuditQueryResult` |
| `ServiceMantle.AspNetCore.ManagementApi.Bootstrap.BootstrapHandlers` | `ServiceMantle.Web.ManagementApi.Bootstrap.BootstrapHandlers` |
| `ServiceMantle.AspNetCore.ManagementApi.Bootstrap.BootstrapManagementRegistration` | `ServiceMantle.Web.ManagementApi.Bootstrap.BootstrapManagementRegistration` |
| `ServiceMantle.AspNetCore.ManagementApi.Bootstrap.BootstrapManagementStartupValidator` | `ServiceMantle.Web.ManagementApi.Bootstrap.BootstrapManagementStartupValidator` |
| `ServiceMantle.AspNetCore.ManagementApi.Bootstrap.BootstrapMapping` | `ServiceMantle.Web.ManagementApi.Bootstrap.BootstrapMapping` |
| `ServiceMantle.AspNetCore.ManagementApi.Bootstrap.BootstrapRequest` | `ServiceMantle.Web.ManagementApi.Bootstrap.BootstrapRequest` |
| `ServiceMantle.AspNetCore.ManagementApi.Bootstrap.BootstrapRequestParser` | `ServiceMantle.Web.ManagementApi.Bootstrap.BootstrapRequestParser` |
| `ServiceMantle.AspNetCore.ManagementApi.Bootstrap.BootstrapResult` | `ServiceMantle.Web.ManagementApi.Bootstrap.BootstrapResult` |
| `ServiceMantle.AspNetCore.ManagementApi.Bootstrap.BootstrapUpdateCredential` | `ServiceMantle.Web.ManagementApi.Bootstrap.BootstrapUpdateCredential` |
| `ServiceMantle.AspNetCore.ManagementApi.Bootstrap.BootstrapUpdateCredentialRegistration` | `ServiceMantle.Web.ManagementApi.Bootstrap.BootstrapUpdateCredentialRegistration` |
| `ServiceMantle.AspNetCore.ManagementApi.Entries.ManagementEntryDefinition` | `ServiceMantle.Web.ManagementApi.Entries.ManagementEntryDefinition` |
| `ServiceMantle.AspNetCore.ManagementApi.Entries.ManagementEntryMetadata` | `ServiceMantle.Web.ManagementApi.Entries.ManagementEntryMetadata` |
| `ServiceMantle.AspNetCore.ManagementApi.Entries.ManagementEntryStartupValidator` | `ServiceMantle.Web.ManagementApi.Entries.ManagementEntryStartupValidator` |
| `ServiceMantle.AspNetCore.ManagementApi.Entries.ManagementEntryState` | `ServiceMantle.Web.ManagementApi.Entries.ManagementEntryState` |
| `ServiceMantle.AspNetCore.ManagementApi.Entries.UnsafeRequestFilter` | `ServiceMantle.Web.ManagementApi.Entries.UnsafeRequestFilter` |
| `ServiceMantle.AspNetCore.ManagementApi.Entries.UnsafeRequestGuardMetadata` | `ServiceMantle.Web.ManagementApi.Entries.UnsafeRequestGuardMetadata` |
| `ServiceMantle.AspNetCore.ManagementApi.ManagementApiMetadata` | `ServiceMantle.Web.ManagementApi.ManagementApiMetadata` |
| `ServiceMantle.AspNetCore.ManagementApi.ManagementApiProblemResult` | `ServiceMantle.Web.ManagementApi.ManagementApiProblemResult` |
| `ServiceMantle.AspNetCore.ManagementApi.ManagementApiRegistration` | `ServiceMantle.Web.ManagementApi.ManagementApiRegistration` |
| `ServiceMantle.AspNetCore.ManagementApi.ManagementApiStartupValidator` | `ServiceMantle.Web.ManagementApi.ManagementApiStartupValidator` |
| `ServiceMantle.AspNetCore.ManagementApi.ManagementApiState` | `ServiceMantle.Web.ManagementApi.ManagementApiState` |
| `ServiceMantle.AspNetCore.ManagementApi.RuntimeInfo.RuntimeInfoMapping` | `ServiceMantle.Web.ManagementApi.RuntimeInfo.RuntimeInfoMapping` |
| `ServiceMantle.AspNetCore.ManagementApi.RuntimeInfo.RuntimeInfoResult` | `ServiceMantle.Web.ManagementApi.RuntimeInfo.RuntimeInfoResult` |
| `ServiceMantle.AspNetCore.ManagementApi.Session.ManagementSessionBodyAdmission` | `ServiceMantle.Web.ManagementApi.Session.ManagementSessionBodyAdmission` |
| `ServiceMantle.AspNetCore.ManagementApi.Session.ManagementSessionBodyStatus` | `ServiceMantle.Web.ManagementApi.Session.ManagementSessionBodyStatus` |
| `ServiceMantle.AspNetCore.ManagementApi.Session.ManagementSessionHandlers` | `ServiceMantle.Web.ManagementApi.Session.ManagementSessionHandlers` |
| `ServiceMantle.AspNetCore.ManagementApi.Session.ManagementSessionMapping` | `ServiceMantle.Web.ManagementApi.Session.ManagementSessionMapping` |
| `ServiceMantle.AspNetCore.ManagementApi.Session.ManagementSessionResult` | `ServiceMantle.Web.ManagementApi.Session.ManagementSessionResult` |
| `ServiceMantle.AspNetCore.ManagementApi.SettingQueries.SettingQueryHandlers` | `ServiceMantle.Web.ManagementApi.SettingQueries.SettingQueryHandlers` |
| `ServiceMantle.AspNetCore.ManagementApi.SettingQueries.SettingQueryMapping` | `ServiceMantle.Web.ManagementApi.SettingQueries.SettingQueryMapping` |
| `ServiceMantle.AspNetCore.ManagementApi.SettingQueries.SettingQueryResult` | `ServiceMantle.Web.ManagementApi.SettingQueries.SettingQueryResult` |
| `ServiceMantle.AspNetCore.ManagementApi.SettingUpdates.SettingUpdateHandlers` | `ServiceMantle.Web.ManagementApi.SettingUpdates.SettingUpdateHandlers` |
| `ServiceMantle.AspNetCore.ManagementApi.SettingUpdates.SettingUpdateMapping` | `ServiceMantle.Web.ManagementApi.SettingUpdates.SettingUpdateMapping` |
| `ServiceMantle.AspNetCore.ManagementApi.SettingUpdates.SettingUpdateRequestParser` | `ServiceMantle.Web.ManagementApi.SettingUpdates.SettingUpdateRequestParser` |
| `ServiceMantle.AspNetCore.ManagementApi.SettingUpdates.SettingUpdateResult` | `ServiceMantle.Web.ManagementApi.SettingUpdates.SettingUpdateResult` |
| `ServiceMantle.AspNetCore.ManagementApi.Setup.SetupHandlers` | `ServiceMantle.Web.ManagementApi.Setup.SetupHandlers` |
| `ServiceMantle.AspNetCore.ManagementApi.Setup.SetupMapping` | `ServiceMantle.Web.ManagementApi.Setup.SetupMapping` |
| `ServiceMantle.AspNetCore.ManagementApi.Setup.SetupRequestParser` | `ServiceMantle.Web.ManagementApi.Setup.SetupRequestParser` |
| `ServiceMantle.AspNetCore.ManagementApi.Setup.SetupResult` | `ServiceMantle.Web.ManagementApi.Setup.SetupResult` |
| `ServiceMantle.AspNetCore.ManagementApi.Status.BootstrapRestartLatch` | `ServiceMantle.Web.ManagementApi.Status.BootstrapRestartLatch` |
| `ServiceMantle.AspNetCore.ManagementApi.Status.BootstrapStatusReader` | `ServiceMantle.Web.ManagementApi.Status.BootstrapStatusReader` |
| `ServiceMantle.AspNetCore.ManagementApi.Status.IBootstrapStatusReader` | `ServiceMantle.Web.ManagementApi.Status.IBootstrapStatusReader` |
| `ServiceMantle.AspNetCore.ManagementApi.Status.InstallationStatusHandler` | `ServiceMantle.Web.ManagementApi.Status.InstallationStatusHandler` |
| `ServiceMantle.AspNetCore.ManagementApi.Status.InstallationStatusMapping` | `ServiceMantle.Web.ManagementApi.Status.InstallationStatusMapping` |
| `ServiceMantle.AspNetCore.ManagementApi.Status.InstallationStatusResult` | `ServiceMantle.Web.ManagementApi.Status.InstallationStatusResult` |
| `ServiceMantle.AspNetCore.PhaseGate.ManagementSurfaceMetadata` | `ServiceMantle.Web.PhaseGate.ManagementSurfaceMetadata` |
| `ServiceMantle.AspNetCore.PhaseGate.PhaseAdmissionMetadata` | `ServiceMantle.Web.PhaseGate.PhaseAdmissionMetadata` |
| `ServiceMantle.AspNetCore.PhaseGate.PhaseGateMiddleware` | `ServiceMantle.Web.PhaseGate.PhaseGateMiddleware` |
| `ServiceMantle.AspNetCore.PhaseGate.PhaseGateRegistration` | `ServiceMantle.Web.PhaseGate.PhaseGateRegistration` |
| `ServiceMantle.AspNetCore.PhaseGate.PhaseGateStartupValidator` | `ServiceMantle.Web.PhaseGate.PhaseGateStartupValidator` |
| `ServiceMantle.AspNetCore.PhaseGate.PhaseGateState` | `ServiceMantle.Web.PhaseGate.PhaseGateState` |
| `ServiceMantle.AspNetCore.PhaseGate.PhaseHealthMetadata` | `ServiceMantle.Web.PhaseGate.PhaseHealthMetadata` |
| `ServiceMantle.AspNetCore.RateLimiting.RateLimitPolicySnapshot` | `ServiceMantle.Web.RateLimiting.RateLimitPolicySnapshot` |
| `ServiceMantle.AspNetCore.RateLimiting.RateLimitingPolicy` | `ServiceMantle.Web.RateLimiting.RateLimitingPolicy` |
| `ServiceMantle.AspNetCore.RateLimiting.RateLimitingRegistration` | `ServiceMantle.Web.RateLimiting.RateLimitingRegistration` |
| `ServiceMantle.AspNetCore.RateLimiting.RateLimitingSnapshot` | `ServiceMantle.Web.RateLimiting.RateLimitingSnapshot` |
| `ServiceMantle.AspNetCore.RateLimiting.RateLimitingSnapshotProvider` | `ServiceMantle.Web.RateLimiting.RateLimitingSnapshotProvider` |
| `ServiceMantle.AspNetCore.RateLimiting.RateLimitingStartupValidator` | `ServiceMantle.Web.RateLimiting.RateLimitingStartupValidator` |

类型名全部保留（含例外表先例 `ServiceMantleBuilder` 与框架入口的 `ServiceMantle*` 前缀类名；
4 个消费方实现的 `delegate` 契约 `SettingUpdateExecutor`、`SetupExecutor`、`SetupInputExecutor`、
`ManagementLoginAdapter` 同样只换 namespace）。

### 名称敏感反射与消费编译

- 对 `HealthStartupValidator` 与 `SensitiveHeaderStartupValidator` 的按名字断言（`GetType().Name`）
  按类型短名匹配，类型名未变，断言与验证代码均零变化；本文件「没有变化的部分」一节列出的两处
  诊断在此继续有效。
- `eng/tests/consumers` 的 `aspnetcore`、`composed` 项目随新 namespace 更新 using，并继续同时 using
  所需框架 namespace（暴露 CS0104）；`opentelemetry`、`provider-neutral` 项目保持不 using 任何适配包
  自有 namespace 的既有约束（`provider-neutral` 的约束注释随本切片改述为不以 `ServiceMantle.Web`
  点名 #572 上移契约）。

## OpenTelemetry 能力命名空间迁移（#576）

[#570](https://github.com/philfanzhou/ServiceMantle/issues/570) 能力命名空间切片之一：
`ServiceMantle.OpenTelemetry` 包的自有 namespace 迁往 `ServiceMantle.Diagnostics` 能力 namespace 的
适配包子空间。包 ID、程序集名 `ServiceMantle.OpenTelemetry`、依赖（含 OpenTelemetry SDK 依赖）与
运行时行为不变；OTLP 配置节名 `ServiceMantle.Otlp.Traces` / `ServiceMantle.Otlp.Metrics`、meter 名
`ServiceMantle` 与全部指标名、Prometheus scrape 路由与 Header、`otlp.*` / `prometheus.*` 错误码、
`InternalsVisibleTo` 全部零变化。这是源码与二进制破坏性变更，只在后续新版本交付，不覆盖历史版本。

### namespace 迁移

| 原 namespace | 新 namespace |
| --- | --- |
| `ServiceMantle.OpenTelemetry`（根 2 个文件：插桩选项、注册与启动校验） | `ServiceMantle.Diagnostics.Instrumentation` |
| `ServiceMantle.OpenTelemetry.Otlp`（3 个文件：OTLP 选项、错误码、运行时） | `ServiceMantle.Diagnostics.Export.Otlp` |
| `ServiceMantle.OpenTelemetry.Prometheus`（5 个文件：Prometheus 选项、默认值、错误码、注册快照、scrape 门） | `ServiceMantle.Diagnostics.Export.Prometheus` |

`Microsoft.Extensions.DependencyInjection` 的 4 个入口与 `Microsoft.AspNetCore.Builder` 的 1 个
入口不变（框架 namespace 入口，类名与方法名保留产品辨识名）；仅其中的 `using` 行随新 namespace
同步。目录调整为根文件移入 `Instrumentation/`（`Otlp/`、`Prometheus/` 目录名与子空间末段一致，
保持不变），`<RootNamespace>` 改为 `ServiceMantle.Diagnostics.Instrumentation`。能力 namespace
层次：`ServiceMantle.Diagnostics` 与 `ServiceMantle.Diagnostics.Instrumentation` 中的核心自有类型
（`ServiceMetrics` 等，见上文 #433/#443 映射）归核心包，子空间 `Instrumentation` / `Export.Otlp` /
`Export.Prometheus` 归 OpenTelemetry 适配包，互不跨越程序集。OTLP 与 Prometheus 是标准协议名而非
库名，住 `Export` 子空间（ADR 0007「协议名不是库名」）。

### 公开 API 映射

| 原完整类型名 | 新完整类型名 |
| --- | --- |
| `ServiceMantle.OpenTelemetry.OpenTelemetryOptions` | `ServiceMantle.Diagnostics.Instrumentation.OpenTelemetryOptions` |
| `ServiceMantle.OpenTelemetry.Otlp.OtlpOptions` | `ServiceMantle.Diagnostics.Export.Otlp.OtlpOptions` |
| `ServiceMantle.OpenTelemetry.Otlp.OtlpProtocol` | `ServiceMantle.Diagnostics.Export.Otlp.OtlpProtocol` |
| `ServiceMantle.OpenTelemetry.Otlp.OtlpSignalOptions` | `ServiceMantle.Diagnostics.Export.Otlp.OtlpSignalOptions` |
| `ServiceMantle.OpenTelemetry.Otlp.OtlpTraceOptions` | `ServiceMantle.Diagnostics.Export.Otlp.OtlpTraceOptions` |
| `ServiceMantle.OpenTelemetry.Otlp.OtlpMetricOptions` | `ServiceMantle.Diagnostics.Export.Otlp.OtlpMetricOptions` |
| `ServiceMantle.OpenTelemetry.Otlp.OtlpConfigurationException` | `ServiceMantle.Diagnostics.Export.Otlp.OtlpConfigurationException` |
| `ServiceMantle.OpenTelemetry.Otlp.WellKnownOtlpErrorCodes` | `ServiceMantle.Diagnostics.Export.Otlp.WellKnownOtlpErrorCodes` |
| `ServiceMantle.OpenTelemetry.Prometheus.PrometheusOptions` | `ServiceMantle.Diagnostics.Export.Prometheus.PrometheusOptions` |
| `ServiceMantle.OpenTelemetry.Prometheus.PrometheusDefaults` | `ServiceMantle.Diagnostics.Export.Prometheus.PrometheusDefaults` |
| `ServiceMantle.OpenTelemetry.Prometheus.PrometheusConfigurationException` | `ServiceMantle.Diagnostics.Export.Prometheus.PrometheusConfigurationException` |
| `ServiceMantle.OpenTelemetry.Prometheus.WellKnownPrometheusErrorCodes` | `ServiceMantle.Diagnostics.Export.Prometheus.WellKnownPrometheusErrorCodes` |

### 内部类型映射

| 原完整类型名 | 新完整类型名 |
| --- | --- |
| `ServiceMantle.OpenTelemetry.OpenTelemetryRegistration` | `ServiceMantle.Diagnostics.Instrumentation.OpenTelemetryRegistration` |
| `ServiceMantle.OpenTelemetry.OpenTelemetryRegistrationValidator` | `ServiceMantle.Diagnostics.Instrumentation.OpenTelemetryRegistrationValidator` |
| `ServiceMantle.OpenTelemetry.Otlp.OtlpSignal` | `ServiceMantle.Diagnostics.Export.Otlp.OtlpSignal` |
| `ServiceMantle.OpenTelemetry.Otlp.OtlpSignalRegistration` | `ServiceMantle.Diagnostics.Export.Otlp.OtlpSignalRegistration` |
| `ServiceMantle.OpenTelemetry.Otlp.OtlpRegistration` | `ServiceMantle.Diagnostics.Export.Otlp.OtlpRegistration` |
| `ServiceMantle.OpenTelemetry.Otlp.OtlpSignalConfiguration` | `ServiceMantle.Diagnostics.Export.Otlp.OtlpSignalConfiguration` |
| `ServiceMantle.OpenTelemetry.Otlp.OtlpRuntime` | `ServiceMantle.Diagnostics.Export.Otlp.OtlpRuntime` |
| `ServiceMantle.OpenTelemetry.Otlp.OtlpOptionsConfigurator` | `ServiceMantle.Diagnostics.Export.Otlp.OtlpOptionsConfigurator` |
| `ServiceMantle.OpenTelemetry.Otlp.OtlpStartupValidator` | `ServiceMantle.Diagnostics.Export.Otlp.OtlpStartupValidator` |
| `ServiceMantle.OpenTelemetry.Otlp.OtlpNames` | `ServiceMantle.Diagnostics.Export.Otlp.OtlpNames` |
| `ServiceMantle.OpenTelemetry.Prometheus.PrometheusRegistration` | `ServiceMantle.Diagnostics.Export.Prometheus.PrometheusRegistration` |
| `ServiceMantle.OpenTelemetry.Prometheus.PrometheusSnapshot` | `ServiceMantle.Diagnostics.Export.Prometheus.PrometheusSnapshot` |
| `ServiceMantle.OpenTelemetry.Prometheus.PrometheusSnapshotProvider` | `ServiceMantle.Diagnostics.Export.Prometheus.PrometheusSnapshotProvider` |
| `ServiceMantle.OpenTelemetry.Prometheus.PrometheusExporterOptionsPolicy` | `ServiceMantle.Diagnostics.Export.Prometheus.PrometheusExporterOptionsPolicy` |
| `ServiceMantle.OpenTelemetry.Prometheus.PrometheusEndpointState` | `ServiceMantle.Diagnostics.Export.Prometheus.PrometheusEndpointState` |
| `ServiceMantle.OpenTelemetry.Prometheus.PrometheusEndpointMetadata` | `ServiceMantle.Diagnostics.Export.Prometheus.PrometheusEndpointMetadata` |
| `ServiceMantle.OpenTelemetry.Prometheus.PrometheusScrapeGate` | `ServiceMantle.Diagnostics.Export.Prometheus.PrometheusScrapeGate` |
| `ServiceMantle.OpenTelemetry.Prometheus.PrometheusStartupValidator` | `ServiceMantle.Diagnostics.Export.Prometheus.PrometheusStartupValidator` |

类型名全部保留（`OpenTelemetryOptions`、`OtlpOptions` 等协议与模块词是调用方同时引用多个同类
options 时的唯一区分；`Otlp*` / `Prometheus*` 前缀诚实暴露协议契约归属）。

### 名称敏感反射与消费编译

- 三个测试项目（`ServiceMantle.OpenTelemetry.Tests|Otlp.Tests|Prometheus.Tests`）的程序集名与
  namespace 保持不变（`InternalsVisibleTo` 字符串契约零变化，含核心包指向程序集名
  `ServiceMantle.OpenTelemetry` 的既有条目）；其中的 `using` 与 `typeof(...).Namespace` 断言随新
  namespace 同步，两个 `PackageDependencyBoundaryTests` 的测试名从
  `with_its_namespace_unchanged` 改述为 `under_its_dedicated_namespace`（断言对象从"合并程序集后
  namespace 不变"变为"合并程序集内 namespace 独立"，装配名断言不变）。
- `tests/ServiceMantle.ReferenceService.Tests` 的 namespace 前缀断言（`IsOtlpOwned`、
  `IsPrometheusOwned`、`IsExporterOwned`）随新 namespace 同步；`ReferencePackageSmokeTests` 的
  装配名断言与 `ReferenceServiceTests` 的 csproj 路径为装配/路径契约，不变。
- `eng/tests/consumers` 的 `composed` 项目三个 using 随新 namespace 更新并继续同时 using 所需框架
  namespace；`opentelemetry` 项目保持不 using 任何适配包自有 namespace 的既有约束，其约束注释随
  本切片改述为新 namespace 名（`ServiceMantle.Diagnostics.Instrumentation` /
  `.Export.Otlp`）；`provider-neutral` 项目仅持有包引用，包 ID 不变，无需改动。
