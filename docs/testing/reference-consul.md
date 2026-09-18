# 参考服务 Consul 注册接线

跟踪 [#159](https://github.com/philfanzhou/ServiceMantle/issues/159)。本文描述参考示例把阶段
Readiness 接到可选 Consul 注册生命周期的接线。Consul 能力自身的状态规则、重试矩阵与取消语义
以 [consul-registration-lifecycle.md](../contracts/consul-registration-lifecycle.md) 为唯一来源，
本文不复制它们，只记录样例接线可观察的结果与调用方责任。

## 开关与身份

| 配置键 | 缺省 | 说明 |
| --- | --- | --- |
| `ReferenceService:Consul:Enabled` | `false` | 只有解析为 `true` 才接线，`Build` 之前固定。 |
| `ReferenceService:InstanceId` | `reference-local` | 可选实例身份，`InstanceId.Parse` 校验；非法值启动失败且消息只含配置键。多实例部署必须为每个实例提供不同值。 |
| `ReferenceService:Consul:AdvertisedAddress` | 无 | 可选实例级宣告地址，仅在开关开启时读取。必须与 `AdvertisedPort` 成对出现。 |
| `ReferenceService:Consul:AdvertisedPort` | 无 | 可选实例级宣告端口（整数，`NumberStyles.None` + 不变文化解析），仅在开关开启时读取。必须与 `AdvertisedAddress` 成对出现。 |

开关开启而 PostgreSQL gate 未开启 → 在任何注册之前抛出 `InvalidOperationException`，消息只含
两个配置键。开关关闭时不调用 `AddServiceMantleConsul`，容器内没有
`ConsulRegistrationLifecycle`、`ConsulClientProvider`、`IConsulClientFactory`，`discovery.*`
定义不进入设置目录（仍只有 3 个 `workspace.*` 键），SQLite 与骨架路径不变。

## 实例级宣告地址与端口

跟踪 [#533](https://github.com/philfanzhou/ServiceMantle/issues/533)。开关开启时，样例读取上表
两个可选键并接到 `AddServiceMantleConsul(configureLifecycle: null, configureAdvertisement: …)`：

| 配置 | 启动 | 注册宣告 |
| --- | --- | --- |
| 开关关闭（无论是否配置宣告键） | 不变（两个键不读取、不校验） | 无 Consul |
| 开关开启，两键缺失 | 成功 | 服务级 `discovery.address`/`discovery.port`（与 #159 一致） |
| 开关开启，两键合法 | 成功 | 实例级 address/port，`HealthUri` 指向实例端点 |
| 只配一项，或端口非纯整数字符 | 注册前失败，`InvalidOperationException` 消息只含两个键名 | 无 |
| 两键存在但共享规则不通过（如端口 `0`） | 共享层 `ConsulConfigurationException(InvalidConfiguration)` 使启动失败 | 无 |

宣告值按进程固定，捕获进每个 client session，不热重载。多实例部署必须为每个实例配置不同的
`ReferenceService:InstanceId` 与各自的宣告地址端口（调用方责任）；宣告地址的可达性与正确性不
在样例保证范围内。

## 接线顺序

1. gate 分支内、`AddServiceMantleSettingSnapshots` 之后：注册
   `ReferenceSettingSnapshotActivation`（hosted service）与 `AddServiceMantleConsul()`（默认
   计时，不新增计时配置键）。
2. hosted service 注册顺序固定：gate → 快照激活 → Consul 生命周期。激活步骤在 gate 迁移完
   数据库之后运行，恰好调用一次 `ServiceSettingSnapshotLoader.RefreshAsync`；失败以只含错误码
   的固定消息让启动失败，调用方取消原样传播。没有后台刷新：`discovery.*` 全部
   `requiresRestart`，运行中写入的新版本不重新绑定，重启后才生效。
3. 样例的设置定义注册表改为同时接收 `IServiceSettingCompositeValidator`，因此
   `discovery.enabled=true` 时 endpoint、service-name、address、port 缺失或不合法会在
   `POST /management/v1/settings` 被组合校验拒绝，而不是等重启时 `CreateClient` 才失败。
4. `IServiceReadinessDecisionSource` 复用 `AddServiceMantleHealthEndpoints` 注册的同一实现，
   不另建判定；健康路径沿用定义默认值 `discovery.health-path=/health/ready`、
   `discovery.health-scheme=http`。

## 结果矩阵（样例接线可观察）

| 事件 | 注册/快照 | factory `Create` 与远程调用 | 宿主 |
| --- | --- | --- | --- |
| 开关关闭 | 无 Consul 类型与 `discovery.*` 定义 | 0 | 启动行为不变 |
| 开关开启、gate 关闭 | 在任何注册前失败 | 0 | 启动失败，消息只含配置键 |
| 快照激活失败（如密文无法解密） | 无 | 0 | 启动失败，消息只含错误码 |
| `discovery.enabled=false`（默认） | 快照激活 | `Create` 0 次，无采样器/timer/循环 | 正常启动，生命周期 `Disabled` |
| 开启且配置合法、未 Ready（PendingSetup 或无工作区） | 快照激活 | `Create` 1 次；注册 0 次 | 正常 |
| 变为 Ready | —— | 以 `reference-service:<instanceId>` 注册恰 1 次 | 正常 |
| 失去 Ready（工作区被删除等） | —— | 对同一 ID 注销 | 正常 |
| 注册返回 `Unavailable`/`Rejected` | —— | 按契约退避重试，恢复后恰好已注册 | 正常 |
| 宿主停止（已注册） | —— | 关闭预算内对同一 ID 注销，session 处置 | 停止 |
| 运行中更新 `discovery.*` | 新版本写入但不重新绑定 | 不变，重启后生效 | 正常 |
| 组合不完整的 `discovery.*` 更新 | 被组合校验拒绝，不写入 | 不变 | 正常 |

## 已写入 `discovery.*` 后关闭开关

开关开启期间写入的 `discovery.*` 行在关闭开关后成为未知键：设置查询与更新返回
`configuration.snapshot_unknown_key`，快照激活失败。**关闭开关前必须先删除这些行**（调用方
责任）。不选择「无条件注册定义」，因为那会改变所有 gate 路径的公开设置目录。回滚本接线同理：
回滚前按 `consul-registration-lifecycle.md` 的停机步骤删除 `discovery.*` 行。

## 非保证与调用方责任

- 保证：上表结果；token 明文不进入样例与 ServiceMantle 拥有的日志、异常与响应；开关关闭时零
  Consul 类型、零连接。
- 不保证：真实 Consul agent 的传播、健康检查与 ACL 行为（
  [#176](https://github.com/philfanzhou/ServiceMantle/issues/176) 用真实 Consul 验证）；两实例
  宣告地址区分（[#532](https://github.com/philfanzhou/ServiceMantle/issues/532)）；计时可配置；
  运行中设置热重载；`consul-registration-lifecycle.md` 已声明的全部非保证。
- 调用方责任：写入合法 `discovery.*` 后重启；为多实例部署配置不同的
  `ReferenceService:InstanceId`；保护根密钥使 credential 能解密；关闭开关前删除已写入的
  `discovery.*` 行。

## 测试

`tests/ServiceMantle.ReferenceService.Tests/ReferenceConsulStartupTests.cs`（无数据库）：
D1 关闭路径零 Consul 类型与 3 键目录、D2 配置校验（consul 无 gate、非法实例 ID）、G3 宣告
配置错误（只配一项、端口非整数 → 消息只含键名；端口 `0` → 共享层
`ConsulConfigurationException`）、G4 开关关闭时宣告键不读取。
`ReferenceConsulTests.cs`（真实 PostgreSQL + 记录型 `IConsulClientFactory`）：D3 禁用零客户端、
D4 组合校验与密文落库、D5 激活失败只含错误码、D6 就绪门控注册与实例 ID、D7 失去/恢复就绪、
D8 重试无重叠、D9 停止注销与 session 处置、D10 不热重载、D11 token canary、G1 同一数据库两
实例各自宣告 Id/Address/Port/HealthUri、G2 缺省回退服务级值、G4 开关关闭不受宣告键影响。
