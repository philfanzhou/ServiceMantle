# 参考服务设置项查询验收

`tests/ServiceMantle.ReferenceService.Tests/ReferenceSettingQueryTests.cs` 验收样例的两个只读设置
查询 endpoint（`GET /management/v1/settings/definitions` 与 `GET /management/v1/settings`）的真实
接线：真实 PostgreSQL（Testcontainers）、真实 `ReferenceApplication` 组合路径、真实管理登录。契约
本体见 [`docs/contracts/management-setting-queries.md`](../contracts/management-setting-queries.md)。

## 运行

```bash
dotnet build tests/ServiceMantle.ReferenceService.Tests -c Release --no-restore
RUN_SERVICEMANTLE_POSTGRES_TESTS=true \
  artifacts/bin/ServiceMantle.ReferenceService.Tests/release/ServiceMantle.ReferenceService.Tests
```

需要本机 Docker；镜像可用 `SERVICEMANTLE_POSTGRES_IMAGE` 覆盖（默认 `postgres:15-alpine`）。CI 的
`Build, test, and pack` 以 `eng/packages.json` 打开同一套件。不带环境变量时整个类按既有
`RealDatabaseTest` 约定跳过。

## 矩阵

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
因为样例本身没有写路径；这同时是把写入面留给更新任务（#519）的边界证明。

不保证与既有契约一致的部分以
[`docs/contracts/management-setting-queries.md`](../contracts/management-setting-queries.md) 为准
（第三方请求日志、进程内存、响应开始写出之后等已声明的非保证）。跨实例一致性（#170）与 root key
轮换流程不在本矩阵内。
