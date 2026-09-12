# SignaCore legacy 替换审计

本审计是 ServiceMantle issue #105 的删除门，也是分阶段移除 Workstream #106 的来源。它对 SignaCore 管理基础设施代码进行盘点，但不删除它，也不改变任何 ServiceMantle 公开契约。

被审计的源码是 [`philfanzhou/SignaCore@23c2f666`](https://github.com/philfanzhou/SignaCore/tree/23c2f666726186b772737988fae8ba8a8ce1f2da)。该确切 commit 通过了其完整的 [GitHub CI 运行](https://github.com/philfanzhou/SignaCore/actions/runs/32585823676)，包括单元、集成/HTTP 契约、PostgreSQL、前端和容器冒烟路径。`manifest.json` 记录了用作现状行为证据的可执行命令和具名测试。

## 决策规则

1. 一个删除候选必须指明确切的旧路径或已验证的符号、至少一个可执行的行为测试、有且只有一个 issue 或点分标识符替换引用，以及它的前置 issue。
2. 计划中的替换或任何覆盖缺口都会强制 `disposition=blocked`。在替换存在且缺口被可执行测试关闭之前，后续 PR 不能把某一项标记为 ready。
3. 一个删除批次必须恰好包含其子系统内的每一个候选一次，在其原生 `Blocked by` 集合中直接强制每个候选的前置条件，并被前一批次阻塞。manifest 测试强制这种分区、前置条件包含关系和顺序。
4. 产品拥有的边界被保留。删除路径或符号不得包含、等于或位于某个被保留的路径或符号之下；被保留的符号被显式记录而不是从文件名推断，并且方法级拆分是显式的（例如管理审计动作可以移动，而登录历史保留）。
5. 每个被保留的边界都声明其覆盖模式。`path-and-symbol` 要求非空的 `paths` **和**非空的 `preservedSymbols`；`path-only` 要求非空的 `paths` 和空的 `preservedSymbols`。`path-only` 是为诸如管理控制台这类非 C# 边界而存在的；不得用它来逃避某个边界实际需要的符号要求。
6. 当 SignaCore 变化时，固定的源码 commit 必须被有意地推进并重新审计。来自移动分支的证据不被接受。

## 删除顺序

| 顺序 | 子系统 | 候选 ID | 唯一替换/集成门 | 跟踪 issue |
|---:|---|---|---|---|
| 1 | Migration | `migration-orchestration`、`installation-state-persistence` | #70、#71 | #128 |
| 2 | Bootstrap | `bootstrap-file-lifecycle`、`bootstrap-management-mode`、`bootstrap-startup-branch` | `BootstrapFileStore`、#95、#116 | #129 |
| 3 | Setup | `setup-lifecycle`、`setup-mode-gate` | #100、#116 | #130 |
| 4 | Configuration | `configuration-catalog`、`configuration-snapshot-storage`、`configuration-management-api`、`legacy-configuration-upgrade` | #101 | #131 |
| 5 | Audit | `management-audit-storage`、`management-audit-query-api` | `EfCoreManagementAuditWriter`、#98 | #132 |
| 6 | Session | `admin-data-protection-key-store`、`admin-cookie-session` | #102 | #133 |
| 7 | Health | `phase-health-endpoints`、`signing-key-readiness` | #103 | #134 |
| 8 | Consul | `consul-registration-lifecycle` | #104 | #135 |

这八行按子系统对历史盘点进行分组；它们不是八个等量的工作单元。全部八个跟踪 issue 都是 [#106](https://github.com/philfanzhou/ServiceMantle/issues/106) 的原生子 issue，但其中三个——[#129](https://github.com/philfanzhou/ServiceMantle/issues/129)、[#131](https://github.com/philfanzhou/ServiceMantle/issues/131) 和 [#135](https://github.com/philfanzhou/ServiceMantle/issues/135)——是 Workstream，而不是任何人都可以领取的 issue。实现发生在它们最深层的任务中：

| Workstream | 可领取的最深层任务 |
| --- | --- |
| [#129](https://github.com/philfanzhou/ServiceMantle/issues/129)（Bootstrap） | [#163](https://github.com/philfanzhou/ServiceMantle/issues/163)、[#168](https://github.com/philfanzhou/ServiceMantle/issues/168)、[#162](https://github.com/philfanzhou/ServiceMantle/issues/162) |
| [#131](https://github.com/philfanzhou/ServiceMantle/issues/131)（Configuration） | [#143](https://github.com/philfanzhou/ServiceMantle/issues/143)、[#144](https://github.com/philfanzhou/ServiceMantle/issues/144)、[#145](https://github.com/philfanzhou/ServiceMantle/issues/145)、[#146](https://github.com/philfanzhou/ServiceMantle/issues/146) |
| [#135](https://github.com/philfanzhou/ServiceMantle/issues/135)（Consul） | [#147](https://github.com/philfanzhou/ServiceMantle/issues/147)、[#148](https://github.com/philfanzhou/ServiceMantle/issues/148) |

其余五行——[#128](https://github.com/philfanzhou/ServiceMantle/issues/128)、[#130](https://github.com/philfanzhou/ServiceMantle/issues/130)、[#132](https://github.com/philfanzhou/ServiceMantle/issues/132)、[#133](https://github.com/philfanzhou/ServiceMantle/issues/133) 和 [#134](https://github.com/philfanzhou/ServiceMantle/issues/134)——是可直接实现的任务。

GitHub 原生的 `Sub-issues` 和 `Blocked by` 关系是执行依赖的事实来源；本文的表格不是。这些关系把能力前置条件与前一个删除批次混在一起，并且每个 Workstream 的 `Blocked by` 集合恰好是它自己的子 issue，因此 Workstream 在依赖图中是透明的，而不是独立的一步。

## 删除后验收

[#107](https://github.com/philfanzhou/ServiceMantle/issues/107) 作为整体只有在 [#106](https://github.com/philfanzhou/ServiceMantle/issues/106) 和全部八个删除批次完成之后才关闭，并且 `manifest.json` 将该聚合边记录为 `postDeletionAcceptance`（`issue: #107`、`afterWorkstream: #106`）。它验证集成后的升级、新装、故障恢复、多实例和 bootstrap 应用预置行为；它不是任何删除候选或批次的前置条件。把这条验收边保持在下游可以避免让 [#106](https://github.com/philfanzhou/ServiceMantle/issues/106) 依赖于它自己的完成。

关闭聚合项和启动一个具体的验收任务是两个不同的层级。各个验收任务带有它们自己的原生 `Blocked by` 门，比整个 Workstream 更窄：

- P0 任务 [#152](https://github.com/philfanzhou/ServiceMantle/issues/152)、[#153](https://github.com/philfanzhou/ServiceMantle/issues/153)、[#155](https://github.com/philfanzhou/ServiceMantle/issues/155)、[#166](https://github.com/philfanzhou/ServiceMantle/issues/166)、[#167](https://github.com/philfanzhou/ServiceMantle/issues/167) 和 [#174](https://github.com/philfanzhou/ServiceMantle/issues/174) 各自只以 [#134](https://github.com/philfanzhou/ServiceMantle/issues/134) 为门。一旦 [#134](https://github.com/philfanzhou/ServiceMantle/issues/134) 完成且它们自己的前置条件成立，它们就可以开工；它们不等待 [#148](https://github.com/philfanzhou/ServiceMantle/issues/148) 中的 P1 Consul 切换。[#152](https://github.com/philfanzhou/ServiceMantle/issues/152)、[#153](https://github.com/philfanzhou/ServiceMantle/issues/153)、[#155](https://github.com/philfanzhou/ServiceMantle/issues/155) 和 [#178](https://github.com/philfanzhou/ServiceMantle/issues/178) 是 [#107](https://github.com/philfanzhou/ServiceMantle/issues/107) 的直接子 issue；[#166](https://github.com/philfanzhou/ServiceMantle/issues/166)、[#167](https://github.com/philfanzhou/ServiceMantle/issues/167) 和 [#174](https://github.com/philfanzhou/ServiceMantle/issues/174) 在故障恢复 Workstream [#154](https://github.com/philfanzhou/ServiceMantle/issues/154) 之下再深一层。
- P1 任务 [#178](https://github.com/philfanzhou/ServiceMantle/issues/178) 以 [#148](https://github.com/philfanzhou/ServiceMantle/issues/148) 为门，因此它确实要等待 Consul 切换。

manifest 的聚合记录和离线守护测试刻意停留在 [#107](https://github.com/philfanzhou/ServiceMantle/issues/107)/[#106](https://github.com/philfanzhou/ServiceMantle/issues/106) 这一层级。它们不读取 GitHub，因此这里没有任何内容断言离线守护已经验证过当前的远端任务图；上面的原生关系是从 GitHub 读取的，也是实现者在领取任务之前应当重新读取的内容。

## 显式覆盖缺口

- Legacy 配置导入：基于 ServiceMantle 的升级必须保留 legacy 键别名和导入不完整时的失败关闭行为。
- 管理会话：替换必须在旧注册被移除之前加入一次真实的双实例 Cookie 往返。
- Consul：当前证据只证明该 provider 默认被禁用。启用后的注册、就绪门控、重试、重复注册和关闭时注销都还没有可执行的行为刻画。#135 必须在切换实现之前补上这些测试。

这些缺口在 manifest 中被有意表示为阻塞数据。它们不是延后处理的评审备注。

## 保留在 SignaCore 中的产品边界

以下内容不是可复用的管理基础设施，不是删除候选：

- OAuth 授权、JWT 签发、refresh token 轮换和 token 吊销；
- 账户、凭据、锁定、用户登录和登录历史记录；
- 应用注册、网关认证和跨应用交换；
- SMS/OTP、LDAP 和微信准入/provider 行为；
- 回调注册及其 SSRF 策略；
- 签名密钥生命周期、私钥保护、JWKS 和 discovery 文档（只有就绪适配器移动）；
- SignaCore 拥有的 identity、token、application 和 provider 表的 migration；
- 管理产品 UI，其基础设施 API 调用点在原处适配（声明为 `path-only`：它不是 C# 符号空间，因此不携带占位符号）。

manifest 为每个被保留的边界列出了具体路径、显式符号根和按命令键控的行为测试。未来的清理 PR 必须把这些路径和符号保持在删除范围之外。

已应用的 migration 文件是不可变的历史。因此，移除 installation-state、system-setting、audit-log 或 data-protection-key 映射要求每个删除批次同时更新两个 provider 的模型快照并新增向前的 drop migration；它不得删除历史上的 `AddSystemSettingsAndInstallationState` 或 `PersistDataProtectionKeys` migration。那些快照映射被列为候选符号，以便可以在符号范围内编辑被保留的 migration 文件，而不暴露其旁边的产品拥有的映射。

## 模型快照实体清单

`src/SignaCore.Database/Migrations/**` 和 `src/SignaCore.Database.Migrations.Sqlite/Migrations/**` 作为整体路径被保留，因此路径门无法保护单个快照映射。那里的删除安全完全依赖符号门，而符号门的安全性只取决于其枚举的完整性。`snapshotEntitySets` 就是那个枚举。

manifest 为每个 provider 快照——PostgreSQL 和 SQLite——固定一条记录，各自记录仓库相对的快照路径、快照符号前缀，以及该快照声明的 18 个逻辑实体简单名。守护测试把 `symbolPrefix + "." + entityName` 推导为 36 个 provider 特定的审计符号，并强制：

```text
snapshotInventory == preservedSnapshotSymbols ∪ deletionCandidateSnapshotSymbols
preservedSnapshotSymbols ∩ deletionCandidateSnapshotSymbols == ∅
```

它还断言两个快照都恰好持有 18 个不重复的实体、它们的逻辑实体集合相同、两条路径和两个前缀互不相同且位于被保留的 `product-schema-migrations` 路径之下，以及一个快照符号只能被 `product-schema-migrations.preservedSymbols` 或某个候选的 `legacySymbols` 认领——绝不能被恰好重叠的无关边界认领。

这 36 个符号按 28 保留 / 8 删除划分：

| 一侧 | 每个快照的实体 |
|---|---|
| 保留（`product-schema-migrations`） | `AccountEntity`、`AppExchangeTrustEntity`、`AppLdapAccessEntity`、`AppRegistrationEntity`、`AppSmsAccessEntity`、`AppWechatAccessEntity`、`LdapCredentialEntity`、`LoginAttemptEntity`、`LoginHistoryEntity`、`OtpEntity`、`PasswordCredentialEntity`、`RefreshTokenEntity`、`SecurityKeyEntity`、`UserLoginEntity` |
| 删除候选 | `InstallationStateEntity`（`installation-state-persistence`）、`SystemSettingEntity`（`configuration-snapshot-storage`）、`AuditLogEntity`（`management-audit-storage`）、`DataProtectionKeyEntity`（`admin-data-protection-key-store`） |

`AuditLogEntity` 位于删除一侧，因为 `src/SignaCore.Database/Entity/AuditLogEntity.cs` 已经是 `management-audit-storage` 的 legacy 路径：管理审计表移动到 `EfCoreManagementAuditWriter`。如果其快照映射不被分类，就会出现实体文件被删除而其映射被留下的情况。其余 14 个是产品拥有的 identity、token、application 和 provider 表。

### 重新采集基线

`snapshotEntitySets` 是与 `source.commit` 绑定的固定审计输入。离线守护测试不访问 GitHub，也不声称固定清单与远端源码一致；它们只证明一旦清单被固定，manifest 的保留/删除划分就恰好完整覆盖它一次。推进固定 commit 而不重新采集是违反基线更新流程的行为，不是一个联网测试能够掩盖的事情。

推进 `source.commit` 时，reviewer 必须：

1. 从新 commit 下的两个快照文件中读取 `modelBuilder.Entity("...")` 调用；
2. 把每个 `snapshotEntitySets[].entities` 列表更新为在那里发现的去重实体简单名（一个实体在一个文件中可能出现多次；清单记录的是去重后的集合）；
3. 把每个新增或改名的实体分类到 `product-schema-migrations.preservedSymbols` 或某个候选的 `legacySymbols`，并移除不再存在的实体的符号；
4. 重新运行守护测试，它们会对任何遗漏、重复、双重认领或两个快照之间的分歧报错失败。

## 可执行验证

用以下命令运行 ServiceMantle 守护测试：

```bash
dotnet test --project tests/ServiceMantle.Tests/ServiceMantle.Tests.csproj
```

`SignaCoreLegacyMigrationManifestTests` 在以下情况失败：某个候选失去证据或唯一替换、必需的缺口数据被丢弃、某个缺口被标记为 ready、某个候选的前置条件没有被其匹配的子系统批次强制、删除后验收泄漏进删除门、某个批次遗漏/重复某个候选、顺序链断裂或成环、缺少跟踪任务、某个命令未被使用，或某个被保留的产品路径或符号与删除范围重叠。

它还会在以下情况失败：某个快照清单符号未被分类、被认领两次、在某个快照列表内重复，或两个快照不一致；某个快照路径或前缀重复或移出被保留的 migration 路径；以及某个边界声明了未知的覆盖模式、某个 `path-only` 边界携带符号，或某个 `path-and-symbol` 边界不携带符号。缺失或拼错的清单和覆盖字段会在反序列化时失败。

## 应用预置边界更新（#237）

应用预置（pre-seed）是进入既有应用域的部署侧入口点，因此
`application-gateway-domain` 现在同时保留 `src/SignaCore.Host/Provisioning/**`、
`src/SignaCore.Host/BootstrapAppsOptions.cs`，以及 `BootstrapAppSeeder`、`BootstrapAppsOptions`
和 `BootstrapAppEntry` 符号。它不是一个新的可复用 migration 子系统。

这次狭窄的增量审计固定在
[SignaCore `51a44bbf`](https://github.com/philfanzhou/SignaCore/tree/51a44bbf1a25f4c747be510119363a216fdf9806)。
在该 commit 处，[Program.cs](https://github.com/philfanzhou/SignaCore/blob/51a44bbf1a25f4c747be510119363a216fdf9806/src/SignaCore.Host/Program.cs#L262)
在 bootstrap 阶段之后直接调用 `Provisioning/BootstrapAppSeeder.cs`。SignaCore
[#145](https://github.com/philfanzhou/SignaCore/issues/145) 移动了该 seeder；
[#157](https://github.com/philfanzhou/SignaCore/issues/157) 随后移除了
`DatabaseInitializer.cs` 中剩余的 refresh token 升级逻辑并记录了最低升级基线。
因此 `DatabaseInitializer.cs` 在这个更新的 commit 处已不复存在。它在 manifest 中的 legacy
路径仍属于原始删除盘点；没有任何 migration 候选路径与保留的 provisioning 路径重叠。
批次 #128 必须保留 Program.cs 中直接的应用阶段调用。

manifest 的 `source`、`schemaVersion`、原始 CI 证据、其他候选和其他被保留的边界均未改变。
provisioning 测试引用是来自固定较新 commit 的补充证据，不是声称原始 `23c2f666` 的 CI
运行执行过这些后来的测试。本次更新不授权在没有决策规则 6 要求的完整重新审计的情况下，把整个
历史删除计划应用到较新的 commit。

批次 #128 和下游的 #107 `bootstrap-app-provisioning` 场景必须验证
`BootstrapApps:FilePath` 仍然选择挂载的文件，且默认值仍为
`/app/data/bootstrap-apps.json`。一个有效的条目仍然创建产品应用，既有应用保持不变，
缺失/无法解析的文件保留其记录在案的行为，可选的 OIDC 部分继续通过
`OidcClientConfigurationApplier` 处理。删除之后，运行保留的
`BootstrapAppSeederTests` 和具名的 `AdminOidcClientTests`，连同完整的单元/集成命令。
它们当前的断言还保留了既有的 password hasher、审计/保存、取消和秘密输出边界；本次 manifest
变更不实现其中任何产品行为，也不声称未来的删除验收已经运行过。
