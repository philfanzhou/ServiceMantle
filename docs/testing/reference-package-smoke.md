# 参考服务发行产物冒烟

跟踪 [#113](https://github.com/philfanzhou/ServiceMantle/issues/113)。本文描述从实际打包产物（而非项目
引用）还原、构建并运行参考服务的门控验收：验证核心包集合、依赖隔离与发行可消费性。

## 运行方式

整个测试类由 `RUN_SERVICEMANTLE_PACKAGING_TESTS=true` 门控，沿用真实数据库测试的
「要求而不可用即失败」策略：变量设置后任何步骤失败都判测试失败；变量未设置时全部跳过。
该变量已注册在 `eng/packages.json` 的参考服务测试条目中，CI 的注册测试步骤会实际运行整个
冒烟（该条目启用 `--fail-skips`，跳过即失败）。

```bash
dotnet restore ServiceMantle.slnx
dotnet build ServiceMantle.slnx -c Release --no-restore --no-incremental
RUN_SERVICEMANTLE_PACKAGING_TESTS=true dotnet test \
  tests/ServiceMantle.ReferenceService.Tests -c Release --no-build --no-restore \
  --filter "FullyQualifiedName~ReferencePackageSmokeTests"
```

前置条件：解决方案已完成 Release 构建（夹具直接复用仓库 Release 构建输出重新打包，不重新编译）；
PostgreSQL 冒烟需要 Docker（Testcontainers，与 `RUN_SERVICEMANTLE_POSTGRES_TESTS` 同一策略，可用
`SERVICEMANTLE_POSTGRES_IMAGE` 固定镜像）。

## 夹具步骤

1. 以仓库 Release 构建输出运行 ReleaseTool `pack`（版本 `0.0.0-local.<时间戳>`，每次运行唯一，
   避免命中全局包缓存），产出本地 feed：`artifacts/package-smoke-feed`（gitignored）。
2. 把 `samples/ServiceMantle.ReferenceService` 源码复制到仓库外临时目录，重写 csproj：
   `ProjectReference` → `PackageReference`（本地 feed 版本），显式写入仓库 `Directory.Build.props`
   的 TFM/Nullable 等属性与 `Directory.Packages.props` 钉住的 EF 包版本。
3. 临时目录写入 `NuGet.config`（`<clear/>` + 仅本地 feed）：ServiceMantle 包只能来自本地 feed，
   外部包来自全局包缓存，不配置任何远程源。
4. `dotnet restore` + `dotnet build -c Release`，解析产物目录与 `project.assets.json`。

## 验收内容与结果矩阵

| 检查 | 结果 |
| --- | --- |
| 直接依赖集合 | 恰好等于样例声明集合：核心/P0（ServiceMantle、AspNetCore、Database.Sqlite、Database.PostgreSql、Persistence.EntityFrameworkCore）+ P1 显式可选（Consul、OpenTelemetry、Serilog）+ 两个 EF provider；ServiceMantle 直接引用版本全部等于打包版本 |
| 传递闭包 | 不出现任何未声明的 ServiceMantle 包（MySql/MariaDb/Oracle/SqlServer 等不可经依赖边混入） |
| SQLite 路径真实进程 | prepare-if-missing 首启迁移、根端点 200（skeleton）、优雅停止；全部监听为回环地址；输出无 Consul/OpenTelemetry/OTLP 活动、无 Npgsql 初始化；输出与响应不含数据库路径与连接串 |
| PostgreSQL 路径真实进程 | 首启对不存在的目标库执行 prepare→migrate→initialize，签发 code（stdout 横幅恰一次）→ `POST /management/v1/setup` 204 → 登录 204（Set-Cookie）→ `/health/ready` 200；无 P1 活动；code 明文只出现在横幅；根密钥、操作员凭据、数据库密码与连接串不进入输出与响应 |

## 已知边界与非保证

- 冒烟验证「能从产物还原、构建、运行安装主路径」；不重复数据库压力套件，不验证真实 Consul agent
  与 OTLP 导出（分别属于 #176 与遥测任务的交付物）。
- 「未安装 P1 包」的构建变体不存在：样例源无条件编译 P1 接线（编译期依赖），本任务验证的是
  「禁用」路径（运行时开关关闭）。
- CI 的注册测试步骤运行该冒烟（约增加 1–2 分钟）；发行管线的 pack/verify 与六个消费项目由
  `ci.yml` 的 build-test-pack 与 `eng/tests/package-consumption.sh` 独立覆盖。
- 本地 feed 目录 `artifacts/package-smoke-feed` 保留供检查，随下次运行重建。

## 测试

`tests/ServiceMantle.ReferenceService.Tests/ReferencePackageSmokeTests.cs`：
`The_packaged_restore_carries_exactly_the_declared_dependency_set`（闭包与版本断言，解析
`project.assets.json`）、`The_sqlite_deployment_path_runs_from_the_packed_artifacts`（SQLite 路径）、
`The_postgresql_installation_path_completes_from_the_packed_artifacts`（安装主路径）。
