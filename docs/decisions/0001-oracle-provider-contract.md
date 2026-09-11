# ADR 0001：Oracle 目标、所有权、锁与真实 CI 契约

- 状态：已接受
- 日期：2026-08-30
- 决策 issue：[#65](https://github.com/philfanzhou/ServiceMantle/issues/65)
- 实现 issue：[#66](https://github.com/philfanzhou/ServiceMantle/issues/66)、[#67](https://github.com/philfanzhou/ServiceMantle/issues/67)

## 背景

ServiceMantle 已经把 Oracle 建模为 `ServerSchema` 目标，但 Oracle 没有可单独创建的
schema 对象：每个数据库用户恰好拥有一个同名的 schema。Oracle 还提供多种部署、身份和
锁模型，它们的保证存在实质差异。把这些选择留给 provider 实现，会让目标存在性、权限、
锁丢失和 CI 强制变成有条件的行为，而不是契约。

本决策固定一种受支持的形态，并对其他所有形态失败关闭。它不新增产品代码，也不改变
目标准备或迁移锁 SPI。

## 决策

### 1. 受支持的 Oracle 形态

第一个 Oracle provider 支持以下全部、且仅支持以下内容：

- Oracle Database 19c 或更高版本，采用多租户架构。
- 自管理的单一数据库实例，通过一个 PDB service 访问。
- 本地 PDB 用户，在目标和管理 ODP.NET 连接字符串中都直接用 `User Id` 和 `Password`
  认证。
- 两个连接字符串都不启用 `DBA Privilege`、集成/外部认证、proxy 身份、wallet、token
  或操作系统认证。
- 未加引号的用户名，长度为 1-128 个 ASCII 字符，以 ASCII 字母开头，之后只包含
  ASCII 字母、数字、`_`、`$` 或 `#`。它会被规范化为大写，且不得以 `C##` 开头。
- 目标密码为 1-30 个可打印 ASCII 字符，且不包含双引号。ServiceMantle 会在
  `CREATE USER` 语句中用双引号将其括起来。超出这个刻意收窄、可安全表示的
  首个 provider 契约的密码，会在管理 DDL 之前被拒绝。
- 目标和管理连接字符串中的 `Data Source` 值必须完全相同且非空。相等性在 trim 之后
  按序数比较；ServiceMantle 不会推断两个 TNS 别名或连接描述符指向同一个 PDB。
- 每个打开的目标或管理会话都必须证明以下运行时事实：
  `SYS_CONTEXT('USERENV', 'CDB_NAME')` 非空；当前 `CON_ID` 大于 `2`；
  `IS_APPLICATION_ROOT` 和 `IS_APPLICATION_PDB` 都为 `NO`；
  `CLOUD_SERVICE` 为空；且 `DBMS_UTILITY.IS_CLUSTER_DATABASE` 为 `FALSE`。

目标身份是规范化后的目标 `User Id`。当且仅当当前 PDB 的 `ALL_USERS` 视图中存在该
`USERNAME`，且 `COMMON = 'NO'`、`ORACLE_MAINTAINED = 'N'` 时，目标存在。其所有者
就是该用户，因为用户和 schema 是同一个 Oracle 身份。空 schema 也算存在；对象数量、
默认表空间、配额、角色和迁移状态都不改变这个结论。

ServiceMantle 拥有观测以及被显式请求的创建尝试。消费方拥有目标凭据、密码轮换、
账户锁定状态、profile、表空间和配额、所有 schema 对象权限、所有 schema 对象以及
所有 migration。ServiceMantle 绝不修改现有用户的密码、解锁它、修改其授权、删除它
或重建它。

Wallet/mTLS、token、外部/OS 和 proxy 认证会在打开连接之前被拒绝，报
`database_target_preparation.invalid_target`；这些身份的迁移锁报
`migration.lock_not_supported`。已成功打开的会话若证明处于 RAC、Autonomous
Database、CDB root、common 或 application-common 身份、application container 或
旧式非 CDB，同样会被观测/准备以 `InvalidTarget` 拒绝，并被锁以
`migration.lock_not_supported` 拒绝。此类目标连接成功之后，观测使用
`TargetUnreachable(InvalidTarget), TargetExists=true`。在会话建立之前发生的凭据或
传输失败保留下文更窄的未知存在性映射；provider 不会声称是哪种隐藏拓扑拒绝了未认证
的连接。如果无法调用 `DBMS_UTILITY.IS_CLUSTER_DATABASE`，provider 就无法证明受
支持的拓扑：观测返回 `TargetUnreachable(PermissionDenied), TargetExists=true`，
准备返回 `database_target_preparation.permission_denied`，锁返回
`migration.lock_not_supported`。未覆盖形态的决策 issue 列在
[后续决策](#后续决策)下。

锁还会把声明的低于 19c 的版本、或超出受支持契约的密码/用户形态拒绝为
`migration.lock_not_supported`。格式错误的 provider/版本/连接配置或缺失的
data source 仍报 `migration.lock_failed`，实际的连接/认证失败、意外的会话身份和
未知 SQL 失败也一样。null 参数和无效的超时值保留参数异常。能力拒绝并不意味着已
识别出未认证目标的隐藏拓扑。

Oracle 在文档中说明每个用户拥有一个同名 schema，并在其
[多租户管理指南](https://docs.oracle.com/en/database/oracle/oracle-database/21/multi/introduction-to-the-multitenant-architecture.html)中区分本地
PDB 用户与 common 用户。

### 2. 观测与安全准备

`ObserveAsync` 只有目标连接字符串，所以当 Oracle 对缺失用户和错误密码刻意给出相同
认证响应时，它不得声称用户缺失。它应用以下矩阵：

| 证据 | 观测结果 |
| --- | --- |
| 连接成功且 `SESSION_USER` 等于规范化目标用户 | `TargetConnectable`、`TargetExists=true` |
| `ORA-01045`（指定用户缺少 `CREATE SESSION`） | `TargetUnreachable(PermissionDenied)`、`TargetExists=true` |
| `ORA-28000` 账户锁定或 `ORA-28001` 密码过期 | `TargetUnreachable(AuthenticationFailed)`、`TargetExists=true` |
| `ORA-01017` 无效凭据 | `TargetUnreachable(AuthenticationFailed)`、`TargetExists=null` |
| Listener、service、传输或协议失败 | `ServerUnreachable(ConnectionFailed)` |
| 不支持的身份或格式错误的连接数据 | `ServerUnreachable(InvalidTarget)` |
| 已连接会话证明处于不支持的运行时拓扑 | `TargetUnreachable(InvalidTarget)`、`TargetExists=true` |
| 已连接会话无法调用必需的拓扑探测 | `TargetUnreachable(PermissionDenied)`、`TargetExists=true` |
| 其他 Oracle 失败 | `ServerUnreachable(PreparationFailed)` |

因此，Oracle 观测不会从有歧义的凭据拒绝中返回 `TargetMissing`。`PrepareAsync`
可以确立缺失，因为它临时持有管理连接，并在该 PDB 中查询 `ALL_USERS`。

准备会禁用管理连接池化和环境事务加入。它先验证受支持的身份形态和相同的
`Data Source`，打开管理连接，然后在任何 DDL 之前探测 `ALL_USERS`：

1. 如果用户存在且目标凭据能以该用户连接，返回 `AlreadyExists`，不做任何修改。
2. 如果用户存在但目标凭据不能以该用户连接，返回
   `database_target_preparation.target_conflict`。绝不重置其密码或授权。
3. 如果用户不存在，发出安全加引号的 `CREATE USER ... IDENTIFIED BY ...`，随后
   执行 `GRANT CREATE SESSION` 并做一次新的目标连接探测。
4. 如果并发的 `CREATE USER` 抢先成功，重新查询并重新探测。只有当提供的目标凭据能
   以预期用户连接时才返回 `AlreadyExists`；否则返回 `TargetConflict`。在
   `ALL_USERS` 读到肯定结果之后发生的探测拒绝，报告为 `TargetConflict`，绝不报告
   为缺失：并发创建者可能仍处于其自身的 create/grant 窗口内，或者刚刚完成补偿。
   本次调用不会重建用户，调用方可以重试。
5. 补偿所有权终止于认领（adoption），而不是发起（authorship），且资格由本次调用已
   发出的语句决定，而不是由它可能无法观测到的结果决定。只有当本次调用证明了用户
   不存在、为其自身的 `CREATE USER` 收到了确定的成功确认，且**尚未**为其发出
   `GRANT CREATE SESSION` 语句时，才会尝试不带 `CASCADE` 的 `DROP USER`。以发出
   grant 为边界，是因为 Oracle 在确认 DDL 之前就已在服务端提交：一旦该语句上线，
   丢失的确认在本地与拒绝无法区分，而任何持有相同目标凭据的并发调用可能已经以该
   用户连接，并按规则 1 或规则 4 返回了 `AlreadyExists`。因此，不确定的 DDL 结果
   绝不是独占所有权的证据。如果 `CREATE USER` 的确认丢失，本次调用没有证明发起权，
   不做补偿。如果 grant 语句已经发出，无论本次调用观测到什么，都不做补偿。在该
   窄窗口之外的任何失败、整体超时或调用方取消，都会保留已创建的用户，不执行补偿。
   ServiceMantle 绝不能删除另一个执行者已经报告为已准备好的目标，而 Oracle 的 DDL
   可见性意味着“本次调用创建了它”并不能证明“本次调用仍然独占它”。
6. 补偿被允许时，它运行在自己有界的预算上，不与调用方的取消 token 绑定，因为触发
   补偿的最常见原因正是该 token 已被取消。未能可验证地删除用户的补偿尝试返回
   `PreparationFailed`，因为最终状态未知。补偿不被允许时，不会尝试补偿，也就不存在
   补偿失败，因此本次调用按下述优先级报告触发失败自身的错误码。

所需的最小直接管理权限为：

- 目标 PDB 中的 `CREATE USER`；
- 目标 PDB 中的 `DROP USER`，仅用于补偿本次调用确定完成创建、且尚未为其发出
  `GRANT CREATE SESSION` 的用户；
- `CREATE SESSION WITH ADMIN OPTION`，以便管理员能只向新用户授予
  `CREATE SESSION`。

不需要 `GRANT ANY PRIVILEGE`、`DBA`、`SYSDBA`、表空间管理或 schema 对象创建权限。
无法提供这三项直接权限的托管或受限环境，不受准备支持，会以
`database_target_preparation.permission_denied` 失败；绝不会被当作已准备好。
管理员和目标还都需要通常可用的调用 `DBMS_UTILITY.IS_CLUSTER_DATABASE` 的能力；
ServiceMantle 既不授予也不修复该包访问权限，若其被吊销，则以上述错误码失败关闭。
Oracle 的
[CREATE USER 参考](https://docs.oracle.com/en/database/oracle/oracle-database/21/sqlrf/CREATE-USER.html)
说明 `CREATE USER` 是必需的，且新用户的权限域为空；其
[GRANT 参考](https://docs.oracle.com/en/database/oracle/oracle-database/19/sqlrf/GRANT.html)
要求持有被授予权限的 `ADMIN OPTION`，或更宽的 `GRANT ANY PRIVILEGE`。

单次调用结束时，上述多个结果可能同时为真。准备按以下固定优先级从高到低解决它们，
使实现者无须自行选择：

1. 补偿被允许、已尝试、且未能可验证地删除用户：
   `database_target_preparation.preparation_failed`。它优先于调用方取消，因为
   `OperationCanceledException` 会掩盖一个泄漏的、无法连接的用户。
2. 否则为调用方取消：抛出 `OperationCanceledException`。无论被允许的补偿是否成功、
   也无论是否已越过 grant 边界，都如此。
3. 否则为调用方提供的整体超时：`database_target_preparation.timeout`。
4. 否则为下表中触发失败自身的错误码。发生在补偿不被允许期间的失败，总是落到这一级，
   绝不以补偿为由产生 `preparation_failed`；例如，grant 语句发出之后丢失的管理会话
   报 `connection_failed`。

准备失败映射固定如下：

| 失败 | 错误码 |
| --- | --- |
| provider 不匹配 | `database_target_preparation.provider_mismatch` |
| 不支持的身份/拓扑、超出精确受支持语法的标识符/密码、格式错误的连接，或不相等的 `Data Source` | `database_target_preparation.invalid_target` |
| 管理凭据被拒绝 | `database_target_preparation.authentication_failed` |
| 缺少必需的直接权限 | `database_target_preparation.permission_denied` |
| 现有用户无法用提供的目标身份连接，包括创建竞争中的落败方 | `database_target_preparation.target_conflict` |
| 管理连接或会话丢失，包括确认始终未到达的 DDL 语句 | `database_target_preparation.connection_failed` |
| 调用方提供的整体超时到期、没有被允许的补偿失败、且调用方未取消 | `database_target_preparation.timeout` |
| 意外的 Oracle 失败，或被允许的补偿未能可验证地删除本次调用的新用户 | `database_target_preparation.preparation_failed` |
| 调用方取消，且没有被允许的补偿失败 | 抛出 `OperationCanceledException` |

provider 不授予任何表空间配额或 schema 对象权限。创建的目标已可建立直接会话，但不
等于可运行任意消费方 migration。消费方 DBA 必须为其迁移执行者配置所需的配额和精确
DDL 权限。

### 3. 迁移锁

Oracle 迁移锁使用专用、非池化的目标用户会话上的 `SYS.DBMS_LOCK`。消费方 DBA 必须
直接向该用户授予 `EXECUTE ON SYS.DBMS_LOCK`；ServiceMantle 的目标准备不会授予它。

锁名是 `ServiceMantle.Migration.` 加上规范化后 `ServiceId` 的 SHA-256 摘要的
小写十六进制形式。provider 调用 `DBMS_LOCK.ALLOCATE_UNIQUE_AUTONOMOUS` 获得同名
句柄，然后以 `X_MODE`、剩余的有界获取超时和 `release_on_commit => FALSE` 调用
`DBMS_LOCK.REQUEST`。租约显式调用 `DBMS_LOCK.RELEASE`，然后关闭会话；会话终止也
会释放锁。

选择该策略是因为 Oracle 会把同名锁分配给所有会话，完整摘要避免把服务身份压缩到
30 位的调用方分配锁 ID 范围，且 autonomous 分配器不会提交消费方工作单元。它的目录
行和较低效率可以接受，因为一次迁移每个服务只获取一把锁，而不是每个会话数百把。
Oracle 在
[`DBMS_LOCK` 参考](https://docs.oracle.com/en/database/oracle/oracle-database/21/arpls/DBMS_LOCK.html)中记录了该分配器、保留名称、目录过期和
autonomous 事务。

被拒绝的替代方案：

- 调用方分配的数字 `DBMS_LOCK` ID 需要把 128 字符的 `ServiceId` 压缩到 30 位。
  碰撞会造成不相关服务之间的争用，而 provider 无法把它与合法争用区分开。
- 锁表或行锁会给消费方 schema 增加 ServiceMantle 拥有的持久化、DDL、清理和事务
  所有权。这与核心包和共享 `DbContext` 的边界相矛盾。
- `ALLOCATE_UNIQUE`（非 autonomous）会执行隐式提交。即使使用专用连接，autonomous
  形式也直接表达了不提交消费方事务这一不变量。

锁获取映射固定如下：

| 失败 | 结果 |
| --- | --- |
| 没有已注册的 Oracle 锁 provider，或目标用户无法执行 `SYS.DBMS_LOCK` | `migration.lock_not_supported` |
| 整体获取期限到期，包括 `REQUEST` 返回码 `1` | `migration.lock_timeout` |
| `REQUEST` 返回码 `0` | 获取到租约 |
| 返回码 `2`、`3`、`4` 或 `5`；分配器失败；连接/认证失败；意外的 Oracle 错误 | `migration.lock_failed` |
| 超时前的调用方取消 | 抛出 `OperationCanceledException` |

现有 SPI 可以报告 `AcquireAsync` 期间的失败，但当专用锁会话在 `ExecuteAsync`
仍在运行时终止，它无法通知 `DatabaseMigrationOrchestrator`。此时 Oracle 会自动
释放锁，所以静默继续会违反多实例保证。issue
[#200](https://github.com/philfanzhou/ServiceMantle/issues/200) 必须先添加
provider 中立的租约丢失信号，#67 才能实现这把锁。检测到的迁移中途丢失映射为
`migration.lock_failed`；消费方执行者已完成的工作不承诺回滚。

### 4. 真实 Oracle CI 是强制的

真实测试环境使用 Oracle 官方的 Oracle Database Free 镜像和 Oracle 的
[Free Use Terms and Conditions](https://www.oracle.com/downloads/licenses/oracle-free-license.html)，
它们允许内部开发和测试。CI 同时固定版本和 manifest 摘要：

```text
container-registry.oracle.com/database/free:23.26.1.0-lite-amd64@sha256:ef1a38683b3783b80e033be6b8f2cb31299dcba5430514ec96e2e8f4f0307d15
```

manifest 为 `linux/amd64`，与 `ubuntu-24.04` 匹配。该镜像提供 Oracle
[单实例容器文档](https://github.com/oracle/docker-images/blob/main/OracleDatabase/SingleInstance/README.md)所述的固定
`FREE` CDB 和 `FREEPDB1` PDB。本仓库不镜像或再分发该镜像。

每个测试已注册 Oracle 包的 pull request 和 push CI 运行必须：

1. 生成并遮蔽一个运行期本地的管理密码；任何仓库 secret 或手动许可点击都不是启动
   条件。
2. 拉取精确的镜像引用。拉取失败则 job 失败。
3. 使用 `ORACLE_PWD` 和随机宿主端口启动容器。退出、`unhealthy` 或有界的启动期限
   都会使 job 失败。
4. 同时要求容器健康和针对 `FREEPDB1` 的真实 ODP.NET `SELECT 1 FROM DUAL`；仅有
   Docker 健康不够。
5. 设置 `RUN_SERVICEMANTLE_ORACLE_TESTS=true`，并通过 #115 的真实数据库测试框架
   提供遮蔽后的目标/管理连接字符串。缺少变量、连接失败、发现的真实测试为零、任何
   skip 或任何测试失败，都会使 job 失败。
6. 让已注册的包通过 ReleaseTool 的 build、test、pack 和 verify。Oracle 环境不可用
   会使 `test` 失败；它绝不会把 Oracle 测试从发布门禁中移除。

本地运行可以省略该 opt-in 变量并跳过 Oracle 容器测试。CI 和发布验证不可以。该镜像
未来的可用性没有保证：镜像被删除、许可变更或 registry 故障都会刻意使必需的 job
失败，直到新的 ADR 接受替代方案或显式关闭 Oracle 支持。

### 5. 必需的实现测试矩阵

issue #66 必须覆盖：

- 有效的描述符元数据（`Oracle`、`ServerSchema`、19c+）、provider 注册、大小写
  规范化、不支持的标识符/认证拒绝，以及针对 RAC、云、root、application container
  和非 CDB 会话的失败关闭运行时探测；
- 可连接目标、`CREATE SESSION` 拒绝、锁定/过期账户、有歧义的 `ORA-01017`、错误
  service、取消，以及安全诊断；
- 缺失用户创建、现有用户保护、错误所有者/凭据冲突、并发同凭据创建、
  create/grant/drop 处的权限拒绝、grant 语句发出前尝试的补偿、grant 语句发出后
  被拒绝的补偿、`CREATE USER` 确认不确定时被拒绝的补偿、补偿成功/失败、失败的补偿
  优先于调用方取消、整体超时、取消优先级，以及管理连接池隔离；
- 显式的认领竞争测试：一个执行者创建并授权，第二个执行者认领该用户并返回
  `AlreadyExists`，随后第一个执行者在其新的目标探测期间被取消或超时。用户必须
  存活，且第二个执行者的结果必须保持正确；
- create/grant 窗口测试：证明在 grant 之前观测到用户的并发调用返回
  `TargetConflict`，且既不删除也不重建它；
- 丢失确认测试：`GRANT CREATE SESSION` 在服务端提交，但其确认始终未到达发出方。
  断言不会发出 `DROP USER`、失败报告为 `connection_failed`、且并发认领者的
  `AlreadyExists` 目标存活；
- 针对相同的成功、失败、取消、安全和确定性双执行者竞争路径（包括上述认领竞争）的
  真实 `FREEPDB1` 测试。#66 被 #115 和 #189 阻塞，以便使用共享的 hard-fail 框架和
  标准的准备注册接缝。

issue #67 必须覆盖：

- 锁名固定向量、同服务互斥、不同服务独立、请求超时、调用方取消、返回码映射、
  缺少直接执行权限、不支持的运行时拓扑、显式释放，以及连接清理；
- 真实的双编排器执行：证明只有一个执行者运行、持锁复查、释放/重新获取、获取前的
  会话终止，以及每个编排阶段期间的确定性会话终止；
- 来自 #200 的租约丢失行为，以及来自 #115/#114 的 hard-fail Oracle
  CI/ReleaseTool 集成。#67 不得实现静默的无锁或会话丢失回退。

### 6. CoddLoom 保持在依赖图之外

Oracle 实现直接使用 `Oracle.ManagedDataAccess.Core`。在
[commit `f23f611`](https://github.com/philfanzhou/CoddLoom/tree/f23f611cda9afaa0eef12cf644af75c3e916de9f)
对 CoddLoom 的检查没有发现 Oracle 连接字符串构建器或可复用的标识符加引号 API：
`OracleExecutor` 只包装一个提供的完整连接字符串，`OracleBuilder` 只是为 ORM SQL
拼接已提供的表标识符。ServiceMantle provider 仍然必须自己使用
`OracleConnectionStringBuilder`、实现两个小的私有加引号/验证辅助方法、对 Oracle
错误分类、管理特权连接池化，并自行实现 `DBMS_LOCK`。

因此，引用 CoddLoom 会增加一个 ORM 包和同样的 ODP.NET 依赖，却不能减少 provider
代码。这一实测的 Oracle 结果印证而非推翻
[#179](https://github.com/philfanzhou/ServiceMantle/issues/179)：数据库能力留在
ServiceMantle。

## 后果

- `ServerSchema` 只有一个无歧义的 Oracle 含义：本地 PDB 用户及其同名 schema，而不是
  数据库、表空间、common 用户或任意当前 schema。
- 准备是最小权限的，对既有用户是非破坏性的，但它并不使任意 migration 成为可能。
  消费方保留 schema DDL 和配额策略。
- 自动删除仅限于本次调用确定完成创建、且尚未尝试使其可连接的用户，因此一个执行者
  返回的 `AlreadyExists` 结果，绝不会被另一个执行者之后的失败、超时或取消所撤销。
- 缺少用户管理权限或 `DBMS_LOCK` 的受限环境会以稳定的既有错误码失败；不存在有条件
  启用。
- `ALLOCATE_UNIQUE_AUTONOMOUS` 写入的是 Oracle 自己的锁名目录行。它不写入任何
  ServiceMantle 表，也不提交任何消费方事务。
- 在 #200 弥合 provider 中立的租约丢失缺口之前，#67 不能开始。
- 固定的专有但免费的镜像提供了可复现性和清晰的许可来源，同时刻意使 registry 或
  许可的丧失成为可见的构建失败。

## 明确非保证

- 不承诺支持下文列出的后续部署和认证形态。
- `ObserveAsync` 在 `ORA-01017` 之后不区分缺失用户与错误密码；它报告未知存在性。
- 准备不授予配额、对象 DDL 权限、角色或 `DBMS_LOCK` 访问，也不修复既有账户。
- 失败的补偿可能留下一个新建但无法连接的用户；结果是 `PreparationFailed`，它优先于
  调用方取消，且需要消费方 DBA 介入修复。
- 准备不是事务性的。在发出 `GRANT CREATE SESSION` 之后失败、超时或被取消的调用，会
  刻意保留已创建的用户；状态在后续调用中收敛，而不是回滚。
- 补偿资格刻意保守而非完备。不确定的 `CREATE USER` 或 `GRANT CREATE SESSION` 结果
  会完全抑制补偿，因此失败的准备可能留下一个已创建但未授权、无法连接的用户，交由
  后续调用或消费方 DBA 收敛。
- 不承诺同时首次准备的多个调用能一次成功。观测到另一个调用的 create/grant 窗口的
  调用返回 `TargetConflict` 而不是 `AlreadyExists`，本 ADR 只承诺此类调用不具有
  破坏性，且重试会收敛。
- Oracle 会话丢失无法撤销在检测到丢失之前已经完成的迁移工作。
- CI 证明的是固定的 Oracle Database Free 构建在 `linux/amd64` 上的行为；它不证明
  每个 19c+ 发布更新、操作系统、云服务或拓扑。

## 后续决策

- [#201](https://github.com/philfanzhou/ServiceMantle/issues/201)：Oracle RAC 与连接故障转移；
  [ADR 0005](0005-oracle-rac-and-failover.md) 保持这些不受支持，并定义了重新开启的证据。
- [#202](https://github.com/philfanzhou/ServiceMantle/issues/202)：Autonomous Database；
  [ADR 0006](0006-oracle-autonomous-database.md) 关闭了 Serverless 和 Dedicated 的目标/锁支持。
- [#203](https://github.com/philfanzhou/ServiceMantle/issues/203)：CDB root 与 common 用户。
- [#204](https://github.com/philfanzhou/ServiceMantle/issues/204)：wallet、token、外部与 proxy 认证。
- [#205](https://github.com/philfanzhou/ServiceMantle/issues/205)：旧式非 CDB 部署。

## 参考资料

- [Oracle 多租户用户与 schema 模型](https://docs.oracle.com/en/database/oracle/oracle-database/21/multi/introduction-to-the-multitenant-architecture.html)
- [Oracle `CREATE USER`](https://docs.oracle.com/en/database/oracle/oracle-database/21/sqlrf/CREATE-USER.html)
- [Oracle `GRANT`](https://docs.oracle.com/en/database/oracle/oracle-database/19/sqlrf/GRANT.html)
- [Oracle 密码要求](https://docs.oracle.com/en/database/oracle/oracle-database/19/dbseg/minimum-requirements-passwords.html)
- [Oracle `ALL_USERS`](https://docs.oracle.com/en/database/oracle/oracle-database/21/refrn/ALL_USERS.html)
- [Oracle `DBMS_LOCK`](https://docs.oracle.com/en/database/oracle/oracle-database/21/arpls/DBMS_LOCK.html)
- [Oracle `DBMS_UTILITY.IS_CLUSTER_DATABASE`](https://docs.oracle.com/en/database/oracle/oracle-database/21/arpls/DBMS_UTILITY.html#GUID-EAFE94FC-5BEA-42C4-B70A-8C18DBE9EC20)
- [Oracle Database Free 容器文档](https://github.com/oracle/docker-images/blob/main/OracleDatabase/SingleInstance/README.md)
- [Oracle Free Use Terms and Conditions](https://www.oracle.com/downloads/licenses/oracle-free-license.html)
- [`Oracle.ManagedDataAccess.Core` 23.26.300](https://www.nuget.org/packages/Oracle.ManagedDataAccess.Core/23.26.300)
