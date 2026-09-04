# ADR 0006：关闭 Oracle Autonomous Database 的目标与迁移锁支持

- 日期：2026-09-03；状态：结论固定，待 PR 合并。
- 决策任务：[#202](https://github.com/philfanzhou/ServiceMantle/issues/202)。
- 代码基线：`86cd468`；驱动：`Oracle.ManagedDataAccess.Core` 23.26.300。
- 补充 [ADR 0001](0001-oracle-provider-contract.md)，不改变自管理单实例 PDB 契约。

## 结论

| ServiceMantle 能力 | Autonomous Serverless | Autonomous Dedicated |
| --- | --- | --- |
| Bootstrap 验证并接纳为可用目标 | 关闭 | 关闭 |
| 目标观察成功（TargetConnectable） | 关闭 | 关闭 |
| 目标准备（创建用户或确认 AlreadyExists） | 关闭 | 关闭 |
| migration lock 获取与持有 | 关闭 | 关闭 |
| 本库管理钱包、云凭据或以无钱包连接启用上述能力 | 关闭 | 关闭 |

以上是无条件的不支持结论，不是“ADMIN 能连接便支持”或“有 DBMS_LOCK 就支持”。手工创建用户、
增加权限、选择 TLS/TCPS、去掉 Wallet Location 属性，均不改变本库支持面。没有隐藏开关、无锁
回退、自动升级权限或自动创建云资源。本任务选择 #202 的关闭分支，不实现 provider。

## Oracle 平台事实与本库证据

以下资料于决策日期查阅；它们说明平台机制，不等同于当前驱动与本库的真实云服务验收。

| 关注点 | Serverless | Dedicated | 对本库的含义 |
| --- | --- | --- | --- |
| ADMIN 权限 | ADMIN 不是 SYSDBA，系统/对象授权受限，不能把 GRANT ANY OBJECT PRIVILEGE 当作任意 SYS 对象授权 | ADMIN 与 SYS 的权限集合不同，ANY 权限也受 common-schema lockdown 限制 | 不能依据账户名或角色推断本库所需直接权限 |
| 用户与 schema | ADMIN 可以创建数据库用户并授予会话权限；密码与配额仍受平台规则约束 | 同样提供 ADMIN 创建用户的入口，用户/配额管理与云实例管理分离 | “能创建用户”不证明本库创建、授权、竞争与补偿协议完整可用 |
| 网络与凭据 | mTLS 需要客户端钱包；部分客户端在 TLS 模式下可无钱包连接 | AVMC 的 listener/TLS 配置、服务名和证书信任需要单独固定；符合前置条件时允许无钱包 TLS | 不能把“没有钱包属性”当作自管理数据库证明，也不能把 TCPS 当作唯一 Autonomous 标识 |

权限证据：[Serverless ADMIN](https://docs.oracle.com/en/cloud/paas/autonomous-database/serverless/adbsb/autonomous-admin-user-roles.html)、
[Dedicated ADMIN/SYS 差异](https://docs.oracle.com/en/cloud/paas/autonomous-database/dedicated/adbaa/oracle-database-features-in-autonomous-ai-database-on.html)。
用户证据：[Serverless 用户管理](https://docs.oracle.com/en/cloud/paas/autonomous-database/serverless/adbsb/manage-users.html)、
[Dedicated 用户管理](https://docs.oracle.com/en/cloud/paas/autonomous-database/dedicated/adbaa/manage-autonomous-ai-database-users-on-dedicated-exadata.html)。
连接证据：[Serverless 客户端](https://docs.oracle.com/en/cloud/paas/autonomous-database/serverless/adbsb/about-connecting-client.html)、
[Dedicated 无钱包 TLS 前置条件](https://docs.oracle.com/en/cloud/paas/autonomous-database/dedicated/adbaa/prepare-for-tls-walletless-connections.html)、
[Dedicated 连接服务](https://docs.oracle.com/en/cloud/paas/autonomous-database/dedicated/adbaa/connect-to-autonomous-ai-database-on-dedicated-exadata.html)。

本库的准备协议需要实际证明 `CREATE USER`、`DROP USER`、可继续授予的 `CREATE SESSION`，以及
受支持的本地用户视图/拓扑探针。它只授予会话权限；不会授予 quota、DWROLE、DBA 或消费方
migration 所需对象权限。云服务能执行某条 CREATE USER 不能证明所有权限组合、密码策略、
并发创建、DDL 确认丢失和补偿窗口与 ADR 0001 一致。已有用户的密码、状态、权限和对象仍由消费方拥有。

这里不宣称 Autonomous 全面禁止 DBMS_LOCK。Oracle 的
[Serverless PL/SQL 限制说明](https://docs.oracle.com/en/cloud/paas/autonomous-database/serverless/adbsb/autonomous-plsql-packages.html)
没有给出本库完整命名锁协议的验收结果；Oracle 的
[ATP-D Fusion Middleware 迁移指南](https://docs.oracle.com/en/middleware/fusion-middleware/14.1.2/fmwmi/migrating-oracle-fusion-middleware-premises-database-autonomous-database.pdf)
还包含对特定 schema 授予 DBMS_LOCK 的步骤。不能从另一个产品的安装步骤推断任意应用用户可
获得本库所需权限，也不能反向推断该包在所有 Autonomous 部署均不可用。

本库实际需要 `SYS.DBMS_LOCK.ALLOCATE_UNIQUE_AUTONOMOUS`、`REQUEST(X_MODE,
release_on_commit => FALSE)`、`RELEASE`，并且需要不同会话的竞争、取消、释放和失锁证据。
[通用 DBMS_LOCK 文档](https://docs.oracle.com/en/database/oracle/oracle-database/26/arpls/DBMS_LOCK.html)
描述这些接口；它不是 Autonomous Serverless/Dedicated 的本库测试报告。Dedicated 的实例路由或
透明会话恢复还受 [ADR 0005](0005-oracle-rac-and-failover.md) 的独立关闭约束。

## 当前代码中的拒绝路径与错误码

已阅读目标身份解析、Bootstrap、目标观察/准备、行政会话、共享拓扑探针、锁的打开/分配/请求/
监控调用路径、相应结果分类测试、真实 Oracle Free 测试环境和 CI。现有代码在每个打开的目标/
行政/锁会话检查预期 SESSION_USER、本地普通 PDB 与非集群条件，并要求 `CLOUD_SERVICE` 为空。
Oracle 的 [SYS_CONTEXT 文档](https://docs.oracle.com/en/database/oracle/oracle-database/26/sqlrf/SYS_CONTEXT.html)
说明该属性在相应 Autonomous 服务上可返回 `DWCS`、`OLTP`、`JDCS`。本库拒绝读取并 trim 后的任意非空值，
不是只列举这些名字；不会按域名、TNS 别名或服务名后缀猜测云产品。

| 已获得的证据/入口 | 当前分类及停止位置 |
| --- | --- |
| Bootstrap：身份匹配的已连接会话证明不支持的云拓扑 | `database.connection_string_invalid`，验证失败 |
| Observe：身份匹配的已连接会话证明不支持的云拓扑 | `TargetUnreachable(database_target_preparation.invalid_target)`，`TargetExists=true`；没有成功接纳目标 |
| Prepare：行政会话证明不支持的云拓扑 | `database_target_preparation.invalid_target`；在 ALL_USERS 查询及 CREATE/GRANT/DROP 之前停止 |
| Acquire：会话证明不支持的云拓扑 | `migration.lock_not_supported`；在锁分配/REQUEST 之前停止 |
| 必需拓扑探针被拒绝 | Bootstrap 为 `database.permission_denied`；Observe 为 `TargetUnreachable(database_target_preparation.permission_denied), TargetExists=true`；Prepare 为 `database_target_preparation.permission_denied`；Acquire 为 `migration.lock_not_supported` |
| 尚未取得可验证会话：凭据、网络、服务或未知 SQL 失败 | 保持既有 authentication/connection/validation 分类；Acquire 为 `migration.lock_failed`，获取 deadline 到期为 `migration.lock_timeout`；不推断目标不存在或隐藏拓扑 |

现有 Bootstrap 的未知探针失败是 `database.provider_validation_failed`；Observe/Prepare 的未知
失败是 `database_target_preparation.preparation_failed`。身份不匹配不算云产品证据：Observe 为
InvalidTarget，锁为 LockFailed。只有支持拓扑下的现存用户分支才可能运行目标复核；复核失败
保留既有 TargetConflict，不因本决策改写该分支。

已解析出的不支持身份/钱包属性按目标契约拒绝：Bootstrap 为 `database.connection_string_invalid`，
Observe/Prepare 为 `database_target_preparation.invalid_target`，锁为 `migration.lock_not_supported`。
当前 main 的目标共用 parser 对部分被 ODP.NET 拒绝的属性仍可能抛出 OracleException；这是已有
[#274](https://github.com/philfanzhou/ServiceMantle/issues/274)，由独立
[PR #278](https://github.com/philfanzhou/ServiceMantle/pull/278) 修复，尚未合并。本决策不把预期错误码
冒充为所有畸形输入已经实现的保证。锁路径已经具有独立的属性预检与异常分类；[#268](https://github.com/philfanzhou/ServiceMantle/issues/268)
已进入本基线，不再使用 ADR 0005 历史基线所记录的 LockFailed 漂移作为当前行为。

调用方取消仍遵循对应公开 API；取消不能使不支持拓扑成功。未取得有效租约就不开始 migration。
“关闭支持”不承诺识别所有外部 TNS、sqlnet.ora、钱包搜索路径、未来云元数据或服务端恢复设置。
自管理单实例 PDB 是部署前提，探针通过不构成 Autonomous 支持声明；更不承诺消费方已提交 DDL 的回滚。

## 真实服务与凭据的证据边界

本次没有收到已授权的真实 Autonomous Serverless/Dedicated 测试端点、账户或钱包，也没有运行
云服务能力实测。现有本地 Oracle 开关/管理员连接未配置。Oracle Free 的现有 CI 只验证单实例
FREEPDB1；本地 Autonomous Free 容器也不构成上述两种 OCI 服务的替代证据。本次不新增一个
默认 skip 的 Autonomous job，不复用 Oracle Free 变量伪装成云测试，也不创建云资源。

关闭结论无需等待不存在的支持证据。若未来提出重新开放，必须先以独立决策/实施任务交付以下
自动化证据；下面是准入要求，不是已执行结果：

1. 对 Serverless 与 Dedicated 分别固定真实 OCI 服务、数据库版本/工作负载、service、网络/
   TLS 模式、ODP.NET 版本与实例路由。缺少任何被宣称支持的环境、凭据、权限或测试发现结果，
   必需 CI 非零退出；不允许 skip、零测试、Free 替代或因环境变量缺失而变绿。
2. 使用专用测试租户/数据库与最小权限 ADMIN/应用用户，机器验证直接权限和授权能力、ALL_USERS
   身份/拓扑值、用户创建/会话授权/已有用户不变、竞争、取消及 DDL 确认丢失的补偿边界。不得
   为通过测试自动授予 DBA/SYSDBA；只清理本运行可证明拥有的用户和对象。
3. 在两个独立真实会话上执行完整命名锁协议，验证同 service_id 互斥、不同 service_id 不冲突、
   超时/取消、释放后重新获取，以及检查/执行/最终检查各阶段的会话损失。若 Dedicated 涉及
   RAC/回放，必须同时满足 ADR 0005 的独立证据门槛，不能把重连当作原 lease。
4. 钱包下载材料、钱包密码、ADMIN/目标连接及 OCI 凭据只经受保护的 CI secret 或短期身份注入；
   钱包落在任务独占临时目录，限制文件访问并在 finally 清理，不进入仓库、缓存、artifact、
   测试名或原始错误输出。不对不可信 fork 暴露凭据。保留证书/主机名校验；先分别证明 TLS 与
   mTLS 的驱动行为，不因连接成功省略数据库用户名/密码保护。

## 实现拆分与验收

本次关闭结论沿用已交付的拓扑拒绝能力，新增实现任务为零，因而不制造无工作内容的 task 或
原生依赖。#274 是已独立跟踪的 parser 邻近债务，既不依赖也不阻塞 #202 的支持决策；不在这里修复。
无其他新增邻近债务。通用认证支持仍由 [#204](https://github.com/philfanzhou/ServiceMantle/issues/204)
决策，本任务不接管它。

未来重开时，环境/认证、目标准备、迁移锁必须拆为可独立验收的 task，使用 GitHub 原生 Blocked by
连接本决策的替代决策及各自真实前置；若涉及 #204 或 RAC，先落实相应决策。未形成代码/测试/
邻近债务盘点前不标记 ready。现在没有“待环境可用自动启用”的实施任务。

本决策验收是两类 Autonomous 的逐能力关闭矩阵、精确错误与证据范围、重新开放的真实 CI 门槛，
以及 README/ADR 0001 的一致链接。公开 API、provider、SQL、包/依赖与发布流水线均不改变。
