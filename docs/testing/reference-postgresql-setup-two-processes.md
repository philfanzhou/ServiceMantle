# 参考服务双实例迁移与 Setup 单一成功者（进程级）

跟踪 [#165](https://github.com/philfanzhou/ServiceMantle/issues/165)。本文记录两个真实参考服务进程
共享一个 PostgreSQL 目标时，迁移、初始化与一次性 Setup 的单一成功者行为。advisory lock 迁移语义、
Setup 事务与失败映射以
[reference-postgresql-setup.md](reference-postgresql-setup.md)、
[reference-postgresql-migrations.md](reference-postgresql-migrations.md) 与共享契约为唯一来源，本文
只记录双进程可观察的结果矩阵与非保证。

## 竞争屏障

跨进程确定性屏障沿用单进程并发行（C8）的数据库原语：`CREATE SEQUENCE` 到达计数 + `BEFORE INSERT`
触发器（到达不足 2 时 `pg_sleep` 自旋等待），天然跨连接、跨进程、可重复。进程消亡场景用同一到达
标记 + `pg_advisory_lock`（由测试会话持有后释放）。真实 PostgreSQL 路径不可跳过
（`RealDatabaseTestEnvironment` 要求而不可用即失败）。

## 结果矩阵

| 场景 | 进程 A | 进程 B | 数据库唯一事实 |
| --- | --- | --- | --- |
| 同时冷启动同一空库 | 迁移/初始化由 advisory lock 串行化，两进程均监听 | 同 | 恰 1 行 PendingSetup（status=0）、1 份 code 摘要、迁移历史恰 4 条；两进程 `/health/ready` 均 503 `pendingSetup`；明文 code 恰一进程打印，另一进程打印固定提示 |
| 确定性门下双 POST 同 code | 204 或 409（恰一 204） | 互补 | 恰 1 工作区、1 条 `installation.completed` 审计、安装 version 相对完成前恰 +1；两进程 GET setup 均为 `{"status":"completed"}` |
| A 遇审计触发器失败 | 503，事务回滚 | 同 code 完成 204 | 零残留：0 工作区、0 审计、status 仍 0、摘要与 version 不变；完成后失败方 GET 观察同一完成状态，version 恰 +1 |
| A 被门阻塞时进程消亡 | 在途请求以传输失败结束，进程树被回收 | 同 code 完成 204 | 无半安装：0 工作区、0 审计、status 仍 0、version 不变；完成后恰 +1、恰 1 条完成审计 |

任何路径：响应与捕获输出不含 code 明文（签发横幅恰一次除外）、根密钥、操作员凭据与连接秘密。
安装 `version` 为乐观并发 token：其绝对值属于实现（初始化后为 2，完成后为 3），验收断言的是
「完成恰使 version 递增一次」；`GET /management/v1/setup` 只投影 `{"status":...}`，不含 version。

## 非保证与调用方责任

- 双实例冷启动中哪个进程赢得首签（打印明文）不保证；输掉首签的进程打印固定提示，同一 code 仍可
  完成安装。
- 提交期连接中断的真实结果以 `GET /management/v1/setup` 为准（503 可能掩盖已提交，为既有声明）。
- 迁移 DDL 已提交部分不因取消回滚（既有非保证）。
- 不验证配置管理、Cookie、健康或 Consul（分别由 #170、#164、#173、#176 负责）；SQLite 路径不在
  本任务范围。
- 调用方责任：同 code 完成失败后以 GET 复核再重试；保护启动输出中的明文 code。

## 测试

`tests/ServiceMantle.ReferenceService.Tests/ReferenceSetupTwoProcessTests.cs`（两个真实 OS 进程 +
Testcontainers PostgreSQL，`RUN_SERVICEMANTLE_POSTGRES_TESTS=true`）：
`Two_processes_cold_starting_one_empty_target_settle_on_one_pending_installation`（冷启动）、
`The_deterministic_gate_race_completes_exactly_one_installation`（确定性门竞争）、
`A_failed_completion_leaves_nothing_and_the_same_code_still_completes`（审计失败与恢复）、
`A_process_dying_mid_completion_leaves_no_half_installation`（进程消亡与回滚）。
