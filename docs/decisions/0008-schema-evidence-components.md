# ADR 0008：EF 模型对照的 schema 证据构件与基线写入原语

- 日期：2026-10-01；状态：已接受；本 ADR 不含产品代码，实现由独立 task 交付
- 决策 issue：[#600](https://github.com/philfanzhou/ServiceMantle/issues/600)
- 代码基线：`187e159c04aeeb7e06746f7fd10156d7a28c56b9`
- 关联边界：`MIGRATION_ORCHESTRATION.md`「集成示例」（接管判断归消费方）、
  [ADR 0007](0007-provider-neutral-contract-boundary.md)（契约归属判据）

## 背景

三个消费方各自实现了面向 PostgreSQL 的「遗留库接管」迁移执行器
（`IDatabaseMigrationExecutor`），合计约 2800 行：

| 消费方 | 文件 | 行数 | 自有差异 |
| --- | --- | ---: | --- |
| Doctheca | `src/Database/DocthecaMigrationExecutor.cs` | 1009 | 外键校验、可安全回填的缺失列与索引、`updated_at` 触发器 |
| Quaestura | `src/Database/QuaesturaMigrationExecutor.cs` | 934 | 已应用迁移必须是已知列表的有序前缀；接管只 stamp InitialCreate 后由 EF 执行其余 |
| Ruoyu.Admin | `backend/Admin.WebApi/Database/AuditMigrationExecutor.cs` | 863 | identity 列（`GENERATED ALWAYS AS IDENTITY`）校验，无外键；缺失列不回填，直接拒绝 |

`MIGRATION_ORCHESTRATION.md`「集成示例」已明确：判断未知数据库能否安全接管是消费服务
自己的问题，库给不出通用答案。本 ADR 回答的问题因此**不是**「库要不要做接管判断」，而是：
在不改变该边界的前提下，ServiceMantle 是否应提供**证据读取与比对构件**，让消费方
executor 只保留决策规则。

## 逐项差异归属分析

对三份实现逐项拆解，每项标注归属：**构件**（三份相同或仅读取维度不同，收敛进库）或
**消费方**（规则因服务而异，保留在 executor）。

| # | 能力 | 三份现状 | 归属 |
| --- | --- | --- | --- |
| 1 | 连接失败分类 | 逐行相同：SQLState `3D000` 视为「目标不存在」，认证/网络/权限失败拒绝，绝不解释为「缺失」后走创建分支 | 构件提供两个**事实**（目标不存在 / 读取失败）；「不存在算 Empty」的分类决策归消费方 |
| 2 | 业务表名清单（`pg_class`/`pg_namespace`） | 逐行相同 | 构件 |
| 3 | 已应用迁移 id（`__EFMigrationsHistory`） | 逐行相同 | 构件 |
| 4 | 列快照（名、`format_type` 类型串、可空性） | Doctheca/Quaestura 相同；Ruoyu.Admin 增读 `attidentity` | 构件（identity kind 一并作为读取维度） |
| 5 | 主键快照与期望推导 | 逐行相同 | 构件 |
| 6 | 外键快照与期望推导（列、引用表/列、`DeleteBehavior` → 删除规则映射） | Doctheca/Quaestura 相同；Ruoyu.Admin 模型无外键 | 构件（期望侧无外键时自然不产生该维度差异） |
| 7 | 非约束索引快照与期望推导（列、唯一性） | 逐行相同 | 构件 |
| 8 | EF 模型期望结构推导 | 逐行相同；Ruoyu.Admin 增 identity 生成策略推导 | 构件 |
| 9 | 逐表差异验证循环（缺失/多余列、类型、可空性、PK/FK/索引匹配） | 骨架相同，失败即返回首个原因 | 构件（改为输出**全部结构化差异**，见下） |
| 10 | 迁移契约自检（程序集迁移 == 已知冻结契约） | 结构逐行相同，仅错误消息文字微差 | 消费方（冻结的契约清单本身是消费方决策；构件不持有任何服务的迁移 id） |
| 11 | 五态分类（Empty / CurrentVersionCompatible / PendingMigration / VersionTooNew / InspectionFailed） | 三份规则互不相同 | 消费方 |
| 12 | 有序前缀检查（applied 必须是已知列表前缀） | 仅 Quaestura | 消费方 |
| 13 | 部分表集拒绝规则 | 三种不同细节（全表必须齐 / 初始四表 + tag 表部分拒绝 / 全表必须齐） | 消费方 |
| 14 | 安全回填收集策略（哪些缺失列/索引算可安全回填） | Doctheca 允许（可空或有默认的列、索引）；Ruoyu.Admin 仅索引，缺失列一律拒绝；Quaestura 无回填 | 消费方（构件只输出差异，含「缺失列是否可空、有无默认」的判断材料） |
| 15 | 回填 DDL 生成与标识符引用 | Doctheca/Ruoyu.Admin 有，引用细节不同 | 消费方（本次不提供通用 DDL 生成，见「非目标」） |
| 16 | `updated_at` 触发器回填 | 仅 Doctheca | 消费方（应用特有） |
| 17 | 基线写入（独立事务、`CREATE TABLE IF NOT EXISTS`、参数化幂等 `INSERT … WHERE NOT EXISTS`） | 三份方法体经规范化 diff 完全相同；差异仅在调用语义（Quaestura 只 stamp InitialCreate 后由 EF 执行其余） | 构件（写入原语；**何时写、写哪个 id** 由消费方决定） |
| 18 | 取消语义（阶段间检查点、取消与内部异常区分） | 逐行相同 | 构件（随读取与写入原语提供） |
| 19 | 安全诊断（错误与日志只含标识符，无 SQL 文本、无连接值） | 逐行相同 | 构件（差异与错误消息沿用同一纪律） |

收敛后的量化预期：构件覆盖各份约 600–700 行（快照读取、期望推导、差异比对、基线写入、
取消与安全诊断），消费方 executor 各保留约 150–250 行决策规则；2800 行降为约 700 行构件
加三份短规则。差异能否全部落在「消费方规则」一侧——**能**：上表 11–16 行的全部分歧都
是决策，而 1–9、17–19 的全部共同点都是证据与原语。

## 决策

采用候选边界 **2：只读证据构件 + 基线写入原语**。

1. **只读证据构件**：PostgreSQL 实际 schema 快照读取器（`ServiceMantle.Database.PostgreSql`）
   + EF 模型期望结构推导（`ServiceMantle.Persistence.Relational`）+ 结构差异比对，
   输出结构化差异列表；不做分类决策、不写库。
2. **基线写入原语**：在独立事务中向 EF 历史表幂等写入指定基线迁移 id 的原语
   （调用方决定何时写、写哪个 id）。

不采用候选边界 1（只做只读构件）：基线写入在三份实现中逐行相同且是安全敏感代码
（幂等、事务边界、参数化），没有理由留在消费方；提供原语并不越界，因为 stamp 的
内容与时序仍是消费方决策。

不采用候选边界 3（维持现状）：三份共同部分逐行相同，取消语义、参数化、幂等 stamp
这类修正目前要修三次；Ruoyu.Study 等尚未迁移的服务按现状会复制出第四份。

### 与既有边界的关系

- 「接管判断归消费方」不变：构件不产生 Empty/Compatible/TooNew 等分类，不判断差异
  是否可接受，不执行迁移。消费方 executor 拿结构化差异列表自行分类与决策。
- `DatabaseMigrationOrchestrator`、锁 provider、`IDatabaseMigrationExecutor` 契约不变。
- `MIGRATION_ORCHESTRATION.md`「集成示例」的陈述无需修改；实现交付时在同一文档补充
  证据构件的用法一节。

## 公开类型草案

名称为实现时的示意，最终以 `CONTRIBUTING.md`「命名规范」定稿。

### 核心包 `ServiceMantle`（`ServiceMantle.Migration` 能力域）

中立快照、期望与差异模型，以及纯函数比对器。按 ADR 0007 判据属 **A 类**：消费方在
executor 源码里必须点名这些类型（读差异、做分类），契约中没有任何 provider 特有成分。

- `SchemaSnapshot`：不可变的实际结构快照——表、列（名、类型串、可空性、identity kind）、
  主键、外键（列、引用表/列、删除规则）、非约束索引（列、唯一性）。
- `ExpectedSchema`：与快照同构的期望模型。
- `SchemaDifference`：结构化差异（种类 + 表/列/约束/索引标识 + 期望与实际值），附
  「缺失列是否可空、有无存储默认值」等供回填判断的证据字段。
- `SchemaEvidenceComparer.Compare(snapshot, expected)`：返回全部差异，不做任何决策。
- 目标读取结果类型：区分「目标数据库不存在」「读取失败」两个事实，供消费方把前者
  归类为 Empty、后者归类为 InspectionFailed。

### `ServiceMantle.Persistence.Relational`（`ServiceMantle.Persistence.Relational`）

- EF 模型期望推导：`IModel` → `ExpectedSchema`，含 value generation 策略（identity）
  的推导。
- 基线写入原语：向 EF 历史表（表名与 schema 经 `IHistoryRepository` 解析）参数化、
  幂等写入一个迁移 id，独立事务提交。

### `ServiceMantle.Database.PostgreSql`（`ServiceMantle.Database.PostgreSql`）

- PostgreSQL 证据读取器（B 类，类型名保留产品词）：在调用方打开的连接上读取表名
  清单、已应用迁移 id 与 `SchemaSnapshot`（`pg_catalog`，含 `attidentity`），并区分
  「目标不存在」（SQLState `3D000`）与其他读取失败。

依赖方向不新增环：PostgreSql 包只依赖核心包（输出中立快照模型）；EF 期望推导与
基线写入依赖既有 EF Core Relational；比对器是核心包纯函数。

## 非保证

- **比对维度**限于：表存在性、列（名、类型串、可空性、identity kind）、主键、外键、
  非约束索引（列、唯一性）。**不比对**：CHECK 约束、列默认值（默认值仅作为差异证据
  字段输出，供消费方回填判断）、非主键唯一约束、触发器、视图、存储过程、序列状态、
  权限、分区、表空间、注释、排序规则、索引的部分谓词（`WHERE`）、表达式正文/位置与排序方向、存储参数。消费方如需拒绝这些维度上的偏差，必须自己补充读取。
- 类型比较是 provider 方言字符串的**精确匹配**，不解释语义等价（`integer` 与 `int4`
  视为不同）；方言归一化不是本构件的承诺。
- 读取在调用方提供的连接上执行，不开启自己的快照事务；多次读取之间的结构变化由
  调用方的时序约束（通常是编排器租约）覆盖。
- 基线写入原语只保证单次调用的幂等与事务性；**不验证**被写入的迁移 id 与实际结构
  一致——「先证据、后 stamp」的顺序与验证责任在消费方 executor。
- 差异列表是「本次读取所见」的完整清单，不承诺跨版本的结构漂移审计。

## 对 ADR 0007 的影响

无既有类型迁移。新类型的归属按 0007 判据判定并在上文明示：中立模型与比对器是 A 类
（进核心包 `ServiceMantle.Migration`）；pg_catalog 读取器是 B 类（留在 provider 包，
类型名保留产品词）；EF 期望推导与基线写入按签名依赖 EF Core 的判据留在
`ServiceMantle.Persistence.Relational`。消费验证项目需覆盖上述每个包。

## 后续 provider 支持的代价

期望推导、比对器与基线写入原语都是 provider 中立的；新增 provider 的全部代价是
一个快照读取器（把该 provider 的系统目录读成核心包 `SchemaSnapshot`）及其真实数据库
集成测试。MySQL/SQL Server 的支持各是一个独立 task，不在本 ADR 的实现范围内预支。

## 非目标

- 不提供「差异 → 回填 DDL」的生成器：哪些差异可安全回填是消费方决策，触发器等回填
  本来就是应用特有的。若未来多份消费方规则再次趋同，可另立决策评估。
- 不做 EF Core 自动迁移、不做接管分类、不读取 `IConfiguration`。
- 不在本 ADR 中实现产品代码。

## 拆分的实现 issue

| Issue | 层 | 内容 | Blocked by |
| --- | --- | --- | --- |
| 核心包证据模型与比对器 | L01 | `SchemaSnapshot` / `ExpectedSchema` / `SchemaDifference` / 比对器与目标读取结果类型，单元测试 | #600（本 ADR） |
| EF 期望推导与基线写入原语 | L01 | `ServiceMantle.Persistence.Relational` 的 `IModel` → `ExpectedSchema` 推导与幂等基线写入，单元与集成测试 | 核心包证据模型 |
| PostgreSQL 证据读取器 | L01 | `ServiceMantle.Database.PostgreSql` 的快照读取器，Testcontainers 集成测试 | 核心包证据模型 |

三个消费方（Doctheca、Quaestura、Ruoyu.Admin）各有一个替换 issue（见各自仓库），
把自有 executor 改为「证据构件 + 自有决策规则」，均以上述实现 issue 为前置。

## #638 的证据扩展

核心新增可空对象名、索引总键数量与独立 INCLUDE 列，并以显式选项启用比较；旧构造与默认比较保持。
扩展后的唯一语义规则见 [核心 schema 证据模型](../contracts/schema-evidence-models.md)。
读取器与 EF 推导分别由 #639/#640 在核心扩展合并后实施，本 ADR 不预支其交付。
