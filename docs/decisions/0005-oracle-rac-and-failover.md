# ADR 0005：关闭 Oracle RAC 与持锁会话故障转移支持

- 日期：2026-09-03；状态：结论固定，待 PR 合并
- 决策任务：[#201](https://github.com/philfanzhou/ServiceMantle/issues/201)
- 基线：`9e574c90b57c279cc05195579ba9a9cd72ac2256`
- 本决策补充 [ADR 0001](0001-oracle-provider-contract.md)，不改变单实例 PDB 支持面。

## 结论

ServiceMantle 不支持 Oracle RAC、多实例服务上的迁移，也不支持把已取得的迁移租约随
TAF、AC/TAC、服务重定位或其他透明会话恢复迁移到新会话。没有隐藏配置开关或降级成无锁路径。
这是完成 #201 的“明确关闭支持”分支，不是等待环境的暂时批准；本 PR 不实现 provider。

## Oracle 能力与本库证据的区别

[Oracle DBMS_LOCK 文档](https://docs.oracle.com/en/database/oracle/oracle-database/26/arpls/DBMS_LOCK.html)
说明命名用户锁可由同一或另一实例识别，且属于 Oracle 的锁管理机制。由此可以考虑同一数据库
RAC 实例间的互斥；它不证明任意独立数据库、PDB、Data Guard 副本共享一个锁空间，也不证明
专用会话丢失后本库的 executor 已停止。实例切换不会赋予新会话旧会话的锁所有权。

[ODP.NET 连接文档](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/featConnecting.html)
区分连接池的 HA Events/负载均衡与连接恢复。ACR 自 23.8 起用于请求边界的新会话分发，
不是把旧会话/事务状态交还同一请求的保证。FAN/FCF 的通知和池清理也不构成 DBMS_LOCK lease。
是否支持某种回放必须对固定驱动、服务配置和真实数据库验证，不能由功能名称推断。

本库固定驱动为 `Oracle.ManagedDataAccess.Core` 23.26.300。已完整阅读锁 provider/operations、
目标/拓扑检查、核心 lease-loss 编排、单元与真实失锁测试、README/ADR 0001、包登记与 CI。
现有实现强制 unpooled、Enlist=false；打开后要求 `IS_CLUSTER_DATABASE = FALSE` 再分配/请求锁。
监控仅在同一连接执行 SELECT 1 FROM DUAL；它证明探针可执行，不能证明透明替换后的会话仍持有
原锁。现有永久 LeaseLost 信号与编排取消足以表达“已检测失锁”，不需要新公共 SPI 来关闭支持。

## 路径与既有错误码

| 场景 | 支持结论与处理 |
| --- | --- |
| 单实例 PDB，原物理会话持续存活 | 保持已有支持；真实 lease 覆盖检查/执行/最终检查 |
| 打开时已证明 RAC（包括只有一个活动节点的 RAC） | 拒绝，在分配/请求锁前停止 |
| 无法证明非 RAC / 无权限调用拓扑探针 | 拒绝，不以未读到 TRUE 推断单实例 |
| 建立初始连接前的地址候选选择 | 不构成持锁恢复；最终会话仍需通过完整拓扑检查，RAC 拒绝 |
| 节点终止、网络断连导致获取失败 | LockFailed；获取整体 deadline 到期为 LockTimeout |
| 单实例已有 lease 的连接故障被监控观察到 | 永久 LeaseLost，编排返回 LockFailed，后续阶段不再开始 |
| RAC 服务重定位/实例切换（计划或非计划） | 不支持；不转移 lease，不重放迁移；新调用若连接 RAC 仍拒绝 |
| FAN/FCF 清理连接或建议路由 | 不作为租约证据；专用连接损失按已检测失锁终止 |
| TAF session failover、AC/TAC replay、ACR 重新分发或手工重连 | 不支持继承旧 lease；若会话替换未被探针发现，不承诺当前实现能够安全继续或准确检测 |
| 消费方在失败后重试 | 必须结束旧编排，从全新调用重新获取权限并在锁内检查；没有自动重试/自动恢复成功 |
| 调用方取消与失锁/超时同时发生 | 调用方 OCE 优先，保留原 token；已开始的消费方副作用不保证回滚 |

上述 LockFailed/LockTimeout 为既有 `migration.lock_failed` / `migration.lock_timeout`。
没有 provider 或缺少直接 DBMS_LOCK 权限为既有 `migration.lock_not_supported`。
当前 main 对不支持输入/拓扑实际返回 LockFailed，ADR 0001 则要求 LockNotSupported；
此既有漂移已独立记录为 [#268](https://github.com/philfanzhou/ServiceMantle/issues/268)。本 PR 不掩盖
差异、不修改既有实现；调用方目前必须把两者都作为不能迁移，不能依赖该漂移放行。

“关闭支持”不声称能够解析所有 TNS/服务端回放配置，也不声称 pooling=false 会禁用所有恢复功能。
消费方必须确保持锁连接不被透明替换；无法约束这种部署就不能使用本库 Oracle migration。
当前五秒监控界限只适用于原有运行进程与探针可超时的直接会话失败，不扩展到 RAC/重放/进程暂停。

## 真实验证与重新开放的门槛

现有 CI 的 Oracle Free 是单实例：它验证单实例竞争、会话 kill 和关闭支持的代码路径，不能
验证 RAC 跨实例用户锁。当前不添加一个会跳过的 RAC job，也不把单实例成功改名为 RAC 验收。
由于选择关闭支持，本 PR 无需一个不存在的 RAC 环境才能交付决策。

只有另开实施任务并提供以下自动化证据，才可重新开放；此清单是固定验收方案，不是已执行结果：

1. 专用、已授权的真实 RAC runner；至少两个不同实例上的同一 PDB/service，探针断言
   `IS_CLUSTER_DATABASE=TRUE`、相同数据库/PDB身份与不同 INST_ID。环境、权限、第二节点缺失
   或探针异常立即非零退出；不接受 skip、零测试或回退单实例。日志只输出固定阶段/结果。
2. 由测试唯一运行标识派生服务锁名；两个实例用屏障同步。A 获取后 B 的 REQUEST 必须超时；
   不同 service_id 的 C 可获取。A 显式释放后 B 成功。观察以结果码和机器断言为准。
3. 在初始检查、迁移执行、最终检查三个屏障位置分别 kill 持锁 SID/SERIAL#/INST_ID，
   测量失锁信号、executor 停止、结果失败及第二 actor 的获取时间；不允许在旧 executor
   仍可写时把新会话当作旧 lease。若需要 fencing，独立 SPI task 先交付。
4. 分别执行节点 abort、服务 relocate、网络中断、TAF/AC/TAC 配置，以及当前固定驱动支持的
   请求边界恢复。每个场景记录会话身份变化，断言永久失锁而非恢复旧 lease；禁止人工看日志判定。
5. 将专用 runner、所需权限和固定数据库/驱动矩阵纳入必需 CI/Release gate，失败不会被重试
   或环境变量未设置掩盖。清理只触及本运行服务/对象，节点操作仅限专用测试集群。

在这些条件未交付时维持关闭，不新增支持 task 或可选的绿色 CI。没有新的公共 SPI 需求。
#268 是错误分类邻近债务，不依赖或阻塞本决策；其他 Oracle 拓扑决策 #202–#205 保持独立。

## 验收与非保证

本决策的验收为：故障转移矩阵有明确关闭结论、现有错误码和漂移说明；真 RAC 自动化准入
要求环境不可用时失败；README 与 ADR 0001 的支持链接一致。没有运行真实 RAC，也不提供
它的互斥、回放安全、零延迟失锁、执行器强制终止、分区 fencing 或已提交 DDL 回滚保证。
