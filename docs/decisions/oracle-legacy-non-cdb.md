# Oracle legacy non-CDB 支持保持关闭

- 日期：2026-09-06；状态：结论固定，待 PR 合并。
- 决策 issue：[#205](https://github.com/philfanzhou/ServiceMantle/issues/205)。
- 代码基线：`fdac1d591488d57d639a05fc2b8a04faf536ae82`。
- 本决策补充 [ADR 0001](0001-oracle-provider-contract.md)。它不改变对自管理
  单实例 PDB 中本地应用用户的支持。

## 决策

ServiceMantle 不支持把 legacy non-CDB 作为 Bootstrap 目标、观察或准备的目标，
或获取 migration 锁的目标。本文档的 legacy 情形是 Oracle Database 19c non-CDB。
19c 之前的版本仍在 provider 最低版本契约之外，且 non-CDB 架构自 Database 21c
起被 Oracle 停止支持。

| 声明/实际部署 | ServiceMantle 决策 |
| --- | --- |
| 实际为 Oracle 19c non-CDB，声明为 19c 或更高 | 关闭；运行时拓扑必须拒绝它。更高的声明版本不会把数据库变成 PDB。 |
| 满足 ADR 0001 的实际 Oracle 19c 或更高普通 PDB | 保留现有支持。 |
| 声明的服务器版本低于 19c | 在连接之前按现有最低版本契约拒绝；本决策不重新开启它。 |
| 声明版本缺失或语法无效 | 配置无效，不是关于服务器真实版本或架构的证据。 |
| 声称是 21c 或更高的 non-CDB | 仍然关闭。Oracle 的停止支持不会让它对 ServiceMantle 更可接受，且如果连接仍然被提交，运行时探测保持权威。 |

Oracle 19c 的
[`non-CDB upgrade scenarios`](https://docs.oracle.com/en/database/oracle/oracle-database/19/upgrd/upgrade-scenarios-non-cdb-oracle-databases.html)
同时说明 non-CDB 自 12.1 起被弃用，且 multitenant 是 21c 及以后唯一受支持的
架构。19c 升级指南另行把 19c 称为
[`terminal non-CDB upgrade release`](https://docs.oracle.com/en/database/oracle/oracle-database/19/upgrd/overview-conveting-databases-during-upgrade.html)。
这比只说“19c 之后”无法新建 non-CDB 更精确：Oracle 19c 仍是正在讨论的 legacy
情形；从 21c 开始，创建或升级到 non-CDB 架构被停止支持。

支持关闭是 ServiceMantle 的契约与证据决策。它不声称 Oracle 19c non-CDB 缺少
用户、schema、`CREATE USER`、`CREATE SESSION` 或命名锁。Oracle 19c 文档记载了
普通的 [`CREATE USER`](https://docs.oracle.com/en/database/oracle/oracle-database/19/sqlrf/CREATE-USER.html)
和 [`DBMS_LOCK`](https://docs.oracle.com/en/database/oracle/oracle-database/19/arpls/DBMS_LOCK.html)
能力。ServiceMantle 尚未在该架构上验证其完整的准备、补偿、并发、取消和租约丢失
契约。

## 运行时证据与身份差异

当前 provider 打开的每个 Oracle 目标、管理和 migration 锁会话都运行同一个拓扑
查询。它读取 `SESSION_USER`、`CDB_NAME`、`CON_ID`、application-container 标志和
云服务标记。受支持的会话在尝试非 RAC 探测之前，必须有非空的 `CDB_NAME`、大于
`2` 的十进制 `CON_ID`、两个 application 标志均等于 `NO`，以及空的云标记。

对于 legacy 形态，决定性的区别不是用户名语法。在 non-CDB 中不存在当前 PDB 身份：
`CDB_NAME` 为空，容器元数据使用 non-CDB 身份而非用户 PDB。Oracle 19c 参考说明，
`CON_ID` 列在
[`non-CDB`](https://docs.oracle.com/en/database/oracle/oracle-database/19/refrn/cdb_-views.html)
中取值为 `0`。`CDB_NAME` 为空、非数字/空值，或 `CON_ID <= 2`，都会产生
`UnsupportedTopology`。为空或伪造的探测字段永远不会产生支持。

`SESSION_USER` 匹配仍然只证明配置的会话身份。它不能替代必需的数据库/容器证据。
同样，`ALL_USERS` 行可以确立某个数据库用户存在，但不能把会话变成 PDB 目标。
准备在其 `ALL_USERS` 查询之前拒绝管理会话，锁获取在任何 `DBMS_LOCK` 分配之前
拒绝目标会话。

声明的 `ServerVersion` 是用于最低版本门禁的配置。provider 不接受它作为经服务器
证明的拓扑，也不接受它作为远端数据库是 PDB 的证明。会话存在之前的传输或认证
失败既不揭示实际版本，也不揭示 CDB/non-CDB 架构。

## 各入口点的当前行为

以下结果描述一个语法有效的直接密码目标。调用方取消保持现有的
`OperationCanceledException` 行为，不能把不支持的拓扑变成成功。

| 证据或阶段 | Bootstrap Validate | Observe | Prepare | Acquire migration 锁 |
| --- | --- | --- | --- | --- |
| 声明版本低于 19c | `database.server_version_unsupported` | `ServerUnreachable(InvalidTarget)`，存在性未知 | `database_target_preparation.invalid_target` | `migration.lock_not_supported` |
| 声明版本缺失或无效 | `database.server_version_invalid` | `ServerUnreachable(InvalidTarget)`，存在性未知 | `database_target_preparation.invalid_target` | `migration.lock_failed` |
| 有效的 19c+ 声明；会话创建前认证失败 | `database.authentication_failed` | `TargetUnreachable(AuthenticationFailed)`；凭据无效时存在性未知，账户锁定/过期时 `TargetExists=true` | 管理登录：`database_target_preparation.authentication_failed` | `migration.lock_failed` |
| 有效的 19c+ 声明；监听器、服务、传输或协议失败 | `database.connection_failed` | `ServerUnreachable(ConnectionFailed)` | 管理登录：`database_target_preparation.connection_failed` | `migration.lock_failed`，或在获取截止时间先到时为 `migration.lock_timeout` |
| 已连接会话的 `SESSION_USER` 不符合预期 | `database.connection_string_invalid` | `TargetUnreachable(InvalidTarget)`，`TargetExists=true` | 管理会话：`database_target_preparation.invalid_target` | `migration.lock_failed` |
| 已连接的 19c non-CDB 返回空 `CDB_NAME` 或非 PDB 的 `CON_ID` | `database.connection_string_invalid` | `TargetUnreachable(InvalidTarget)`，`TargetExists=true` | `database_target_preparation.invalid_target` | `migration.lock_not_supported` |
| 必需的拓扑查询被拒绝 | `database.permission_denied` | `TargetUnreachable(PermissionDenied)`，`TargetExists=true` | `database_target_preparation.permission_denied` | `migration.lock_not_supported` |
| 意外的拓扑/探测失败 | `database.provider_validation_failed` | `ServerUnreachable(PreparationFailed)` | `database_target_preparation.preparation_failed` | `migration.lock_failed` |

Validate 和 Observe 从不执行 DDL 或锁分配。Prepare 首先验证目标和管理输入，打开
管理会话，并在 `ALL_USERS`、`CREATE USER`、`GRANT CREATE SESSION` 或任何补偿性
`DROP USER` 之前拒绝 non-CDB 拓扑。因此对于它实际识别出的 non-CDB，它不可能返回
`AlreadyExists` 或 `Created`。

Acquire 打开一个专用的非池化、不加入事务的目标用户会话，然后在读取会话 ID 之前、
在 `DBMS_LOCK.ALLOCATE_UNIQUE_AUTONOMOUS` 或 `DBMS_LOCK.REQUEST` 之前拒绝
non-CDB 拓扑。non-CDB 对这些 Oracle API 的可能支持被刻意不用作回退。不返回
租约，因此 migration 执行不会开始。

Observe 给出的 `TargetExists=true` 只意味着认证在拓扑拒绝之前建立了目标会话。
它不意味着该 non-CDB 受支持、已准备好或是 PDB。反过来，凭据无效不能证明数据库
用户缺失，监听器失败也不能证明数据库缺失或标识其架构。

## 所有权与非目标

ServiceMantle 不转换或升级 non-CDB、创建 CDB/PDB、移动 schema、调用 AutoUpgrade
或 `noncdb_to_pdb.sql`、更改 `COMPATIBLE`、执行 DBA 生命周期工作，或修改其最低
支持版本。消费方和 DBA 拥有任何数据库转换、备份、停机、回滚、对象兼容性和转换后
验证。转换为 PDB 的数据库是一个新的部署断言，必须自行满足普通的 ADR 0001 运行时
与权限契约。

现有的目标凭据、schema 对象、密码/锁状态、配额、权限、migration 和事务仍由
消费方拥有。ServiceMantle 既不改动一个被识别出的 non-CDB，也不承诺回滚在本库
之外产生的副作用。由
[#317](https://github.com/philfanzhou/ServiceMantle/issues/317) 跟踪的
local/non-Oracle-maintained 目标会话验证缺口保持独立；它不削弱 non-CDB 拒绝，
因为容器检查独立发生。

## 重新开启支持所需的证据

重新开启需要新的决策和实现任务。它必须提供一个专用、受支持的 Oracle 19c non-CDB
环境；Oracle Free 的 `FREEPDB1` 是 PDB，不能代替它。如果环境、凭据、权限或已
发现的测试缺失，必需的 CI 和发布验证必须失败而不是跳过。

真实的测试门禁至少必须：

1. 断言实际服务器版本和 non-CDB 证据，包括空的/non-CDB 的 `CDB_NAME` 和
   `CON_ID=0`，然后把它与单独测试的 19c+ 普通 PDB 区分开。调用方声明的版本或
   mock 的探测不够。
2. 使用具有已记录最小授权的直接密码目标与管理用户。验证目标身份/schema 所有权、
   缺失用户创建、精确已存在用户、冲突凭据、权限拒绝、调用方取消、超时、确定性
   并发创建、丢失的 DDL 确认和补偿所有权。清理只能移除被证明属于本次运行的身份。
3. 直接向专用目标授予 `SYS.DBMS_LOCK` 上的 `EXECUTE`，并运行至少两个真实会话。
   证明同服务互斥、不同服务互不干扰、有界超时、调用方取消、显式释放/重新获取，
   以及在每个 migration 阶段终止持有会话后的永久租约丢失。
4. 运行完整的 Bootstrap/Observe/Prepare/Acquire 错误矩阵，不把缺失的 non-CDB
   基础设施当作通过。机密必须不出现在仓库内容、诊断、缓存和构建产物中。

在全部这些证据成为必需门禁之前，支持保持关闭。本决策不创建任何实现任务。#317 是
唯一已知的邻近问题，不在此修复；未发现其他邻近缺陷。本文档不添加任何 provider
代码、SQL、测试、包元数据、CI、README 更改、数据库转换或 19c 之前的保证。
