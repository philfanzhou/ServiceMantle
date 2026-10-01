# Loki 与 OTLP 设置定义（#602）

状态：已实现。实现位于 `ServiceMantle.Logging`（Loki）与 `ServiceMantle.Diagnostics`（OTLP）。

## 决策

远程日志端点与 OTLP 端点的「设置键 → 校验分类 → 启用或禁用 → 启动后写警告」由库统一提供，
消费方不再自写这层。与 `ConsulSettingDefinitions` 同构：实现
`IServiceSettingDefinitionProvider` 的类型同时提供 `IServiceSettingCompositeValidator` 的
**严格管理更新规则**；键名、值类型与敏感性由库固定；不新增任何读取 `IConfiguration` 的路径。

## 键表

| 键 | 类型 | 敏感 | 含义 |
| --- | --- | --- | --- |
| `loki.uri` | String | 否 | Loki 基础端点：绝对 HTTPS URI，无 user-info、query、fragment。 |
| `loki.authorization` | String | 是 | Authorization 头值：1–4096 字符、无控制字符；受保护存储，无明文默认。 |
| `opentelemetry.otlp_endpoint` | String | 否 | OTLP 端点：绝对 HTTPS URI，无 user-info、query、fragment。 |

## 两层规则

- **严格更新规则**（`IServiceSettingCompositeValidator`，挂在管理更新目录上）：保存的值必须是
  下一次启动可用的值。Loki 拒绝非 HTTPS 端点（`loki.invalid_endpoint`）、不可用的 Authorization
  值（`loki.authorization_value_invalid`）与只配置一半的组合（缺失的一侧报 `setting.required`）；
  OTLP 拒绝非空且不合法的端点（`otlp.invalid_endpoint`）。
- **宽容启动分类**（`GrafanaLokiSettingState.Classify` / `OtlpSettingState.Classify`，消费方把
  启动加载目录与管理更新目录分开挂载，正如既有消费方的做法）：旧版本已存储的不可用值不使启动
  失败，而是能力禁用并返回固定分类供启动后写警告。Loki 分类为
  `disabled / enabled / endpoint_invalid / endpoint_missing / authorization_missing /
  authorization_invalid`；OTLP 分类为 `enabled / disabled / endpoint_invalid`。

分类结果、异常与诊断不包含端点值、Authorization 值或 URI 的 user-info/query；`ToString` 与
警告类别只输出类别名。

## 快照驱动注册

- `AddServiceMantleGrafanaLokiFromSettings(builder, snapshot, configure?)`：可用对按既有显式入口
  的校验规则启用 sink，并注册恰好一个内存内的 `FixedRemoteLogAuthorizationResolver`（固定名
  `servicemantle-loki-settings`）；空对或不可用对保持禁用——不注册 resolver、不替换 sink factory、
  不产生任何网络或后台活动。分类结果 owns `Enabled`、`Endpoint` 与
  `AuthorizationHeaderResolverName`；configure 只管批量、标签与超时等其余选项。
- `AddOpenTelemetryOtlpExporterFromSettings(builder, snapshot, configure?)`：可用端点经既有
  `AddOpenTelemetryOtlpExporter` 启用，分类为每个已启用信号注入端点；空或不可用值不注册任何
  exporter、provider 或后台活动（即使 configure 启用了信号）。信号选择（traces/metrics）始终
  由消费方在 configure 中决定。

两个入口都向全局设置目录贡献定义与严格校验器；重复等价注册幂等，遵循底层显式入口自己的
幂等与冲突规则（Loki 的等价注册幂等、不等价注册在启动时报 `loki.conflicting_registration`）。

## 非保证

- 不保证远端可达或投递成功：沿用上游 sink/exporter 语义。
- 设置变更需重启生效；无热加载。
- 未配置时零副作用仅覆盖本注册路径；消费方绕过入口自行注册的能力不受约束。
- 严格更新规则只约束挂载了该校验器的目录；消费方若把启动加载与管理更新共用一个目录，
  旧版本存储的不可用值将无法通过加载——这是消费方的挂载选择，不是库的行为变化。
