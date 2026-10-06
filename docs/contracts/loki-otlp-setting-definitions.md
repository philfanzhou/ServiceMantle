# Loki 与 OTLP 设置定义（#602、#663）

状态：已实现。实现位于 `ServiceMantle.Logging`（Loki）与 `ServiceMantle.Diagnostics`（OTLP）。

## 决策

远程日志端点与 OTLP 端点的「设置键 → 校验分类 → 启用或禁用 → 启动后写警告」由库统一提供，
消费方不再自写这层。与 `ConsulSettingDefinitions` 同构：实现
`IServiceSettingDefinitionProvider` 的类型同时提供 `IServiceSettingCompositeValidator` 的
**严格管理更新规则**；键名、值类型与敏感性由库固定；不新增任何读取 `IConfiguration` 的路径。

## 键表

| 键 | 类型 | 敏感 | 含义 |
| --- | --- | --- | --- |
| `loki.uri` | String | 否 | Loki 基础端点：默认 HTTPS，显式允许后可用 HTTP；绝对 URI、有 host，无 user-info、query、fragment。 |
| `loki.authorization` | String | 是 | Authorization 头值：1–4096 字符、无控制字符；受保护存储，无明文默认。 |
| `loki.allow_insecure_http` | Boolean | 否 | 缺省 `false`；明确接受任意 host 的 HTTP，不自动信任回环、私网或容器名。 |
| `loki.allow_no_authentication` | Boolean | 否 | 缺省 `false`；明确选择无认证，完整候选中必须删除 `loki.authorization`。 |
| `opentelemetry.otlp_endpoint` | String | 否 | OTLP 端点：绝对 HTTPS URI，无 user-info、query、fragment。 |

## 两层规则

- **严格更新规则**（`IServiceSettingCompositeValidator`，挂在管理更新目录上）：保存的值必须是
  下一次启动可用的值。Loki 拒绝未经明确允许的 HTTP 或结构非法端点（`loki.invalid_endpoint`）、不可用的 Authorization
  值或无认证选择与仍保存的凭据冲突（`loki.authorization_value_invalid`），认证模式只配置一半时缺失的一侧报 `setting.required`；
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
  的校验规则启用 sink；认证模式注册恰好一个内存内的 `FixedRemoteLogAuthorizationResolver`（固定名
  `servicemantle-loki-settings`），明确无认证则 resolver name 为 null、不注册/枚举/构造/调用 resolver、不发送 Authorization；空对或不可用对保持禁用——不注册 resolver、不替换 sink factory、
  不产生任何网络或后台活动。分类结果 owns `Enabled`、`Endpoint`、
  `AuthorizationHeaderResolverName`、`AllowInsecureHttp` 与 `AllowInsecureLoopbackForTesting`（快照路径恒为 false）；configure 只管批量、标签与超时等其余选项。
- `AddOpenTelemetryOtlpExporterFromSettings(builder, snapshot, configure?)`：可用端点经既有
  `AddOpenTelemetryOtlpExporter` 启用，分类为每个已启用信号注入端点；空或不可用值不注册任何
  exporter、provider 或后台活动（即使 configure 启用了信号）。信号选择（traces/metrics）始终
  由消费方在 configure 中决定。

两个入口都向全局设置目录贡献定义与严格校验器；重复等价注册幂等，遵循底层显式入口自己的
幂等与冲突规则（Loki 的等价注册幂等、不等价注册在启动时报 `loki.conflicting_registration`）。

## Loki 完整候选与启动矩阵

本表解释 [#663](https://github.com/philfanzhou/ServiceMantle/issues/663) 的唯一策略模型。
E 为端点，A 为认证值，H/N 为两个 Boolean。四个 Loki 键均 `RequiresRestart`。
新 Boolean 先经核心解析；非法文字报 `setting.invalid_boolean`，持久化类型错误使加载失败并保留当前快照，不能降级默认。

| 输入 | 严格管理更新 | 快照分类 / 注册 |
| --- | --- | --- |
| E/A 均缺失，H/N 合法 | 接受关闭配置 | `disabled`，旗标本身不启动网络 |
| E 缺失，A 非空 | E 上 `setting.required` | `endpoint_missing` |
| E 非法，或 HTTP 且 H=false（含回环） | E 上 `loki.invalid_endpoint` | `endpoint_invalid`，callback 不能启用 |
| HTTPS，或 HTTP 且 H=true；N=false、A 缺失 | A 上 `setting.required` | `authorization_missing` |
| 可用 E；N=false、A 合法 | 接受 | `enabled`，固定 resolver，发送原 Authorization |
| 可用 E；N=false、A 非法 | A 上 `loki.authorization_value_invalid` | `authorization_invalid`；旧空白 A 保留 `authorization_missing` 宽容分类 |
| 可用 E；N=true、A 已删除 | 接受 | `enabled`，无 resolver、无 Authorization |
| 可用 E；N=true、仍存 A（含空串/空白） | A 上 `loki.authorization_value_invalid` | `authorization_invalid`，不能静默忽略凭据 |

端点缺失/非法优先于认证问题；严格更新可以返回多个安全错误。旧空白 E/A 在宽容启动与严格更新间的差异保留。
HTTPS+认证、HTTP+认证、HTTPS+无认证、HTTP+无认证四种成功组合独立可达，H 不决定认证。
默认 raw `Classify(string?, string?)` / `TryParseEndpoint(string?, out Uri?)` 的二进制签名和严格策略不变；新增重载显式接收策略。

## 保存、迁移与回滚

管理 `ServiceSettingUpdateService.UpdateAsync` 对完整候选校验，敏感 A 保持 `sm:v1:` 信封，audit 仅含 key；
成功只在调用方事务中暂存，库不 commit、不发布新运行快照。调用方 commit 后版本递增一次、记录 restart；
下一进程通过 store/source/typed loader 加载完整版本，再分类与注册 immutable sink。发送沿现有 sanitizer、
有界队列、prefix + `/loki/api/v1/push`、排空及 content-free diagnostics 契约；取消与失败优先级不变。

旧 HTTPS+A 且无新键保持兼容：缺失或删除 H/N 恢复 false，缺 A 不自动选择无认证。启用 HTTP 明确保存 H=true；
改为无认证必须同一版本 batch 设置 N=true 并删除 A。恢复认证必须同一候选删除/关闭 N 并补回合法 A。
关闭 sink 同 batch 删除 E/A，合法 H/N 可留存；保存不会热改变前一快照或运行 sink。重复等价注册幂等，
不同有效端点、认证模式或 batch 配置仍按 `loki.conflicting_registration` 拒绝；同固定名 resolver 重复捕获值
仍保留首次值，不新增凭据热旋转保证。

配置回滚到 HTTPS+认证：同候选恢复 HTTPS 和有效 A，删除/关闭 H/N，commit 后重启。
降级到不认识新键的旧包前，先用新包原子删除 H/N 并恢复 HTTPS+A 或关闭 E/A，确认 committed raw store
不存在新键后再降级；旧 loader 拒绝 unknown key 是预期边界，不是透明二进制降级。无需 schema/migration，
旧 keys、purpose、route、包 ID 和 namespace 均不变。发布及消费方升级独立执行，产品只用正式官方包。

## 非保证

- HTTP 明确接受日志及可能认证头的明文传输；不保证链路机密性、完整性或网络可信性，调用方负责网络与访问权限。
- 不保证远端可达、持久化或可靠消息存储：沿用上游 sink/exporter 投递语义。
- 设置变更需重启生效；无热加载。
- 未配置时零副作用仅覆盖本注册路径；消费方绕过入口自行注册的能力不受约束。
- 严格更新规则只约束挂载了该校验器的目录；消费方若把启动加载与管理更新共用一个目录，
  旧版本存储的不可用值将无法通过加载——这是消费方的挂载选择，不是库的行为变化。
