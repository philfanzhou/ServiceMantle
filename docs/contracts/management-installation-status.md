# 安装状态条目契约（#94）

状态：已实现。本文档描述 ServiceMantle 管理面的匿名安装状态条目。它细化
[management-entry-authorization.md](management-entry-authorization.md) 中的 Installation status
行，不向其添加任何内容。

## 注册与映射

`AddServiceMantleInstallationStatus` 注册进程本地的 Bootstrap 重启闩锁以及该条目读取的
Bootstrap 观察边界。它还添加共享管理条目约定，该条目消费此约定且绝不扩展它。
`MapServiceMantleInstallationStatus` 映射该条目：

```csharp
builder.Services
    .AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"))
    .AddSecurityResponseHeaders()
    .AddSensitiveHeaders()
    .AddRateLimiting()
    .AddServiceMantleManagementApiV1()
    .AddServiceMantleInstallationStatus();

var app = builder.Build();
app.UseServiceMantlePipeline();
app.MapServiceMantleInstallationStatus();
```

该条目是所配置版本化根的直接子项，与 `MapServiceMantleManagementApiV1` 返回的受保护组并列
映射，绝不位于其内部。使用默认根时完整路径为 `/management/v1/status`；自定义版本化根会移动
它。它至多被映射一次。缺少安装状态能力、管理 API v1 能力或共享管理条目能力，以及重复映射，
都会在宿主启动前失败。

`GET` 和 `HEAD` 是该条目拥有的唯一方法。共享条目基线在任何处理器之前用现有的
`503 {"errorCode":"service.phase.unavailable"}` 阻止其他所有方法。

## 准入与安全基线

该条目在每个已定义阶段以及每种迁移和数据库状态下都是匿名的，因此 Phase Gate 准入它时不读取
健康快照。它保持共享基线不变：带匿名客户端分区的管理速率限制策略，使出示的管理 cookie 无法
将该条目移入按操作员配额；强制安全响应 Header，包括 `Cache-Control: no-store`；以及现有的
关联契约。速率限制拒绝保持现有的 `429 application/problem+json` 响应，绝不进入处理器。

## 观察

处理器不解析任何内容。它不声明任何路由值、查询参数、Header 或请求体。它按此顺序读取，每项
至多一次：

1. 消费服务的 `IServiceHealthSnapshotSource`，受管理 API 自身 `SnapshotTimeout` 约束；
2. 本地 `BootstrapConfigurationManager` 状态，通过一个只保留存在标志的边界；
3. 进程本地的 Bootstrap 重启闩锁。

它不执行任何写入、重试、缓存或后台工作，也不跨请求持有任何可变对象。并发调用方各自组装自己
完整的观察。

## 一致性

只有本进程实际可能处于的组合才会产生成功：

| 阶段 | 本地 Bootstrap 文件 | 重启闩锁 | 结果 |
| --- | --- | --- | --- |
| `BootstrapConfiguration` | 不存在 | 任意 | 成功 |
| `BootstrapConfiguration` | 有效 | `true` | 成功 |
| `BootstrapConfiguration` | 有效 | `false` | 不可用 |
| `PendingSetup`、`Completed` | 有效 | 任意 | 成功 |
| `PendingSetup`、`Completed` | 不存在 | 任意 | 不可用 |
| 任意 | 损坏、不匹配、不可读 | 任意 | 不可用 |

迁移和数据库状态不施加进一步限制：每个已定义值都会被投影。

## 响应

| 结果 | HTTP 结果 |
| --- | --- |
| 一致的有限观察 | `200 application/json`，带五个固定字段 |
| 快照源不存在、为 null 或失败 | `503 {"errorCode":"management.status.unavailable"}` |
| Bootstrap 文件损坏、不匹配或不可读 | 相同的 `503` |
| 不一致的组合 | 相同的 `503` |
| 内部故障、内部取消或内部超时 | 相同的 `503` |
| 调用方取消 | 原始 `RequestAborted` token 传播 |

成功响应体恰好包含 `phase`、`migrationStatus`、`databaseStatus`、`bootstrapConfigured` 和
`restartRequired`，逐字段写入：

```json
{
  "phase": "completed",
  "migrationStatus": "succeeded",
  "databaseStatus": "reachable",
  "bootstrapConfigured": true,
  "restartRequired": false
}
```

枚举值为固定的小写 snake case：`bootstrap_configuration`、`pending_setup`、`completed`；
`not_started`、`running`、`succeeded`、`failed`；`reachable`、`unreachable`。超出这些枚举的
值视为不可用，绝不猜测。

`HEAD` 返回相同的状态码和相同的 Header，包括 `Content-Length`，但没有响应体。

## 否定性披露保证

任何快照、`BootstrapManagementStatus`、配置、路径或异常对象都不会到达序列化器。因此响应、
错误体和 ServiceMantle 自身的诊断都不携带 ServiceId、InstanceId、数据库 provider、服务器版本、
连接字符串、MasterKey、Bootstrap 文件路径、健康源错误码或异常文本。不可用响应体只携带封闭的
错误码，别无其他，因此存储故障、不一致组合和内部超时对匿名调用方不可区分。

## 重启闩锁

该闩锁是每进程一个。它初始为 `false`，仅在本进程成功创建或替换其自身本地 Bootstrap 文件后
被置位，绝不被持久化，并在重启时复位。它绝不声明另一个实例已重启、另一个进程写过 Bootstrap，
或配置在任何地方已被激活。

## 明确的非保证

- 该投影是在一次调用期间组装的一次一致观察。它不会在响应写入之后冻结阶段、Bootstrap 文件、
  迁移、数据库或闩锁。
- 内部预算约束的是健康源让出之后的等待。本地 Bootstrap 读取是同步文件访问；它在完成之后才
  对照预算检查，不会被抢占。不配合的消费方源无法被强制终止。
- 匿名披露保证覆盖 ServiceMantle 自身的投影、固定响应和诊断。它不覆盖第三方请求日志、原始
  请求捕获，或将秘密放入五个固定字段之外某个值的消费组件。
- 该条目不创建、不更新、不返回任何 Bootstrap 配置，也不实现任何 Setup、登录、cookie 或管理
  写入。
