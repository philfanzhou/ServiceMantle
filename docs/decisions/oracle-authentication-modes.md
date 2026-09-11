# Oracle 非密码认证支持保持关闭

- 日期：2026-09-06；状态：结论固定，待 PR 合并。
- 决策 issue：[#204](https://github.com/philfanzhou/ServiceMantle/issues/204)。
- 代码基线：`fdac1d591488d57d639a05fc2b8a04faf536ae82`；
  `Oracle.ManagedDataAccess.Core` 23.26.300。
- 本决策补充 [ADR 0001](0001-oracle-provider-contract.md)，不把
  [ADR 0006](0006-oracle-autonomous-database.md) 中的 Autonomous Database 决策
  扩展到所有自管理部署。

## 决策

ServiceMantle 继续只支持直接认证的 Oracle 数据库用户，其目标连接字符串必须包含
普通的 `User Id`、密码和非空的 `Data Source`。准备阶段使用的管理连接有同样的
直接用户/密码要求。受支持的运行时身份是该数据库用户在同一受支持普通 PDB 中的
同名 schema。

以下认证族在目标验证、观察、准备和 migration 锁获取上保持关闭：

| 认证族 | 决策 | ServiceMantle 之外的身份与凭据所有权 |
| --- | --- | --- |
| 基于钱包的客户端认证与 mTLS | 关闭 | 消费方拥有钱包获取、文件、密码、证书信任、续期、吊销、文件权限和清理。仅用作 TLS 信任库的钱包本身不能标识 schema 所有者。 |
| OCI IAM、Microsoft Entra ID/OAuth 或其他 ODP.NET token 认证 | 关闭 | 消费方拥有工作负载/用户身份、token 获取、私钥、回调、刷新、过期、audience、文件和池生命周期。ServiceMantle 不接受任何 token 或刷新回调。 |
| 外部或操作系统认证，包括 NTS、Kerberos 和证书映射外部用户 | 关闭 | 消费方拥有客户端/进程身份、Oracle Net 配置、外部名映射、平台限制和凭据续期。`User Id=/` 不是受支持的目标身份。 |
| 代理认证，包括双会话属性和 `proxy[client]` 语法 | 关闭 | 消费方拥有代理与客户端凭据、`CONNECT THROUGH` 授权、角色限制、审计身份、代理会话状态和池隔离。ServiceMantle 不选择由哪个身份拥有 migration。 |

没有隐藏的启用开关、无密码回退、自动凭据发现、代理会话切换或更弱的锁模式。
ODP.NET 能打开其中某种连接，并不构成 ServiceMantle 的支持。

## ServiceMantle 无法证明的显式输入与配置

当前的目标解析器显式拒绝序列化了 `DBA Privilege`、`Proxy User Id`、
`Proxy Password`、`Wallet Location` 或 `Token Authentication` 中任何一项的连接
builder。它也拒绝缺失/空密码、`User Id=/`，以及不在狭窄的未加引号标识符文法
之内的用户名。ODP.NET 单会话 `proxy[client]` 语法中的方括号字符不在该文法之内。

migration 锁入口点首先使用一个通用连接字符串解析器来识别这五个命名属性，即使
固定版本的 ODP.NET builder 会拒绝某个关键字。这是一个刻意的
`migration.lock_not_supported` 预检。它不会把每个未知、格式错误或依赖版本的
关键字都变成受支持认证的诊断。

ODP.NET 还支持这些显式输入之外的配置。它的
[`OracleConfiguration` secure connection properties](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/ConfigurationSecureConnectionProperties.html)
包含进程级的钱包、token 和 Oracle Net 设置。`tnsnames.ora`、`sqlnet.ora`、
`TNS_ADMIN`、系统证书库、数据库侧身份映射，或修改 ODP.NET 全局状态的代码，都
可以在没有对应序列化属性的情况下影响连接。Oracle 文档说明，托管 ODP.NET 的外部
认证方法可以通过
[`SQLNET.AUTHENTICATION_SERVICES`](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/InstallManagedConfig.html)
选择。

ServiceMantle 不解析、快照、隔离、重置或证明这些进程级、文件级、平台或服务端
设置。因此：

- `Wallet Location`、`Token Authentication`、代理属性或 `User Id=/` 的缺失，
  不能证明这些认证路径都没有影响 ODP.NET；
- 使用平台证书库的 TCPS 连接可能只是普通的单向 TLS；传输成功不是对钱包/mTLS
  身份支持的声明；
- 本决策不削弱 ODP.NET 的主机名、可分辨名称、证书链或 TLS 版本验证，
  ServiceMantle 也不添加任何替代信任路径；
- 认证完成之前的连接失败无法揭示隐藏的认证方法、数据库身份、容器或 schema
  所有者。

当前的运行时拓扑查询只用规范化后的配置用户外加已有的 PDB/cloud/RAC 事实来验证
`SESSION_USER`。它不读取 `AUTHENTICATION_METHOD`、`AUTHENTICATED_IDENTITY`、
`PROXY_USER` 或 `CURRENT_SCHEMA`。Oracle 的
[`SYS_CONTEXT`](https://docs.oracle.com/en/database/oracle/oracle-database/26/sqlrf/SYS_CONTEXT.html)
把它们定义为彼此独立的证据：例如 `PROXY_USER` 是代表 `SESSION_USER` 打开会话的
数据库用户，而 `CURRENT_SCHEMA` 在会话期间可以改变。因此仅凭 `SESSION_USER`
匹配不能证明直接密码认证、无代理、凭据来源或 schema 上下文未变。

这种不完整的运行时认证检测是已声明的非保证，不是支持面的扩大。独立的
local/non-Oracle-maintained 目标身份缺口仍由
[#317](https://github.com/philfanzhou/ServiceMantle/issues/317) 跟踪。

## 当前错误与副作用边界

全部四个公开入口点在无效输入分类之前，先向已取消的调用方抛出携带其 token 的
`OperationCanceledException`。I/O 期间观察到的取消同样被保留，唯一例外是 ADR
0001 已有的 Prepare 规则：一次被允许的补偿尝试无法验证移除时，以
`database_target_preparation.preparation_failed` 优先于调用方取消。下表列出当前
基线上的非取消结果。

| 证据 | Bootstrap Validate | Observe | Prepare | Acquire migration 锁 |
| --- | --- | --- | --- | --- |
| 显式 `Wallet Location`、`Token Authentication`、代理属性或 `DBA Privilege` | `database.connection_string_invalid` | `ServerUnreachable(InvalidTarget)`，存在性未知 | `database_target_preparation.invalid_target` | `migration.lock_not_supported`，经由通用属性预检 |
| `User Id=/`、空密码或方括号代理语法 | `database.connection_string_invalid` | `ServerUnreachable(InvalidTarget)`，存在性未知 | `database_target_preparation.invalid_target` | `migration.lock_not_supported`，在 ODP.NET builder/目标身份检查之后 |
| 未知属性、格式错误语法或无效属性值 | `database.connection_string_invalid` | `ServerUnreachable(InvalidTarget)`，存在性未知 | `database_target_preparation.invalid_target` | `migration.lock_failed`；不承诺作为不支持认证的诊断 |
| 缺失或空的 `Data Source` | `database.connection_string_invalid` | `ServerUnreachable(InvalidTarget)`，存在性未知 | `database_target_preparation.invalid_target` | `migration.lock_failed` |
| 目标会话建立前凭据无效 | `database.authentication_failed` | `TargetUnreachable(AuthenticationFailed)`，存在性未知 | 管理登录：`database_target_preparation.authentication_failed`；已存在目标探测与创建后探测：`database_target_preparation.target_conflict` | `migration.lock_failed` |
| 目标账户被锁定或过期 | `database.authentication_failed` | `TargetUnreachable(AuthenticationFailed)`，`TargetExists=true` | 已存在目标探测与创建后探测：`database_target_preparation.target_conflict` | `migration.lock_failed` |
| 目标缺少 `CREATE SESSION` | `database.permission_denied` | `TargetUnreachable(PermissionDenied)`，`TargetExists=true` | 管理登录：`database_target_preparation.permission_denied`；已存在目标探测：`database_target_preparation.target_conflict`；创建后探测：`database_target_preparation.permission_denied` | `migration.lock_failed` |
| 已连接的 `SESSION_USER` 与配置身份不匹配 | `database.connection_string_invalid` | `TargetUnreachable(InvalidTarget)`，`TargetExists=true` | 管理会话：`database_target_preparation.invalid_target`；已存在目标探测：`database_target_preparation.target_conflict`；创建后探测：`database_target_preparation.invalid_target` | `migration.lock_failed` |
| 已连接会话证明不支持的拓扑 | `database.connection_string_invalid` | `TargetUnreachable(InvalidTarget)`，`TargetExists=true` | 管理会话：`database_target_preparation.invalid_target`；已存在目标探测：`database_target_preparation.target_conflict`；创建后探测：`database_target_preparation.invalid_target` | `migration.lock_not_supported` |
| 必需的拓扑探测被拒绝 | `database.permission_denied` | `TargetUnreachable(PermissionDenied)`，`TargetExists=true` | 管理会话：`database_target_preparation.permission_denied`；已存在目标探测：`database_target_preparation.target_conflict`；创建后探测：`database_target_preparation.permission_denied` | `migration.lock_not_supported` |
| 监听器、服务、传输或协议失败 | `database.connection_failed` | `ServerUnreachable(ConnectionFailed)` | 管理会话：`database_target_preparation.connection_failed`；已存在目标探测：`database_target_preparation.target_conflict`；创建后探测：`database_target_preparation.connection_failed` | `migration.lock_failed`，或在获取截止时间先到时为 `migration.lock_timeout` |
| 意外的 provider/Oracle 失败 | `database.provider_validation_failed` | `ServerUnreachable(PreparationFailed)` | `database_target_preparation.preparation_failed` | `migration.lock_failed` |

输入拒绝发生在任何 provider I/O、管理 DDL 或锁分配之前。Validate 和 Observe 从不
发出 DDL 或分配锁。Prepare 只有在管理输入、连接、身份和拓扑检查全部成功之后，才
访问 `ALL_USERS`，随后才是 `CREATE USER`/`GRANT CREATE SESSION`；ADR 0001 的补偿
与错误优先级保持不变。Acquire 打开一个专用会话，并在
`DBMS_LOCK.ALLOCATE_UNIQUE_AUTONOMOUS` 和 `REQUEST` 之前完成其身份/拓扑探测。

外部配置盲区意味着，如果 ODP.NET 产生了预期的会话身份，某个未被识别的认证模式
可能到达这些较晚的操作。这不保证所有不支持的模式都会在副作用之前被拒绝。这也
正是未来的支持需要显式的凭据与身份契约、而不能依赖连接成功的原因。

## 凭据、连接池与事务所有权

目标和管理连接字符串仍然是调用方提供的输入。ServiceMantle 不持久化、导出、续期、
轮换、下载或记录其中的凭据。它也不承诺调用方、进程、ODP.NET 诊断、Oracle Net 或
数据库不会在本库自有结果与异常投影之外保留或记录数据。

Bootstrap 验证和目标观察保留目标 builder 当前的池化/加入事务设置，只应用有界
连接超时。准备只强制管理连接使用 `Pooling=false` 和 `Enlist=false`；目标验证保留
目标连接的设置。migration 锁强制其专用目标用户连接使用 `Pooling=false` 和
`Enlist=false`。锁的物理会话拥有 `DBMS_LOCK` 租约，而消费方继续拥有 schema 对象、
migration 和任何消费方事务。增加钱包、token、外部身份或代理将需要针对池键、凭据
过期、会话重置和租约丢失的显式规则；当前规则不提供这些。

## 重新开启某个模式所需的证据

任何未来的支持提案都必须从一份独立决策开始，并且在凭据或目标身份无法放入现有
配置时，附带一个 provider 中立的凭据或目标 SPI 任务。除非另有独立容器决策先行
重新开启该边界，否则必须保持同 PDB 目标契约。缺失的基础设施、凭据、权限或已发现
的测试必须让必需的 CI 失败，而不是跳过。

每个重新开启的模式都需要（在适用处）最小权限的目标与管理员账户、机器验证的
会话/schema/代理/容器身份、Bootstrap 与准备的失败/取消测试，以及两个独立锁会话
证明同服务互斥、不同服务互不干扰、超时、释放和被杀会话的租约丢失。模式特定证据
同样是必需的：

1. **钱包/mTLS：** 真实的自管理服务器与客户端证书映射、固定的 TLS 与信任策略、
   有效/过期/吊销/错误 DN 的证书、有密码保护与自动登录的钱包、安全的临时物化、
   新连接期间的续期、池隔离，以及只删除本次运行自有钱包文件的确定性清理。
2. **Token：** 所声称的每种 OCI IAM 或 Entra 流程、可信的工作负载/用户身份来源、
   audience 与数据库映射、（在适用处）应用提供的与 provider 获取的 token、刷新
   回调竞争、过期/吊销/错误 audience 的 token、新建与已打开池化会话的差异、
   私钥/文件清理，以及诊断中不出现 token。Oracle 的
   [token connection documentation](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/featConnecting.html)
   把应用提供凭据的刷新列为应用生命周期职责；当前仅基于连接字符串的 SPI 不实现
   它。
3. **外部/OS：** 每种受支持的平台与方法（例如 NTS 或 Kerberos）、隔离的进程身份、
   Oracle Net 与数据库映射、预期的 `AUTHENTICATION_METHOD`/`AUTHENTICATED_IDENTITY`、
   不可用的票据或身份、续期与吊销，以及另一个服务进程无法仅通过共享机器配置继承
   目标的证明。
4. **代理：** 所声称的两种 ODP.NET 代理形式、显式的代理/客户端/schema 所有权、
   `SESSION_USER`、`PROXY_USER`、认证方法、直接授权与受限角色、缺失/吊销的
   `CONNECT THROUGH`、错误客户端、池复用/会话重置，以及锁分配与 migration 在预期
   客户端 schema 下执行的证明。ODP.NET 在其
   [connection-string reference](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/ConnectionConnectionString.html)
   中记录了这两种身份和连接属性。

机密材料只能经由受保护的 CI 身份或机密设施进入，不得出现在仓库内容、缓存、
构建产物、测试名称和已脱敏结果中，并且必须在 `finally` 中从一个运行独占的位置
移除。测试不得仅仅为了掩盖权限契约而授予 DBA/SYSDBA，也不得把 Oracle Free 当作
它并不提供的认证服务的证据来复用。

本关闭决策不创建任何实现任务。#317 仍是已知邻近债务，不在此修复；未发现其他
邻近缺陷。本文档不为四个关闭的认证族添加任何 provider 代码、凭据 SPI、SQL、
测试、包元数据、CI、README 更改或支持保证。
