# 管理设置查询：接纳与交付的请求取消优先级

`MapServiceMantleSettingQueries` 暴露的两个只读 endpoint（`GET {root}/settings/definitions` 与
`GET {root}/settings`）由 `SettingQueryHandlers.Definitions` 与 `CurrentValuesAsync` 处理，其
投影、有界输入与失败应答契约见
[管理设置项查询契约](management-setting-queries.md)。本文档只固定这两个 handler 内部的请求取消
观察点：当调用方已经取消时，查询在哪些依赖落定边界上以取消结束，普通结果（400 / 503 / 200）
何时不再交付，以及哪些内容刻意留在承诺之外。两条路由使用相同的取消规则。

## 检查点

一次查询按顺序经过以下取消检查点，每个结果出口在交付前都先观察 `RequestAborted`：

1. **入口。** 已经取消的调用方在有界 query 读取、scoped 服务解析或刷新之前收到取消；此时
   query feature 零次读取、`ServiceSettingQueryService` 零次解析、快照零次刷新（definitions 路由
   本就不刷新）。
2. **有界 query 读取落定。** `SettingQueryMapping.TryParseGroup` 正常返回（接纳或拒绝 group）或
   异常落定后，先观察 `RequestAborted`，再分类。观察到取消时不交付固定 `400`，也不进入服务解析
   或刷新。
3. **单次刷新落定（仅 current-values）。** `GetCurrentAsync` 正常返回后、映射前观察
   `RequestAborted`；成功或失败的刷新都不在取消已发生时交付 `200` 或固定 `503`。
4. **交付已构造结果前。** 序列化缓冲构造完成后、返回前再观察一次 `RequestAborted`。

在任一检查点上观察到调用方取消，或任一查询依赖（有界 query 读取、scoped 解析、单次快照刷新）
在请求已中止后落定时，handler 以一个 `OperationCanceledException` 结束，其 `CancellationToken`
恰好是 `RequestAborted`，`InnerException` 为 null，消息与 `ToString` 不携带依赖异常文本、快照错误
码、设置值或合成秘密。该安全取消优先于 handler 已经计算出的所有其他结果与分类，包括 query
feature 或刷新抛出的普通异常和别人 token 的内部 `OperationCanceledException`；内部 token 的取消
绝不被重抛。核心 loader 对刷新期间取消（含等待刷新锁时取消）本就交付携带调用方 token 的安全
取消，handler 原样转交该优先级。

没有调用方取消时，一切不动：合法 query 仍返回 `200`（definitions 目录、current-values 恰好一次
完整刷新后的版本与投影，敏感值仍为 null，不回显默认值或约束）；空匹配仍返回 `200` 与空投影；
非法 query 仍固定 `400` 且零解析、零刷新；刷新失败仍固定 `503`；未取消的未映射异常继续按原政策
传播，不借此改判为 `503`。检查点只新增取消观察，不改变任何未取消路径的分类、计数或投影。

## 不承诺的内容

- **观察点之后的窗口。** 只保证 handler 的入口、query 读取落定、刷新落定与结果交付观察点；不
  保证观察点之后或响应开始写入之后到达的取消。检查点是边界，不是持续守卫。
- **不强行中断。** 忽略其 token 的同步 query feature、序列化器或快照 source 不会被强制中断，本层
  不新增超时。
- **不撤回已发布快照。** 已由 loader 发布的快照不因取消而撤回；查询不写数据库，也不改变跨实例
  一致性。
- **未取消异常政策不变。** 未取消时第三方或依赖抛出的未映射异常仍按既有管道政策处理，本项不
  将其统一改判，也不纳入新保证。
- **秘密断言的边界。** 消息 / `ToString` / `InnerException` 的否定性断言只覆盖本 handler 主动产生
  的异常与结果，不对第三方请求日志或进程内存作任何说明。

## 如何被覆盖

`SettingQueryAdmissionCancellationTests`（ASP.NET Core，专属 query feature / 快照 source /
service provider doubles，用 `DefaultHttpContext` 直接驱动 handler，不经 phase gate，也不使用或
修改共享 host fixture）覆盖：

- 两个入口 × 合法/非法 query × 预取消：恰好携带调用方 token 的 OCE，query feature 零读取、服务
  零解析、快照零刷新。
- query feature 在读取中取消并正常交付合法/非法 query，或抛普通异常/内部 OCE：先交付固定消息的
  caller OCE（无 `InnerException`、无原始异常文本或合成秘密），服务零解析、快照零刷新。
- current-values 的 source 正常返回/抛普通异常/内部 OCE 同时取消：交付 caller OCE，服务恰好一次
  解析、source 恰好一次调用；等待刷新锁时取消同样交付 caller OCE，被占用的持有者仍完成 `200`。
- 未取消对照：definitions 合法 `200` 目录、非法 `400`；current-values 合法 `200`（版本与投影、
  敏感值为 null 且不回显）、非法 `400`（零解析零刷新）、空匹配 `200` 空投影、刷新失败固定
  `503`、未映射异常按原样传播（非 `503`）。
- 两个并发请求只取消一个：另一个正常得到完整版本 `200`，各自 source 计数与 token 独立不串用。

既有 `SettingQueryEndpointTests` 的路由、投影、有界输入、敏感值 null 与失败应答契约保持不变。
