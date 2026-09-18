# 参考服务设置项查询与更新验收

本文件是查询与更新两套验收的共用说明：运行命令、容器与环境变量完全一致，矩阵各自成节。

- `tests/ServiceMantle.ReferenceService.Tests/ReferenceSettingQueryTests.cs` 验收样例的两个只读
  设置查询 endpoint（`GET /management/v1/settings/definitions` 与 `GET /management/v1/settings`）
  的真实接线。契约本体见
  [`docs/contracts/management-setting-queries.md`](../contracts/management-setting-queries.md)。
- `tests/ServiceMantle.ReferenceService.Tests/ReferenceSettingUpdateTests.cs` 验收样例的事务批量
  更新 endpoint（`POST /management/v1/settings`）及其消费方提交边界
  （`ReferenceSettingUpdateExecutor`）。契约本体见
  [`docs/contracts/management-setting-updates.md`](../contracts/management-setting-updates.md)。

两套验收同样使用真实 PostgreSQL（Testcontainers）、真实 `ReferenceApplication` 组合路径与真实管
理登录。

## 运行

```bash
dotnet build tests/ServiceMantle.ReferenceService.Tests -c Release --no-restore
RUN_SERVICEMANTLE_POSTGRES_TESTS=true \
  artifacts/bin/ServiceMantle.ReferenceService.Tests/release/ServiceMantle.ReferenceService.Tests
```

需要本机 Docker；镜像可用 `SERVICEMANTLE_POSTGRES_IMAGE` 覆盖（默认 `postgres:15-alpine`）。CI 的
`Build, test, and pack` 以 `eng/packages.json` 打开同一套件。不带环境变量时整个类按既有
`RealDatabaseTest` 约定跳过。

## 查询矩阵（ReferenceSettingQueryTests）

| 用例 | 断言要点 |
| --- | --- |
| gate 关闭 | 容器中无 `IServiceSettingStore`、`IServiceSettingRootKeySource`、`ServiceSettingQueryService`；路由集合仍为 `["/"]` |
| registry 唯一 | gate 开启时 `GetServices<ServiceSettingDefinitionRegistry>()` 恰好一个注册（样例 registry 在前、快照 `TryAdd` 在后的顺序不变量） |
| 定义与版本 0 | 三个键按 ordinal 排序、每项恰好六个固定字段、敏感键 `isSensitive:true` 且无默认值投影；版本 0 的 `source` 为 `default`/`missing`；`group=workspace.item_limit` 只返回该项 |
| 授权矩阵 | 未登录 401、只有 `management.read` 的操作员 403 `management.session.forbidden`；两者都断言 store 读取次数为 0（测试替身包装真实 store） |
| 敏感值 | 播种后数据库 `values_json` 含 `sm:v1:` 且不含明文；投影 `hasValue:true`、`source:"persisted"`、`value:null`；响应与捕获日志不含明文、root key |
| 错误 root key | 库中密文以另一把 key 加密时 `GET /settings` 为固定 `503 {"errorCode":"management.settings.unavailable"}`，定义查询仍 200 |
| 换部署 key 重启 | 以另一把 root key 重启后登录先以 `503 management.session.unavailable` 失败（key ring 不可解，复用根密钥的既有推论） |
| 存储损坏 | `values_json` 改为非法 JSON 时为同一固定 503 响应体 |
| 取消 | store 读取中调用方取消：客户端得到取消、store 观察到调用方 token、日志无未处理异常、随后请求 200 |

播种使用公开 API（`SensitiveValueProtector.Protect` + `EfCoreServiceSettingStore.UpdateAsync`），
因为样例在更新面交付前没有写路径；更新面交付后该播种方式保留为查询矩阵自身的边界证明。

## 更新矩阵（ReferenceSettingUpdateTests）

失败与提交时失败用 PostgreSQL 触发器构造：`BEFORE INSERT` 抛错模拟审计写入失败、
`DEFERRABLE INITIALLY DEFERRED` 约束触发器抛错模拟提交时失败、`pg_sleep` 触发器拉开取消窗口。

| 用例 | 断言要点 |
| --- | --- |
| A1 成功 | 两个 key、`expectedVersion` 0 → `200 {"version":1}`；`service_settings` 版本 1、`updated_by` 为登录操作员；每个变更 key 一行审计，`configuration.changed\|操作员\|interactive_admin\|configuration\|reference-service\|{"key":…}`，`metadata_json` 不含值 |
| A2 拒绝 | 版本冲突 409 `management.request.conflict`、未注册 key 400、约束违规（`item_limit=5000`）400 且响应不回显违规值；两表计数均为 0 |
| A3 授权 | 未登录 401、只读操作员 403；两表计数均为 0 |
| A4 审计插入失败 | 触发器抛错 → 固定 `503 {"errorCode":"management.settings.update_unavailable"}`；设置行与审计行都不存在（证明同事务） |
| A5 提交时失败 | 延迟约束触发器抛错 → 同一固定 503；两表计数为 0 |
| A6 提交前取消 | 审计触发器 `pg_sleep(3)`，800ms 时取消：客户端观察到取消，服务端结束后两表计数为 0 |
| A7 提交中取消 | 延迟触发器 `pg_sleep(3)`，1s 时取消：客户端观察到取消；版本 1 与审计 1 行保留（提交走 `CancellationToken.None`） |
| A8 并发 | 相同 `expectedVersion` 0 的两个请求恰一个 200、一个 409；版本 1、审计 1 行、库存的是赢家的值 |
| A9 敏感写入 | 写 `workspace.integration_token` → 库中只有 `sm:v1:` 密文；审计与捕获日志不含明文与密文；随后 `GET /settings` 投影 `hasValue:true`、`value:null` |

不保证与既有契约一致的部分以各自的契约文档为准（第三方请求日志、进程内存、响应开始写出之后、
跨请求幂等、自动冲突重试、未知提交结果补偿等已声明的非保证）。跨实例一致性（#170）、root key
轮换流程、快照发布与热更新、SQLite 写入路径不在矩阵内。

## 跨实例一致性（ReferenceSettingCrossInstanceTests，#170）

两个真实 OS 进程共享同一 PostgreSQL 目标，各自独立登录；断言只在测试显式的观察点进行。

| 场景 | 实例 A | 实例 B | 数据库唯一事实 |
| --- | --- | --- | --- |
| A 更新成功（version 0→1） | 200 | GET 观察同一 version 与逐字节相同的完整快照 | 版本 1、每个变更 key 一行审计 |
| 同 expectedVersion 并发更新（审计 `pg_sleep` 触发器拉开窗口） | 200 或 409（恰一 200） | 互补 | 版本递增恰一次；审计恰一胜者行；库存恰一赢家的值；409 方重读与胜者逐字节一致 |
| A 审计触发器失败 | 503 | GET 观察仍为 version 0 快照（无部分状态） | 设置行仍不存在（version 0 无行）、审计 0；移除触发器后 B 重试成功、版本 1 |
| A 写敏感项 | 200 | 投影 `hasValue:true`、`isSensitive:true`、`value:null` | `values_json` 含 `sm:v1:`、无明文 |

任何路径：两实例响应体、捕获输出与审计行不含敏感明文、`sm:v1:`、根密钥。版本 0 的
「未变」指 `service_settings` 仍无行（首次成功更新才建行）。

非保证与调用方责任：跨实例传播延迟上界、缓存失效时刻与最终一致收敛时间不保证（只在显式观察点
比较）；冲突的自动重试与未知提交结果的补偿不在范围内（409 方以重读决定下一步）。调用方以
`GET /management/v1/settings` 的版本号做乐观并发基准。
