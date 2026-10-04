# PostgreSQL 显式扩展证据

`PostgreSqlSchemaEvidenceReadOptions` 在 provider 的 Migration 子空间提供不可变开关与精确表对范围。
旧入口等价默认选项：不输出名称、排除表达式索引；过滤不改变历史表的读取。
null 范围取所有原有候选表；空范围或无匹配输出空快照。筛选在详情查询和模型构造前完成，
详情参数是成对数组，不做 schema/table 的笛卡尔积。

扩展模式读取真实 PK/FK/index 名。PK 的 INCLUDE 不混入主键列；索引键 ordinal 不超过
indnkeyatts 时计为键，表达式键只计数量，超过时输出 IncludedColumns。相同形状不同名保留。
核心校验和显式严格 Compare 仍以 [核心证据契约](schema-evidence-models.md) 为准。

每一步只 SELECT；调用方连接和 transaction 不关闭/提交/回滚。历史表的 42P01 只表示空迁移清单，
3D000 只表示目标不存在，其他失败归 ReadFailed。返回诊断不携带查询、驱动或连接内容；
不合法数据库标识符使用固定 database 兜底，不破坏错误分类。
正常、异常、资源释放和最终出口均观察调用方取消，安全 OCE 携原 token 且无 inner。
不承诺 concurrent DDL 的同一时间点，不输出/解释表达式正文，不作接管分类。

端到端：选择表对→读取历史→仅已选表详情→名称/键数/INCLUDE→调用方严格比较；
失败在资源释放后分类，不输出局部成功。调用方应对齐 expected schema，需要快照隔离时自行提供时序。
