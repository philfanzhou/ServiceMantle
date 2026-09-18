# 参考服务 PostgreSQL 一次性首次安装（Setup）

跟踪 [#175](https://github.com/philfanzhou/ServiceMantle/issues/175)。本文描述参考示例在
PostgreSQL 启动 gate 之上的一组安装接线：启动签发、控制台交付、轮换命令，与
`POST /management/v1/setup` 的一次性事务化完成。它组合既有组件——staging contributor
（[reference-postgresql-setup-staging.md](reference-postgresql-setup-staging.md)）、
`EfCoreServiceSetupCodeStore`、`ServiceSetupOrchestrator`、`EfCoreManagementAuditWriter`——
不引入新的设置键、新表或新迁移。

## 开关与成员

全部接线只在 PostgreSQL gate 打开时注册。gate 关闭时没有 Setup 路由、没有签发器、没有
`ReferenceSetupCodeOutput`，控制台没有任何横幅，SQLite 与骨架路径逐字不变。

| 成员 | 行为 |
| --- | --- |
| `ReferenceSetupCodeIssuer`（hosted service，注册在 gate 之后） | 仅当 gate 结果为 `Ready` 且阶段为 `PendingSetup` 时，用短生命周期 context 调一次 `CreateAsync`（默认 30 分钟有效期）。其他阶段不访问 store。 |
| `ReferenceSetupCodeOutput`（可替换单例） | 控制台接缝：默认包裹 `Console.Out`/`Console.Error`，测试注入记录器。明文只经过它，从不经过 `ILogger`。 |
| `ReferencePostgreSqlSetupExecutor`（internal static） | `MapServiceMantleSetup` 的执行器：六步序列 + 单事务提交。 |
| `ReferenceSetupCodeRotation`（`--rotate-setup-code`） | `Program.cs` 在构建 Web 宿主之前识别的运维命令。 |

## 签发与交付

首次启动（gate 初始化为 `PendingSetup`）打印一次横幅：

```
one-time setup code:
<32 字符明文>
expires at <UTC ISO-8601>
for a new code later run with --rotate-setup-code
```

数据库只存摘要（`setup_code_digest`），不等于明文。`PendingSetup` 期间重启打印固定提示
（`a setup code is already issued; …`），不打印明文、不轮换，首个 code 仍可完成安装。两实例
同时首签由 `CreateAsync` 的乐观并发裁决：恰一份材料，一个实例打印明文，另一个打印固定提示。
`installation.completed` 不打印。其他拒绝或 store 异常在 stderr 打印固定提示，宿主以
`PendingSetup` 继续运行（运维用轮换恢复）；调用方取消原样传播并取消启动。

## 轮换命令

`dotnet ServiceMantle.ReferenceService.dll --rotate-setup-code <gate 参数>`：读取与正常启动相同
的 gate 输入，直接以目标连接串建 context（不准备、不迁移），`RotateAsync`；若材料从未签发
（`setup_code.not_created`）改用 `CreateAsync`。退出码固定：成功打印同一横幅并退出 `0`；
`installation.completed`、`installation.not_found`、其他拒绝或异常 → 一行固定 stderr（不含
provider 文本或明文）并退出 `1`；gate 未开启 → 固定 stderr 并退出 `2`。

## 完成执行器与结果表

`POST /management/v1/setup` 的线上格式、判定顺序与响应映射由共享契约
[management-setup.md](../contracts/management-setup.md) 唯一定义。示例执行器只拥有事务：

1. fresh async scope + scoped `ReferencePostgreSqlDbContext`，开事务；
2. 只读 `ValidateAsync`；
3. `ServiceSetupOrchestrator([ReferencePostgreSqlSetupContributor], staging scope)`；
4. 暂存一条审计：action `installation.completed`，operator `System()`（source `system`，无
   operator id 与显示名），target `service`/`reference-service`，outcome `Success`，无 client
   IP、无 security description、无 metadata；
5. `StageConsumeAsync`；
6. 一次 `SaveChangesAsync`，检查一次调用方 token，`CommitAsync(CancellationToken.None)`（提交
   开始后不被调用方取消打断成未知结果），然后才返回 `Committed`。

| 来源 | 结果 |
| --- | --- |
| `setup_code.invalid`、`setup_code.expired` | 回滚 → `CredentialInvalid` → 401 |
| `installation.completed`、`installation.concurrency_conflict` | 回滚 → `Conflict` → 409 |
| `SaveChangesAsync` 的 `DbUpdateConcurrencyException`（另一请求先提交安装行） | 回滚 → `Conflict` → 409 |
| 其他 store 拒绝、orchestrator 失败 | 回滚 → `Unavailable` → 503 |
| 其他任何异常（含内部取消与调用方取消） | 回滚后原样抛出，由共享处理器映射为 503 或调用方取消 |

初始配置不写 `service_settings` 行：三个定义中两个有默认值，敏感项契约禁止默认值，版本 0 的
查询已按默认值投影。完成后不重启：健康快照源逐请求重读安装行，`/health/ready` 在下一次请求
即可 200，管理登录的 Completed 阶段准入随之放开。

## 非保证与调用方责任

- 保证：安装状态 `Completed`、code 材料清除、版本递增、一条工作区与一条审计在同一事务内一起
  提交或都不存在；`Committed`（204）只在提交完成后返回；Setup Code 明文只出现在签发器/轮换
  命令的标准输出与调用方请求体中；签发器从不轮换，轮换只由显式运维命令触发。
- 不保证：标准输出被容器日志或终端采集后的保密（运维负责）；code 有效期内的可用性（过期后需
  轮换）；提交时连接中断的真实结果（返回 503，数据库可能已提交，以 `GET /management/v1/setup`
  为准）；双实例下哪个实例打印明文（[#165](https://github.com/philfanzhou/ServiceMantle/issues/165)
  验收竞争）。
- 调用方责任：保护启动输出；过期或丢失时运行 `--rotate-setup-code`；收到 503 或取消后以
  `GET` 为准再决定是否重试。

## 测试

`tests/ServiceMantle.ReferenceService.Tests/ReferenceSetupTests.cs`（真实 PostgreSQL，
`RUN_SERVICEMANTLE_POSTGRES_TESTS=true`）：首次签发与摘要（C1）、重启不轮换（C2）、轮换命令
进程级退出码（C3）、成功矩阵与审计形状（C4）、错误/过期/轮换后旧 code 的 401（C5）、审计/
工作区/提交失败触发器（C6）、提交前后取消（C7a/C7b）、双并发单赢家（C8）、敏感 canary
（C9）、gate 关闭零 Setup 面（C10）。
