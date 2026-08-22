# ServiceMantle 后端公共库剩余范围规划

> 状态：持续更新的后续实施范围  
> 更新日期：2026-08-23  
> 目标：记录 ServiceMantle 在当前代码基线之上仍需实现的后端公共能力。已经完成并通过验收的工作不再列入待办范围。  
> 原则：ServiceMantle 管理服务如何安装、配置、认证管理、审计和观测；消费服务继续拥有自己的业务领域、业务迁移和身份模型。

## 1. 文档用途

本文件由最初的《服务管理后端公共库范围规划》收敛而来。当前仓库已经完成的基础能力已从后续任务清单中剔除，后续任务应以本文件为范围依据。

当前只规划后端公共库。管理前端及其复用方式继续由独立仓库 `ServiceMantle.Console` 承担，待管理 API 稳定后再规划。

## 2. 当前代码基线

以下能力已经实现并通过验收，仅作为后续工作的前置条件，不再属于本文件的待办任务：

- `ServiceId`、`InstanceId` 及三阶段启动状态模型；
- 本地 Bootstrap 文件的兼容读取、严格校验、安全权限、原子创建和替换；
- Bootstrap 状态、创建和更新用例，以及不返回秘密的管理模型；
- 数据库 Provider SPI、Provider Registry 和候选配置分发验证；
- PostgreSQL Bootstrap Provider，包括版本、连接字符串、目标库连通性和安全错误分类；
- 核心包、PostgreSQL Provider 包及其依赖隔离；
- `IServiceInstallationStore`；
- `service_installations` 实体、跨 Provider EF Core 映射和 Store；
- 安装记录的幂等创建、完成转换、乐观并发和状态不变量校验；
- 消费服务拥有业务 `DbContext` 和迁移的集成方式。

当前代码包为：

```text
ServiceMantle
ServiceMantle.Database.PostgreSql
ServiceMantle.Persistence.EntityFrameworkCore
```

## 3. 已确定且后续不得推翻的边界

### 3.1 Bootstrap 文件属于本地实例

每个服务实例都必须拥有自己的 Bootstrap 文件，文件中保存：

- `ServiceId`；
- 数据库 Provider；
- 可选或必需的数据库版本；
- 数据库连接字符串；
- 外部根密钥；
- Bootstrap 格式版本。

数据库连接信息和外部根密钥不得写入业务数据库。

多个实例之间如何分发或同步 Bootstrap 文件属于部署系统职责，不属于当前 ServiceMantle 后端库的同步功能。管理 API 修改 Bootstrap 时，只修改接收该请求的实例本地文件，并明确返回需要重启。

### 3.2 共享状态进入业务数据库

同一服务的多个实例共享：

- 稳定的 `service_id`；
- 安装状态；
- 配置及配置版本；
- 审计日志；
- Data Protection Keys；
- Setup 流程中需要跨实例共享的安全状态。

这些状态必须保存在共享业务数据库中，不能使用每实例本地 SQLite 作为全局配置权威。

`instance_id` 只用于日志、指标、服务发现和诊断，不能用于共享配置、安装状态或管理员身份隔离。

### 3.3 业务 DbContext 和迁移归消费服务所有

ServiceMantle 提供公共实体、EF Core 映射、Store 和迁移编排扩展点；消费服务负责：

- 把公共实体加入自己的业务 `DbContext`；
- 生成并维护业务迁移；
- 决定数据库是否为空或可接管；
- 提供业务初始化 Contributor；
- 在部署流程中应用最终迁移集合。

ServiceMantle 不内置 SignaCore 的 `IdentityDbContext` 或业务迁移，也不在当前阶段引入独立 `ManagementDbContext`。

这样可以使安装状态、公共配置、初始管理员及业务初始化继续共享同一个业务事务。

### 3.4 管理授权不等于本地管理员

每个服务都需要统一的管理授权边界，但不要求每个服务都维护本地管理员用户名和密码。

后续公共库提供管理身份接入机制。身份来源由消费服务选择：

- SignaCore 可以使用自己的账户体系；
- 普通生产服务可以接入 SignaCore 或其他 OIDC Provider；
- 离线服务可以选择未来的本地管理员模块；
- break-glass 紧急账户应作为独立可选模块。

## 4. 剩余功能范围

### 4.1 数据库迁移编排和多实例锁

这是当前基线之后的第一优先级。

ServiceMantle 仍需提供：

- Provider 无关的迁移编排接口；
- 消费服务迁移执行器或迁移 Contributor 接口；
- 迁移前、迁移中、迁移后状态和安全结果模型；
- 迁移锁的可选 Provider 能力接口；
- PostgreSQL advisory lock 实现；
- 基于 `service_id` 的稳定锁标识；
- 带超时和取消的锁获取；
- 锁生命周期覆盖全部消费服务迁移；
- 迁移失败时不进入 Setup 或 Completed 阶段；
- 多实例等待、重新检查和幂等恢复；
- 已有数据库、空数据库、需要迁移和版本过新的状态表达；
- 安全诊断，禁止输出连接字符串和外部根密钥。

消费服务继续负责真正的业务 Migration。ServiceMantle 只负责在正确的共享锁和启动阶段下编排它。

Provider 锁不能强制所有数据库使用同一种实现：

- PostgreSQL：advisory lock；
- MySQL/MariaDB：未来可使用 `GET_LOCK`；
- SQL Server：未来可使用 `sp_getapplock`；
- Oracle：需要独立的锁策略；
- SQLite：只支持明确的单实例模式。

### 4.2 数据库目标准备

PostgreSQL Provider 当前只验证目标数据库是否存在并可连接，仍需规划可选的数据库准备能力：

- 识别目标不存在；
- 区分“服务器可连接”和“目标数据库可连接”；
- 可选的目标数据库创建；
- 创建权限不足时返回稳定错误；
- 不覆盖、删除或重新创建已有数据库；
- 不把数据库管理员连接信息写入业务数据库；
- 对 `ServerDatabase`、`File` 和 `ServerSchema` 使用不同能力接口。

数据库准备必须是 Provider 的可选能力，不能扩展现有验证接口后强迫所有 Provider 支持建库。

Oracle 不应复制 PostgreSQL 的“一服务一数据库”假设；其目标可能是 Schema 或 User。MySQL 与 MariaDB 即使共享底层驱动，也必须保留不同的逻辑 Provider ID。

具体的 SQLite、MySQL、MariaDB、Oracle 和 SQL Server Provider 可以在核心编排接口稳定后分别实现，不阻塞 PostgreSQL 首条完整路径。

### 4.3 Setup Code 和首次安装编排

ServiceMantle 仍需提供：

- 一次性 Setup Code 的生成、哈希、验证、过期和轮换；
- Setup Code 的安全显示边界；
- 多实例共享的 Setup 状态；
- 安装完成后永久关闭匿名 Setup 流程；
- Setup 请求重放防护；
- Setup Contributor 注册、排序和执行；
- Contributor 的验证阶段和执行阶段；
- 单一业务事务中的 Contributor 执行；
- Contributor 失败时整体回滚；
- 安装完成状态、初始配置和安装审计的一致提交；
- 并发 Setup 只允许一个实例成功。

建议扩展点仍采用类似以下语义：

```csharp
public interface ISetupContributor
{
    Task ValidateAsync(
        SetupContext context,
        CancellationToken cancellationToken);

    Task ExecuteAsync(
        SetupContext context,
        CancellationToken cancellationToken);
}
```

SignaCore 的初始管理员创建由 SignaCore Contributor 完成，不进入 ServiceMantle 核心包。其他服务可以提供不同 Contributor，也可以不创建本地管理员。

### 4.4 数据库配置管理

ServiceMantle 仍需实现：

- `service_settings` 持久化实体和 EF Core 映射；
- 配置定义注册；
- 字符串、数字、布尔值和 JSON 等值类型；
- 默认值、必填项和取值约束；
- 敏感配置标记；
- 使用 Bootstrap 外部根密钥加密敏感配置；
- 完整配置快照读取和校验；
- 配置版本递增和乐观并发；
- 事务化批量更新；
- 更新者、更新时间和是否需要重启；
- 配置列表和更新的后端用例；
- 审计只记录配置键，不记录敏感值。

消费服务通过定义 Provider 声明自己的配置目录，例如：

```csharp
public interface ISettingDefinitionProvider
{
    IEnumerable<SettingDefinition> GetDefinitions();
}
```

SignaCore 的 JWT、Refresh Token、SMS、LDAP、微信、Callback 和身份领域配置继续由 SignaCore 声明。

### 4.5 管理身份、授权和多实例会话

ServiceMantle 仍需提供：

- `ManagementAdmin` 授权策略；
- 管理身份 Claims 约定；
- 管理角色和权限表达；
- 当前管理操作者解析；
- 管理身份 Provider 接口；
- 未认证、无权限和会话过期的统一结果；
- 管理 Cookie 的安全默认值；
- 所有管理接口默认受保护的约束；
- ASP.NET Core Data Protection 统一配置；
- `service_data_protection_keys` 实体和 EF Core 映射；
- 数据库 Data Protection Key Repository；
- Key XML 的二次加密；
- Application Name 与 `service_id` 隔离；
- 多实例共享管理 Cookie；
- 密钥写入并发处理。

推荐身份扩展点保持 Provider 形式：

```csharp
public interface IManagementIdentityProvider
{
    Task<ManagementAuthenticationResult> AuthenticateAsync(
        ManagementAuthenticationContext context,
        CancellationToken cancellationToken);
}
```

ServiceMantle 核心包不实现 SignaCore 用户密码校验，也不把本地管理员固化为必选能力。

### 4.6 审计日志

ServiceMantle 仍需实现：

- `service_audit_logs` 实体和 EF Core 映射；
- 操作者 ID、显示名称和身份来源；
- Action、Target Type 和 Target ID；
- Client IP、Correlation ID 和时间；
- 可选的安全描述及结构化元数据；
- 通用写入服务；
- 分页、筛选和时间范围查询；
- 敏感字段清理约束；
- 安装、登录、配置变更和高风险操作的基础审计事件。

消费服务负责定义自己的业务 Action、Target Type 和审计触发点。ServiceMantle 不内置 SignaCore 的用户、应用或密钥轮换语义。

### 4.7 日志与请求关联

ServiceMantle 仍需提供：

- Correlation ID 的读取、生成和响应回写；
- Correlation ID 注入日志 Scope；
- `ServiceName`、`ServiceVersion` 和 `InstanceId` 日志字段；
- 日志值清理和敏感数据掩码工具；
- Serilog Host 配置入口；
- Console、Loki 等 Sink 的可选接入；
- 进程退出时日志刷新；
- 安全默认日志配置。

ServiceMantle 不保存和查询完整业务日志。日志检索继续由 Loki、Elasticsearch 或其他外部系统负责。

### 4.8 监控与健康检查

ServiceMantle 仍需提供：

- OpenTelemetry 基础接入；
- ASP.NET Core、HttpClient 和 Runtime Instrumentation；
- OTLP Exporter；
- Prometheus 指标端点；
- `/health/live`、`/health/ready` 和兼容 `/health` 的约定；
- 数据库和迁移状态健康检查；
- 安装未完成时 Readiness 失败；
- 通用服务信息和安装状态指标；
- 业务健康检查 Contributor。

SignaCore 签名密钥等业务就绪条件仍由 SignaCore 自己提供。

### 4.9 服务发现

ServiceMantle 仍需实现可选的 Consul 集成：

- 启用和禁用；
- 服务注册和注销；
- Service Name、Instance ID、地址和端口；
- 健康检查路径；
- Token 等敏感配置；
- 从数据库配置快照读取 Consul 配置；
- 安装或迁移未完成时避免注册为可用业务实例。

ServiceMantle 只负责服务注册和发现，不使用 Consul KV 作为配置权威。

### 4.10 ASP.NET Core 基础设施和管理 API

在身份、Setup 和配置边界稳定后，ServiceMantle 仍需提供：

- 独立的 ASP.NET Core 集成包；
- Correlation ID Middleware；
- 统一 Problem Details；
- 通用异常映射扩展点；
- Forwarded Headers 安全配置；
- 管理接口基础限流；
- 敏感 Header 清理；
- 管理 API 路径前缀；
- 管理端点安全响应头；
- 安装状态 API；
- Bootstrap 状态、创建和更新 API；
- Setup API 基础协议；
- 配置列表和更新 API；
- 管理会话状态 API；
- 审计查询 API；
- 服务运行信息 API。

Bootstrap API 必须明确：

- 它修改的是当前实例本地文件；
- 修改后需要重启；
- 未安装阶段使用何种一次性安全凭据；
- 已安装阶段必须受管理授权保护；
- 不在响应中返回连接字符串或外部根密钥。

SignaCore 的 `X-Admin-AppSecret` 属于业务协议，不成为 ServiceMantle 固定规则，只能通过可配置的敏感 Header 清理扩展接入。

## 5. 明确不属于 ServiceMantle 后端公共库

以下能力继续保留在 SignaCore 或其他业务服务：

- OAuth/OIDC 协议端点；
- JWT 签发、验证和 RSA 签名密钥生命周期；
- 用户、账户、密码凭据和外部登录绑定；
- 应用注册、AppSecret 和应用信任关系；
- SMS、LDAP、微信登录；
- Gateway、Callback、Profile 和 Refresh Token；
- SignaCore 特有配置键和组合验证；
- `bootstrap-apps.json` 业务预置；
- 用户、应用和登录策略管理 API；
- 通用动态业务 CRUD。

后端首版也不包含：

- 管理前端；
- 完整本地管理员账户模块；
- 内置 OIDC 登录实现；
- break-glass 紧急管理员实现；
- 细粒度动态权限管理系统；
- 日志存储和日志查询；
- 指标数据存储和监控大盘；
- 配置热更新；
- Consul KV 配置；
- Kubernetes Operator；
- 多租户控制面；
- 跨服务集中管理控制台；
- Bootstrap 文件跨实例同步。

## 6. 后续包边界建议

继续保持依赖隔离，避免把数据库驱动、EF Core 和 ASP.NET Core 依赖全部放入核心包。

建议后续包形态：

```text
ServiceMantle
├── 领域模型、接口、编排和安全结果

ServiceMantle.Persistence.EntityFrameworkCore
├── 公共管理实体、映射和 Store

ServiceMantle.Database.PostgreSql
├── PostgreSQL 验证、目标准备和迁移锁

ServiceMantle.AspNetCore
├── DI、Middleware、授权策略和管理端点

未来可选 Provider
├── ServiceMantle.Database.SQLite
├── ServiceMantle.Database.MySql
├── ServiceMantle.Database.MariaDb
├── ServiceMantle.Database.Oracle
└── ServiceMantle.Database.SqlServer
```

日志、监控和 Consul 是否继续拆包，应在实际依赖出现时决定，不需要现在预先创建空项目。

## 7. 更新后的实施顺序

### 阶段一：迁移闭环

1. 定义迁移编排和可选锁接口；
2. 实现 PostgreSQL advisory lock；
3. 接入消费服务迁移执行器；
4. 验证两个实例不会重复迁移；
5. 补齐数据库状态和失败诊断。

### 阶段二：首次 Setup 闭环

1. Setup Code；
2. Setup Contributor；
3. 安装业务事务；
4. 并发 Setup 控制；
5. 安装完成状态和审计一致提交；
6. SignaCore 初始管理员 Contributor。

### 阶段三：共享管理数据

1. 配置定义、实体、版本和加密；
2. 审计实体、写入和查询；
3. Data Protection Keys；
4. 多实例 Cookie 验证。

### 阶段四：管理授权和 ASP.NET Core

1. 管理身份 Provider；
2. Claims 和授权策略；
3. Cookie 安全配置；
4. Problem Details 和 Correlation ID；
5. Bootstrap、Setup、配置和审计 API。

### 阶段五：可观测性和服务发现

1. Serilog；
2. OpenTelemetry 和 Prometheus；
3. 健康检查；
4. Consul 注册；
5. 安装阶段与正常运行阶段的可用性联动。

### 阶段六：SignaCore 迁移验证

1. SignaCore 引用 ServiceMantle；
2. SignaCore 业务 DbContext 接入公共映射；
3. 保持首次安装事务语义；
4. 替换 SignaCore 中对应公共实现；
5. 删除重复代码；
6. 验证双实例安装、迁移、会话和配置一致性。

## 8. 剩余范围的最终验收标准

后端首版完成时应满足：

- SignaCore 引用 ServiceMantle 后，现有首次安装行为不退化；
- 配置、初始管理员、审计和安装完成保持所需事务一致性；
- 两个实例连接同一个 PostgreSQL 数据库时不会重复迁移或重复初始化；
- 两个实例读取相同配置版本；
- 管理 Cookie 可以跨实例使用；
- 最小示例服务无需复制 SignaCore 源码即可接入安装、配置、审计、健康检查和 Consul；
- 示例服务可以选择外部管理身份，不需要创建本地管理员；
- ServiceMantle 不引用 SignaCore 的账户、应用、OAuth、JWT、SMS、LDAP 或微信类型；
- ServiceMantle 不包含 SignaCore 特有配置键；
- 连接字符串、外部根密钥和敏感配置不会出现在日志、API 响应或审计描述中；
- SQLite 模式明确限制为单实例；
- PostgreSQL 路径通过多实例并发安装和迁移测试；
- 管理端后端 API 稳定到足以供 `ServiceMantle.Console` 独立接入。

## 9. 当前下一任务

当前代码基线完成后的下一任务是：

> 定义 Provider 无关的迁移编排与迁移锁接口，并实现 PostgreSQL advisory lock，使消费服务拥有的业务迁移能够在多实例部署下安全执行。

这一步只解决迁移执行顺序、锁生命周期、超时取消和安全结果，不同时引入 Setup Code、配置表、管理员或 HTTP API。
