# Naming migration

ServiceMantle now derives a type's module from its directory and namespace, and names the type after
what it does. The product name is no longer repeated in ordinary type and file names.

This is a source and binary breaking change for every renamed type. Nothing else changed: no
signature, no default, no runtime behaviour, no package boundary, no assembly name, and no package
identifier. No previously published version is overwritten; the renames ship in a later version and
are listed here in full.

## The rules

1. A type's namespace is its project's root namespace plus its functional subdirectory. Ordinary
   types carry no product prefix, because the namespace already carries it.
2. Extension entry points keep their framework namespace (`Microsoft.Extensions.DependencyInjection`,
   `Microsoft.Extensions.Hosting`, `Microsoft.AspNetCore.*`) and keep the product prefix in both the
   class name and the method name - `AddServiceMantle*`, `UseServiceMantle*`, `MapServiceMantle*`,
   `WithServiceMantle*`. Those namespaces say nothing about the product, so the name is the only
   thing that separates a ServiceMantle entry point from the framework's own.
3. A file is named after its main type. Public types that belong to one contract family stay in that
   family's file, as they did before.

## Exceptions, and why

| Type | Kept or renamed as | Reason |
| --- | --- | --- |
| `ServiceMantle.AspNetCore.ServiceMantleBuilder` | unchanged | It is the type `AddServiceMantle` returns and the receiver every `AddServiceMantle*` extension hangs off, so it is named for the product like those entry points. `Builder` would also collide with the `Microsoft.AspNetCore.Builder` namespace. |
| `ServiceMantleHeaderNames` | `ServiceHeaderNames` | `HeaderNames` collides with `Microsoft.Net.Http.Headers.HeaderNames`. `Service*` is the repository's existing family for "this host service's X" (`ServiceLogContext`, `ServiceLogFieldNames`, `ServiceHealthSnapshot`). |
| `ServiceMantleMetrics` | `ServiceMetrics` | `Metrics` collides with the `System.Diagnostics.Metrics` namespace. |
| `ServiceMantleForwardedHeadersOptions` | `ForwardedHeadersTrustOptions` | `ForwardedHeadersOptions` collides with `Microsoft.AspNetCore.Builder.ForwardedHeadersOptions`, which a web application imports implicitly. The new name states the responsibility: the explicit trust boundary. |
| `IServiceMantleDbContext` | `IServiceDbContext` | `IDbContext` is a name consuming applications commonly define for themselves. |
| `ServiceMantleRegistration` | `HostRegistration` | `Registration` alone says nothing; the record marks the host identity fixed by `AddServiceMantle`. |
| `ServiceMantleSerilogLoggerProvider` | `RuntimeLoggerProvider` | `SerilogLoggerProvider` collides with `Serilog.Extensions.Logging.SerilogLoggerProvider`, which this type wraps. |
| `IServiceMantleStructuredLogSanitizer` / `ServiceMantleStructuredLogSanitizer` | `ILogFieldSanitizer` / `LogFieldSanitizer` | `StructuredLogSanitizer` is the core type these adapt. |

## What did not change

These are external contracts. A type rename must not move them, and none of them moved:

- Log category strings `ServiceMantle.Http.CorrelationId`, `ServiceMantle.Http.ProblemDetails`,
  `ServiceMantle.Http.RateLimiting`.
- Authentication scheme and policy names `ServiceMantle.ManagementCookie`,
  `ServiceMantle.ManagementAdmin`, `ServiceMantle.ManagementSession`.
- The Data Protection purpose `ServiceMantle.Management:<serviceId>`.
- The meter name `ServiceMantle`, its version, and every metric name.
- OTLP option section names `ServiceMantle.Otlp.Traces` and `ServiceMantle.Otlp.Metrics`.
- Every HTTP route, header name, JSON field, error code, configuration key, database table and
  column, migration lock key prefix, package identifier, assembly name, and `InternalsVisibleTo`
  target.

Two diagnostics do name a type and therefore changed with it: reflection-by-name assertions over
`HealthStartupValidator` and `SensitiveHeaderStartupValidator`. Both are assembly-internal
registrations, not a published contract.

## Namespace moves

| Old namespace | New namespace |
| --- | --- |
| `ServiceMantle.Http` | `ServiceMantle.AspNetCore.Http` |
| `ServiceMantle.AspNetCore` (types under `Http/`) | `ServiceMantle.AspNetCore.Http` |
| `ServiceMantle.AspNetCore` (types under `Logging/`) | `ServiceMantle.AspNetCore.Logging` |
| `ServiceMantle.Logging` (types in `ServiceMantle.AspNetCore`) | `ServiceMantle.AspNetCore.Logging` |
| `ServiceMantle.Management` (types in `ServiceMantle.AspNetCore`) | `ServiceMantle.AspNetCore.Management`, `ServiceMantle.AspNetCore.ManagementApi[.<Group>]` |
| `ServiceMantle.AspNetCore` (types under `ManagementApi/`) | `ServiceMantle.AspNetCore.ManagementApi[.<Group>]` |
| `ServiceMantle.AspNetCore` (types under `PhaseGate/`) | `ServiceMantle.AspNetCore.PhaseGate` |
| `ServiceMantle.AspNetCore` (types under `RateLimiting/`) | `ServiceMantle.AspNetCore.RateLimiting` |

`ServiceMantle.Logging` and `ServiceMantle.Management` still exist: they are the core package's own
namespaces. Only the `ServiceMantle.AspNetCore` types that used to share them moved, so one namespace
no longer spans two assemblies.

The core, Consul, database provider, and EF Core persistence packages keep their namespaces
unchanged.

## Public API map

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
| `ServiceMantle.Persistence.EntityFrameworkCore.ServiceMantleDataProtectionBuilderExtensions` | `ServiceMantle.Persistence.EntityFrameworkCore.DataProtectionBuilderExtensions` |
| `ServiceMantle.Persistence.EntityFrameworkCore.IServiceMantleDbContext` | `ServiceMantle.Persistence.EntityFrameworkCore.IServiceDbContext` |
| `ServiceMantle.Serilog.ServiceMantleSerilogConfigurationException` | `ServiceMantle.Serilog.SerilogConfigurationException` |
| `ServiceMantle.Serilog.ServiceMantleSerilogDefaults` | `ServiceMantle.Serilog.SerilogDefaults` |
| `ServiceMantle.Serilog.ServiceMantleSerilogOptions` | `ServiceMantle.Serilog.SerilogOptions` |
| `ServiceMantle.Serilog.ServiceMantleSerilogPackage` | `ServiceMantle.Serilog.SerilogPackage` |
| `ServiceMantle.Serilog.GrafanaLoki.ServiceMantleGrafanaLokiDefaults` | `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiDefaults` |
| `ServiceMantle.Serilog.GrafanaLoki.ServiceMantleGrafanaLokiDiagnostics` | `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiDiagnostics` |
| `ServiceMantle.Serilog.GrafanaLoki.ServiceMantleGrafanaLokiOptions` | `ServiceMantle.Serilog.GrafanaLoki.GrafanaLokiOptions` |
| `ServiceMantle.Serilog.GrafanaLoki.IServiceMantleLokiAuthorizationHeaderResolver` | `ServiceMantle.Serilog.GrafanaLoki.ILokiAuthorizationHeaderResolver` |
| `ServiceMantle.Serilog.GrafanaLoki.WellKnownServiceMantleGrafanaLokiErrorCodes` | `ServiceMantle.Serilog.GrafanaLoki.WellKnownGrafanaLokiErrorCodes` |

## Internal type map

These are assembly-internal. They are listed because `InternalsVisibleTo` test assemblies and
reflection-by-name diagnostics see them.

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

## Test class map

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
