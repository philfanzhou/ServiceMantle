# Oracle 容器目标支持保持关闭

- 日期：2026-09-06；状态：结论固定，待 PR 合并。
- 决策 issue：[#203](https://github.com/philfanzhou/ServiceMantle/issues/203)。
- 代码基线：`fdac1d591488d57d639a05fc2b8a04faf536ae82`。
- 本决策补充 [ADR 0001](0001-oracle-provider-contract.md)。它不改变现有的
  单实例、普通 PDB 契约。

## 决策

ServiceMantle 继续只支持普通用户创建的 PDB 中的本地、非 Oracle 维护的应用用户。
CDB root、PDB seed、CDB common 用户、application root、application PDB 和
application-common 用户在 Bootstrap 目标验证、观察、准备和 migration 锁获取上
保持不受支持。

这是一个关闭的支持决策，不是承诺当前解析器在连接之前能识别每一种不受支持的
common 身份。没有特性开关、容器切换、common 授权、跨容器回退或未锁死的
migration 路径。

| 已连接的容器与目标身份 | 决策 | 当前证据与边界 |
| --- | --- | --- |
| 普通用户创建的 PDB；`COMMON=NO` 且 `ORACLE_MAINTAINED=N` 的本地用户 | 保留现有支持 | `CON_ID > 2`，两个 application-container 标志均为 `NO`，云标记为空，且数据库为非 RAC。准备还会验证 `ALL_USERS` 中的目标行。 |
| `CDB$ROOT`（`CON_ID=1`） | 关闭 | 运行时拓扑探测拒绝 `CON_ID <= 2`。 |
| `PDB$SEED`（`CON_ID=2`） | 关闭 | 同一探测拒绝它；ServiceMantle 不尝试修改 seed。 |
| 普通 PDB；CDB common 用户 | 关闭 | 默认的 `C##` 名称在连接之前被拒绝。自定义或空的 `COMMON_USER_PREFIX` 可以绕过该名称检查；当前目标会话探测不查询 `ALL_USERS`，由 [#317](https://github.com/philfanzhou/ServiceMantle/issues/317) 跟踪。 |
| Application root | 关闭 | `IS_APPLICATION_ROOT=YES` 在连接之后被拒绝。 |
| Application PDB，无论其目标是本地还是 application-common | 关闭 | `IS_APPLICATION_PDB=YES` 在连接之后被拒绝。 |
| Application root 中的 application-common 用户 | 关闭 | application-root 拓扑被拒绝；用户名前缀不被接受为身份证明。 |

Oracle 的 [`COMMON_USER_PREFIX`](https://docs.oracle.com/en/database/oracle/oracle-database/26/refrn/COMMON_USER_PREFIX.html)
是可配置的，并且在 CDB root 和 application root 中有不同的默认值。因此，“不以
`C##` 开头”不能证明一个用户是本地用户。反过来，连接别名或用户名也不能标识数据库
容器。

[`ALL_USERS`](https://docs.oracle.com/en/database/oracle/oracle-database/26/refrn/ALL_USERS.html)
提供当前会话可见的用户元数据，包括 `COMMON`、`ORACLE_MAINTAINED` 和
`INHERITED`。容器身份是另一类证据：
[`SYS_CONTEXT`](https://docs.oracle.com/en/database/oracle/oracle-database/26/sqlrf/SYS_CONTEXT.html)
提供当前的 `CON_ID`、`CON_NAME` 和会话用户。受支持的目标需要两类证据；命名约定
或 TNS 别名都不能替代它们。

## 各入口点的当前行为

下面的表格描述所述基线上的实现。它们刻意区分已声明的支持边界与完整检测。在
已认证会话存在之前的失败无法揭示隐藏的容器或用户类型。

### Bootstrap Validate

| 证据或失败 | 当前结果 | 副作用 |
| --- | --- | --- |
| 不受支持的用户名或认证形态，包括字面 `C##` 前缀 | `database.connection_string_invalid` | 无连接、DDL 或锁分配。 |
| 会话建立前账户被锁定/过期或凭据无效 | `database.authentication_failed` | 不做容器或 common 用户推断；无 DDL 或锁分配。 |
| 监听器、服务、传输或协议失败 | `database.connection_failed` | 不做容器或 common 用户推断；无 DDL 或锁分配。 |
| 目标缺少 `CREATE SESSION` | `database.permission_denied` | 无 DDL 或锁分配。 |
| 已连接会话报告 root、seed 或 application 容器 | `database.connection_string_invalid` | 仅探测；无 DDL 或锁分配。 |
| 必需的拓扑探测被拒绝 | `database.permission_denied` | 无 DDL 或锁分配。 |
| `SESSION_USER` 与规范化后的目标用户不同 | `database.connection_string_invalid` | 无 DDL 或锁分配。 |
| 意外的 provider/探测失败 | `database.provider_validation_failed` | 无 DDL 或锁分配。 |

验证不查询已连接目标用户的 `ALL_USERS` 行。因此，普通 PDB 中名称未被字面 `C##`
检查拒绝的 common 用户可能通过当前的拓扑探测。那是现有的 #317 检测缺口，不是受
支持行为，也不是本决策新增的保证。

### Observe

| 证据或失败 | 当前结果 | 副作用 |
| --- | --- | --- |
| 不受支持的用户名或认证形态 | `ServerUnreachable(InvalidTarget)` | 无连接、DDL 或锁分配。 |
| 已连接的 root、seed 或 application-container 会话 | `TargetUnreachable(InvalidTarget)`，`TargetExists=true` | 仅探测。 |
| 拓扑探测权限拒绝 | `TargetUnreachable(PermissionDenied)`，`TargetExists=true` | 仅探测。 |
| 已连接会话身份不匹配 | `TargetUnreachable(InvalidTarget)`，`TargetExists=true` | 仅探测。 |
| 会话存在前凭据无效 | `TargetUnreachable(AuthenticationFailed)`，存在性未知 | 不做隐藏拓扑或用户类型推断。 |
| 账户被锁定或过期 | `TargetUnreachable(AuthenticationFailed)`，`TargetExists=true` | 无 DDL 或锁分配。 |
| 监听器、服务、传输或协议失败 | `ServerUnreachable(ConnectionFailed)` | 无 DDL 或锁分配。 |
| 其他 Oracle 失败 | `ServerUnreachable(PreparationFailed)` | 无 DDL 或锁分配。 |

观察从不执行管理性发现或 DDL。对于普通 PDB 中成功连接的 common 目标，它存在
同样的 #317 缺口。

### Prepare

准备首先验证两个连接字符串以及它们精确修剪后的 `Data Source` 是否一致。然后它
打开一个非池化的管理会话，并在查询或修改目标之前应用运行时拓扑探测。

| 证据或失败 | 当前结果 | 副作用停止点 |
| --- | --- | --- |
| 不受支持的目标/管理形态、无效目标名称/密码，或数据源不相等 | `database_target_preparation.invalid_target` | 在管理连接之前、DDL 之前。 |
| 管理会话位于 root、seed 或 application 容器 | `database_target_preparation.invalid_target` | 在 `ALL_USERS` 之前、DDL 之前。 |
| 管理拓扑探测被拒绝 | `database_target_preparation.permission_denied` | 在 `ALL_USERS` 之前、DDL 之前。 |
| 管理 `SESSION_USER` 与其配置用户不同 | `database_target_preparation.invalid_target` | 在 `ALL_USERS` 之前、DDL 之前。 |
| 管理凭据被拒绝 | `database_target_preparation.authentication_failed` | 在 `ALL_USERS` 之前、DDL 之前。 |
| 管理连接或会话丢失 | `database_target_preparation.connection_failed` | 已确认的语句可能已经发生；ADR 0001 固定补偿资格。 |
| 目标行 `COMMON!=NO` 或 `ORACLE_MAINTAINED!=N` | `database_target_preparation.target_conflict` | 已读取 `ALL_USERS`；不发出创建、授权或 drop。 |
| 受支持普通 PDB 中缺少目标本地用户 | 现有创建路径 | `CREATE USER`，然后 `GRANT CREATE SESSION`，受 ADR 0001 补偿规则约束。 |
| 必需的创建/授权/drop 权限被拒绝 | `database_target_preparation.permission_denied` | 只有拒绝之前已到达的语句可能已经发生。 |
| 总截止时间到期 | `database_target_preparation.timeout` | 适用 ADR 0001 的取消与补偿优先级。 |
| 意外的 Oracle 失败，或符合条件的补偿无法验证移除 | `database_target_preparation.preparation_failed` | 之前已确认的语句不被视为已回滚。 |

SQL 中不包含 `SET CONTAINER`，也不包含 `CONTAINER=ALL`。Oracle 的
[`CREATE USER`](https://docs.oracle.com/en/database/oracle/oracle-database/26/sqlrf/CREATE-USER.html)
规则使 `CONTAINER=CURRENT` 成为 PDB 中本地用户的含义；当前实现通过省略该子句
依赖这一 scope。它同样不发出 common 的 `GRANT ... CONTAINER=ALL`。连接到普通
PDB 时管理账户本身可能是 common 的，因为当前拓扑探测不证明该账户是本地账户；
这并不授权跨容器操作。目标发现仍然在 DDL 之前拒绝可见的 common 或 Oracle 维护
的目标行。

### Acquire migration lock

| 证据或失败 | 当前结果 | 副作用停止点 |
| --- | --- | --- |
| 不受支持的版本、认证或名称形态，例如字面 `C##` | `migration.lock_not_supported` | 在连接和锁分配之前。 |
| 格式错误的 provider/配置或缺失数据源 | `migration.lock_failed` | 在锁分配之前。 |
| 已连接的 root、seed 或 application-container 会话 | `migration.lock_not_supported` | 在 `DBMS_LOCK.ALLOCATE_UNIQUE_AUTONOMOUS` 和 `REQUEST` 之前。 |
| 拓扑探测权限拒绝 | `migration.lock_not_supported` | 在锁分配之前。 |
| 已连接会话身份不匹配 | `migration.lock_failed` | 在锁分配之前。 |
| 认证、连接或意外的 Oracle 失败 | `migration.lock_failed`；获取截止时间仍为 `migration.lock_timeout` | 除非所有更早的检查都成功，否则不分配。 |
| 目标无法执行所需的 `SYS.DBMS_LOCK` 调用 | `migration.lock_not_supported` | 分配或请求可能已被尝试，但不返回有效租约。 |

拓扑探测成功后，当前锁名仅由规范化的 `ServiceId` 派生。provider 不并入数据库
ID、容器 ID、用户名或连接别名。这仅在现有单目标契约之内足够。连接别名和用户名
被明确排除在未来的跨容器锁身份之外。由于 Acquire 同样存在 #317 目标会话缺口，
普通 PDB 中名称未被拒绝的 common 用户今天可以到达锁分配；这既不受支持，本文档
也不使其变得安全。

调用方取消、有界超时、准备补偿、锁释放和租约丢失行为完全保持 ADR 0001 的规定。
取消不会把不支持的拓扑变成成功，ServiceMantle 也不承诺撤销消费方的 DDL。

## 为什么不启用 common 与跨容器操作

Oracle 只允许从适当的 root 创建 common 用户。`CONTAINER=ALL` 改变创建或授权的
scope，而 `CONTAINER=CURRENT` 把它们限制在当前容器。application-common 身份有
独立的 application-root 与同步生命周期。这些操作需要比当前 local-PDB provider
更大的权限与所有权契约。ServiceMantle 不会悄悄把 `CREATE USER`、`DROP USER` 或
`CREATE SESSION WITH ADMIN OPTION` 扩大为 root 级的 common 管理。

数据库/容器身份也是安全锁 scope 的一部分。支持多个容器将需要一个经服务器验证的
规范身份，例如数据库身份加容器身份（`CON_ID` 和一个稳定的容器标识符或等价物），
而不是未经验证的别名。该精确身份必须在同一目标的多个服务名之间保持一致，同时
防止不同 PDB 意外共用同一个锁命名空间。

## 重新开启支持的要求

重新开启任何关闭行需要独立的决策和实现任务。它必须提供以下全部自动化证据；
基础设施不可用时特性保持关闭，且不得产生跳过或虚假通过（绿）的必需作业。

1. 一个专用真实 CDB 环境，包含 root、seed、至少两个普通 PDB，以及一个
   application root/application PDB。测试必须断言服务器报告的数据库、容器、会话
   用户、`COMMON`、`ORACLE_MAINTAINED` 和 `INHERITED` 证据，而不是从名称或别名
   推断。
2. 在自定义、默认和空 `COMMON_USER_PREFIX` 值下，对 CDB common、
   application-common 和本地用户的显式支持规则。每个声称该身份的入口点都必须先
   解决 #317。
3. 针对 `CREATE USER` 和带显式 `CONTAINER=CURRENT` 或 `CONTAINER=ALL` 的
   `GRANT` 的最小权限矩阵，包括创建竞争、取消、丢失的 DDL 确认、补偿所有权，
   以及无关容器绝不被修改的证明。任何测试都不得使用宽泛的 DBA/SYSDBA 访问来
   掩盖所需的授权。
4. 一个规范的数据库加容器锁身份和双会话测试。针对同一规范容器和服务的两个参与者
   必须互斥竞争；不同 PDB 中的参与者不得意外共用别名；释放、超时、取消和被杀
   会话的租约丢失必须保持确定性。
5. 每种所声称拓扑的必需 CI 和发布门禁。缺失的凭据、权限、容器、已发现的测试或
   断言必须失败而不是跳过。

本关闭决策不创建任何实现任务。#317 仍是已知邻近缺陷，不在此修复；未发现其他
邻近债务。本文档不为跨容器行为添加任何 provider 代码、SQL、测试、包元数据、CI、
README 更改或保证。
