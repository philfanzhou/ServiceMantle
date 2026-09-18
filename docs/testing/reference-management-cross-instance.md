# 参考服务跨实例管理 Cookie 与会话隔离

跟踪 [#164](https://github.com/philfanzhou/ServiceMantle/issues/164)。本文记录两个参考服务实例共享
Data Protection Keys 后的管理 Cookie 与会话行为。共享能力（EF Core key ring 仓储、根密钥封装、
service 隔离、轮换与撤销）的规则以
[persistence-key-ring 文档](../contracts/persistence-key-ring.md)与仓储级测试为唯一来源，本文只记录
进程级 E2E 可观察的结果矩阵与非保证。

## 部署事实（实测）

- **key ring 行是宿主启动产物**：首个进程启动完成（listening）后、任何登录之前，
  `service_data_protection_keys` 已有一行；后启动的进程读取并采纳该行，不新增。
- **运行中的进程持有进程内 key ring 缓存**：数据库中该行被篡改后，已运行实例的登录与会话读取
  继续成功，直到进程需要重新读取存储（重启或新进程）。

## 结果矩阵

| 场景 | 实例 A（根密钥 K1） | 实例 B / 新进程 | 数据库事实 |
| --- | --- | --- | --- |
| 同 K1 双实例（既有回归） | 会话有效 | 同 cookie 会话有效；并发使用、签发进程 SIGTERM 重启后仍接受 | 1 行 key ring |
| B 持同长度合法 K2 | 会话有效 | 登录固定 503（`management.session.unavailable`）；A 的 cookie 不成立已认证会话 | key ring 行不变 |
| `encrypted_xml` 被篡改后**新启动**的进程 | ——（运行中实例按缓存继续，非保证） | 登录固定 503；既有 cookie 不成立已认证会话 | 行保持损坏值，不修复不放大 |
| 顺序启动两进程后并发首次登录 | 204 | 204；每张 cookie 在两实例都成立已认证会话 | 恰 1 行（首进程启动创建） |

任何路径：两实例全部响应体与响应头不含 credential、根密钥、连接秘密；cookie 值只出现在其自身的
`Set-Cookie` 传输头中，不进入响应体、其他响应头或进程控制台输出。

## 非保证与调用方责任

- 不保证超过两个实例、跨可用区、时钟漂移或负载均衡粘性会话的行为（正文既有边界）。
- **热进程缓存窗口**：篡改或轮换存储中的 key ring 行后，已运行实例在重新读取前的继续服务不是
  保证，也不是缺陷面；需要 fail-closed 生效时重启实例。
- **双进程同时冷启动同一空库**可能各自创建一行 key（Data Protection 语义上两把钥匙都有效，
  cookie 仍跨实例互通）；冷启动竞争的验收属于
  [#165](https://github.com/philfanzhou/ServiceMantle/issues/165) 的双进程冷启动场景。
- key ring 轮换/撤销的 E2E 不适用：样例没有管理面轮换入口（正文既有边界，仓储级已覆盖）。
- service_id 隔离由仓储级证据覆盖（样例 service_id 是编译期常量，进程级无法配置不同值）。
- 调用方责任：多实例部署使用同一根密钥与同一数据库；保护根密钥；篡改后按需重启实例。

## 测试

`tests/ServiceMantle.ReferenceService.Tests/ReferenceManagementCrossInstanceTests.cs`（两个真实 OS
进程 + Testcontainers PostgreSQL，`RUN_SERVICEMANTLE_POSTGRES_TESTS=true`）：
`Two_instances_share_one_cookie_and_a_restart_keeps_accepting_it`（既有回归，不改断言）、
`A_second_root_key_fails_login_closed_and_never_accepts_the_first_cookies`（K2）、
`A_corrupted_key_ring_row_closes_every_fresh_reader_without_leaking_material`（篡改 + 新进程
fail-closed + 无泄漏）、`Two_processes_over_one_ring_race_their_first_logins_and_share_the_result`
（单行共享 + 并发登录 + cookie 双向成立 + HTTP 面负向）。
