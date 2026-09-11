# 参考服务 SQLite 部署验收

`ReferenceSqliteDeploymentEndToEndTests` 从进程外部验收参考示例显式的单实例 SQLite 部署。
它是本仓库中唯一以独立操作系统进程方式启动示例真实入口点的覆盖，因此也是唯一能够谈论进程
退出码、真实 Kestrel 绑定，以及一次真实启动和停止所留下文件的覆盖。

## 如何运行

这些测试位于既有的 `tests/ServiceMantle.ReferenceService.Tests` 项目中，并随其中所有其他
测试一起运行。它们不需要容器、不需要环境变量、除回环外不需要网络，并且从不被跳过。

```bash
dotnet restore ServiceMantle.slnx
dotnet build ServiceMantle.slnx -c Release --no-restore
dotnet test --solution ServiceMantle.slnx -c Release --no-build --no-restore
```

单独运行本验收：

```bash
dotnet test --project tests/ServiceMantle.ReferenceService.Tests -c Release \
  --filter-class "ServiceMantle.ReferenceService.Tests.ReferenceSqliteDeploymentEndToEndTests"
```

测试不会重新构建或重新发布示例。`ReferenceServiceBuildOutput` 将示例的构建输出解析为测试
程序集自身输出目录在同一配置下的同级目录，当该输出缺失时，启动会以显式消息失败。请先构建
解决方案。

每个用例都以 `--urls=http://127.0.0.1:0` 启动示例自己的可执行文件，因此端口由操作系统选择，
Kestrel 会在宿主机自己的控制台输出中报告它绑定的地址。`ASPNETCORE_ENVIRONMENT` 和
`DOTNET_ENVIRONMENT` 被固定为 `Production`，因此运行测试的机器的环境永远不会决定部署路径。

## 矩阵

| 用例 | 显式输入 | 证据 |
| --- | --- | --- |
| 默认关闭的门 | 仅数据库路径 | `GET /` 返回 200；目录中不出现任何文件，连空文件也没有 |
| 首次授权启动 | `Enabled`、`SingleInstance`、`PrepareIfMissing=true`、目标缺失 | 工作区 migration 在 Kestrel 报告其地址之前被应用；历史中恰好只有一条已知的 migration；工作区表为空 |
| 在同一目标上重启 | 同上，`PrepareIfMissing=false` | 再次提供服务；历史不增长；两次运行之间写入的一行保持不变；文件逐字节相同 |
| 未授权的部署模式 | 未声明、空、`Unspecified`、`MultiInstance`、`3`、`not-a-mode`，各自分别在目标缺失和已存在两种情况下测试 | 非零退出；宿主从不监听；失败消息指出该设置本身而不是它的值；不创建数据库或 sidecar，已存在的文件逐字节相同 |
| 目标缺失且未授权准备 | `PrepareIfMissing=false`、目标缺失 | 非零退出；有限结果为 `TargetMissing`；不创建任何东西 |
| 已存在但不可用的目标 | 一个不是数据库的文件 | 非零退出；一个有限结果；文件逐字节相同——既不被采纳、修复，也不被替换 |
| 未知的 migration 历史 | 一条本次构建不认识的记录，由 fixture 写入其自己已停止的数据库 | 非零退出；有限结果为 `MigrationFailed`；两条历史记录都仍在，且文件逐字节相同 |
| 停止运行中的宿主 | 授权启动 | 进程退出，随后目标文件可以被独占获取并删除，且不留下 sidecar |
| 输出安全 | 授权启动 | 捕获的控制台输出和 HTTP body 都不携带目标路径、`Data Source` 或连接字符串中的 mode |
| 构建输出 | - | 示例声明的程序集都存在，且路径上没有其他数据库 provider |

## 所有权与清理

每个用例都在系统临时路径下拥有自己的临时目录，并解析掉任何符号链接，因为 SQLite 文件目标
契约拒绝链接路径。该目录同时也是子进程的工作目录，因此示例基于相对默认值创建的文件会落在
测试检查的位置。helper 在 dispose 时回收进程，包括进程树，目录也随之被删除。

`ReferenceServiceBudgets` 中的超时是本验收的清理预算：它们限定测试在报告失败并收回进程
之前等待多久。它们不是关于参考服务在部署中允许花多长时间启动或停止的陈述。

## 本验收不声明什么

- **没有跨进程互斥。** 两个都声明 `SingleInstance` 的进程是示例契约无法检测的部署错误，
  这里的内容同样不会检测到。
- **完成的启动是一个已迁移的 schema，而不是一次完成的安装。** 不演练也不声明任何服务安装、
  初始配置、安装状态或就绪；`GET /` 仍然是骨架路由。
- **没有跨步骤原子性。** 文件发布和 EF migration 是分开的步骤。已发布的文件或已提交的
  migration 不会被之后的取消撤销，也不覆盖任意终止点、外部文件替换、链接或网络文件系统，
  以及断电。
- **有界的输出声明。** 对路径或连接字符串不存在的断言只针对本测试所捕获的内容：示例自己的
  启动分类、其异常消息和它的 HTTP 输出。对任意第三方或框架日志、进程内存、操作系统诊断或
  原始数据库内容不作任何声明。示例的路径不接受密码或管理员连接输入，因此不会为测试这类
  输入而发明某种秘密协议。
- **在平台允许的地方以信号方式优雅关闭。** 在 POSIX 平台上，宿主被以 `SIGTERM` 请求停止，
  并断言退出码为零。Windows 对以这种方式启动的子进程没有等价的请求手段，因此在那里进程被
  强制回收，只断言其终止和目标文件的释放。
