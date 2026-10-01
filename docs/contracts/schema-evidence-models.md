# 核心包 schema 证据模型与差异比对器

ADR 0008（[0008-schema-evidence-components.md](decisions/0008-schema-evidence-components.md)）拆分出的
核心包构件（#608）：中立、不可变的快照/期望/差异模型，纯函数比对器，以及区分「目标数据库不存在」
与「读取失败」的读取结果类型。落在 `ServiceMantle.Migration` 能力域，核心包不新增依赖。

## 类型一览

| 类型 | 职责 |
| --- | --- |
| `SchemaSnapshot` | 一次读取观察到的实际结构：表、列（名、类型串、可空性、identity kind）、主键、外键（列、引用表/列、删除规则）、非约束索引（列、唯一性） |
| `ExpectedSchema` | 与快照同构的期望结构（由消费方模型推导，例如后续持久化包的 EF 推导） |
| `SchemaTable` / `SchemaColumn` / `SchemaPrimaryKey` / `SchemaForeignKey` / `SchemaIndex` | 共享的中立成员模型，两种包裹类型共用 |
| `SchemaIdentityKind` / `SchemaForeignKeyDeleteRule` | identity 生成类别（None/Always/ByDefault）与删除规则（NoAction/Restrict/Cascade/SetNull/SetDefault）的中立枚举 |
| `SchemaDifference` / `SchemaDifferenceKind` | 结构化差异：种类 + 表/列标识 + 期望与实际的中立模型对象 |
| `SchemaEvidenceComparer` | 纯函数 `Compare(snapshot, expected)`，输出全部差异，不做任何分类或接受/拒绝决策 |
| `SchemaEvidenceReadResult` / `SchemaEvidenceReadState` | 读取结果：成功（携带已应用迁移 id 与快照）/ 目标不存在 / 读取失败，两个失败事实分开 |

## 比对维度（封闭清单）

- 表存在性：按「schema + 表名」序数比较。
- 列存在性：按列名序数比较；列的类型串**精确匹配**（不做 `integer`/`int4` 之类方言归一化）、可空性、
  identity kind 逐维各出一条差异。
- 主键：存在性与有序列清单。
- 外键：按形状（列、引用 schema/表/列）关联；缺失/多余/删除规则不同各出一条差异。
- 非约束索引：按列清单关联；缺失/多余/唯一性不同各出一条差异。索引名不在模型内。

维度之外的任何结构（CHECK 约束、默认值、触发器、视图、权限、分区、注释等）不产生差异；
`SchemaColumn.HasStoredDefault` 是回填判断证据字段，本身永不产生差异（默认值的具体内容也不进模型）。

## 差异的证据载荷

每种差异按种类恰好填一对载荷（另一侧缺失时为 null）：表差异带 `ExpectedTable`/`ActualTable`，
列差异带 `ExpectedColumn`/`ActualColumn`，主键/外键/索引差异带对应对象。缺失列差异携带完整期望列，
其 `IsNullable` 与 `HasStoredDefault` 就是消费方判断「可否安全回填」的材料——比对器自身不做判断。

差异顺序确定：按期望表顺序逐表输出（缺失表、列差异、主键、外键、索引），最后按快照顺序输出多余表。

## 读取结果的两个事实

`TargetDatabaseMissing` 与 `ReadFailed` 是两个独立状态，由消费方各自分类（例如前者归 Empty、后者归
InspectionFailed）。`Message` 由类型内部以「一个已校验标识符 + 固定英文模板」渲染，不存在能把 SQL
文本、连接串或异常消息带进去的入口；标识符含控制字符、为空或超过 128 字符会被拒绝。

## 保证与不保证

- 保证：给定快照与期望，差异列表确定、完整覆盖声明的比对维度；模型与差异不可变；比对器无 I/O、
  无时钟、无状态；差异与消息只含标识符与结构事实。
- 不保证：不比对未列出的维度；不解释类型串的语义等价；不承诺快照是事务性时间点图像（由读取器
  决定）；已应用迁移 id 与快照的相互一致性归读取器；不做分类与回填决策。
- 调用方责任：提供同构的期望模型（schema 命名与读取器产出的标识符对齐）；自行决定差异的处置。

## 后续切片归属

PostgreSQL 证据读取器已由 `ServiceMantle.Database.PostgreSql` 的
`ServiceMantle.Database.PostgreSql.Migration` 命名空间交付：`PostgreSqlSchemaEvidenceReader`
在调用方连接上只读地读取已应用迁移 id 与完整 `SchemaSnapshot`（`pg_catalog`，含
`attidentity` 与 `pg_attrdef` 存在性证据位），表名清单经数组参数传入明细查询；「目标不存在」
仅由 SQLState `3D000` 判定，认证/网络/超时/权限失败一律返回 `ReadFailed`；历史表经 applied id
维度报告，不进快照。EF 期望推导与基线写入原语（`ServiceMantle.Persistence.Relational`）仍是
独立后续任务。端到端流程已随读取器交付齐全，`MIGRATION_ORCHESTRATION.md` 的
「证据构件用法（遗留库接管）」一节给出读取、推导、比对与基线写入的完整用法与调用方责任。

## 如何被覆盖

- `SchemaEvidenceComparerTests`：每个差异种类逐一断言（含载荷与证据字段）、多差异同时全部输出与
  确定顺序、维度外变化零差异、渲染不含 SQL/连接值、空参数拒绝。
- `SchemaEvidenceReadResultTests` / `SchemaEvidenceModelTests`：两个失败事实的区分、消息的精确内容与
  负向断言、模型不可变性与校验规则。
- `PostgreSqlSchemaEvidenceReaderContractTests` / `PostgreSqlSchemaEvidenceReaderTests`
  （`ServiceMantle.Database.PostgreSql.Tests`，Testcontainers）：快照全部维度的真实 PostgreSQL
  读取断言（含两种 identity kind、`HasStoredDefault`、复合主键序、五种外键删除规则、非约束索引
  与表达式索引排除）、3D000 与认证/网络失败的分别两态返回及消息负向断言、入口与飞行中取消、
  无历史表与空库读取。
- `eng/tests/consumers/provider-neutral`：在全部框架 namespace 同处作用域的条件下点名并调用全部新公开类型。
- `eng/tests/consumers/database-postgresql`：点名 PostgreSQL provider 包全部公开类型（含 #606 的
  probe failure classifier 与本读取器），接入 CI 的消费验证步骤。
