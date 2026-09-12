# 命名迁移说明

ServiceMantle 现在由目录和 namespace 表达一个类型属于哪个模块，由类型名表达它做什么。普通类型和
文件名里不再重复产品名。

对每一个被改名的类型，这都是源码与二进制破坏性变更。除此之外没有任何变化：签名、默认值、运行时
行为、包边界、程序集名和包 ID 都不变。已发布的版本不会被覆盖，改名在后续新版本中交付，本文完整
列出全部变更。

## 规则

1. 类型的 namespace 是它所在项目的根 namespace 加功能子目录。普通类型不带产品前缀，因为 namespace
   已经带了。
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
| `ServiceMantle.OpenTelemetry.ServiceMantleOpenTelemetryOptions` | `ServiceMantle.OpenTelemetry.OpenTelemetryOptions` |
| `ServiceMantle.OpenTelemetry.ServiceMantleMetrics` | `ServiceMantle.OpenTelemetry.ServiceMetrics` |
| `ServiceMantle.OpenTelemetry.ServiceMetrics` | `ServiceMantle.Diagnostics.ServiceMetrics` |
| `ServiceMantle.OpenTelemetry.Otlp.IServiceMantleOtlpAuthenticationHeaderResolver` | `ServiceMantle.OpenTelemetry.Otlp.IOtlpAuthenticationHeaderResolver` |
| `ServiceMantle.OpenTelemetry.Otlp.ServiceMantleOtlpAuthenticationHeader` | `ServiceMantle.OpenTelemetry.Otlp.OtlpAuthenticationHeader` |
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
