# 管理设置更新：接纳与交付的请求取消优先级

`MapServiceMantleSettingUpdates` 暴露的 `POST {versionedRoot}/settings` 由
`SettingUpdateHandlers.UpdateAsync` 处理，其请求/响应契约见
[管理设置更新 HTTP 契约](management-setting-updates.md)。本文档只固定该 handler 内部的请求取消
观察点：当调用方已经取消时，更新在哪些依赖落定边界上以取消结束，普通结果（Forbid / 400 /
409 / 503 / 200）何时不再交付，以及哪些内容刻意留在承诺之外。

## 检查点

一次更新按顺序经过以下取消检查点，每个结果出口在交付前都先观察 `RequestAborted`：

1. **入口。** 已经取消的调用方在解析操作员、读取请求体或执行 executor 之前收到取消；此时
   `IManagementCurrentOperatorResolver` 零次解析、请求体零次读取、executor 零次调用。
2. **操作员解析落定。** `resolver.Resolve` 正常返回（`Resolved` / `Unauthenticated` /
   `ClaimsInvalid` / `null`）或异常落定后，先观察 `RequestAborted`，再分类。观察到取消时不交付
   Forbid，也不进入请求体读取。
3. **请求体解析落定。** `SettingUpdateRequestParser.ParseAsync` 正常返回（有效命令或 `null`，
   包括空体、畸形 JSON、超过长度限制的读取）或异常落定后，先观察 `RequestAborted`，再分类。
   观察到取消时不交付 `400`，也不调用 executor。
4. **executor 落定。** executor 正常返回后、映射结果前观察 `RequestAborted`；已应用/冲突/失败
   的普通映射不在取消已发生时交付。

在任一检查点上观察到调用方取消时，handler 以一个 `OperationCanceledException` 结束，其
`CancellationToken` 恰好是 `RequestAborted`，`InnerException` 为 null，消息与 `ToString` 不携带
依赖注入的异常文本、原始错误、密文或合成秘密。该安全取消优先于 handler 已经计算出的所有其他
结果与分类，包括 resolver/parser/executor 抛出的普通异常和别人 token 的内部
`OperationCanceledException`；内部 token 的取消绝不被重抛，落定后若调用方未取消则仍归一为原
`503` 安全失败。

没有调用方取消时，一切不动：`Resolved` 且请求体有效仍恰好一次 executor 调用并映射
`200 / 400 / 409 / 503`；未解析的操作员仍 Forbid；无效请求体仍固定 `400`；executor 的普通异常与
内部取消仍为 `503`。检查点只新增取消观察，不改变任何未取消路径的分类、计数或提交语义。

## 不承诺的内容

- **检查点之间的窗口。** 只保证进入本 handler 的上述落定检查点；不撤回检查点之后、响应开始写入
  之后到达的取消。检查点是边界，不是持续守卫。
- **不强行中断。** 忽略其 token 的同步 resolver、请求体流或 executor 不会被强制中断，本层不新增
  超时，也不约束不配合依赖的耗时。
- **不撤销副作用。** 取消不回滚 executor 已经提交的数据库，不提供新的事务或取消补偿协议；已确认
  提交之后的取消不意味着提交可被撤销。
- **并发隔离。** 两个独立请求各自拥有 `RequestAborted`、resolver、请求体与 executor，取消一个不
  影响另一个的计数与结果；对同一 scoped 工作单元的并发使用没有承诺。
- **秘密断言的边界。** 消息 / `ToString` / `InnerException` 的否定性断言只覆盖本 handler 主动产生
  的异常与结果，不对第三方请求日志、原始请求保留或进程内存作任何说明。

## 如何被覆盖

`SettingUpdateAdmissionCancellationTests`（ASP.NET Core，专属 resolver / 请求体流 / executor
doubles，直接驱动 handler，不经共享 host fixture）覆盖：

- 预取消：`ResolveCount`、resolver 调用、请求体读取、executor 调用均为零，交付携带调用方 token
  的安全 OCE。
- **操作员解析落定检查点**：resolver 内取消后分别返回 `Resolved / Unauthenticated / ClaimsInvalid
  / null`，或抛普通异常 / 内部 OCE；全部交付 caller OCE，请求体零读取、executor 零调用。
- **请求体解析落定检查点**：body 读取内取消后分别返回有效 JSON、空体、畸形 JSON、超过长度限制
  的读取，或抛普通异常 / 内部 OCE；用可控 stream（不靠延迟竞争）交付 caller OCE，executor 零调用。
- 未取消对照：`200`（恰好一次 executor 调用）、Forbid（`ForbidHttpResult`，body 与 executor 零
  调用）、`400`（空体/畸形/超长）、`409`、`503`（存储失败、内部取消）映射与计数保持不变。
- 新建 OCE 恰好携带 `RequestAborted`，无 `InnerException`、原始错误文本或合成秘密。
- 两个并发请求只取消一个：另一个正常得到完整 `200`，各自 resolver / body / executor 计数与
  token 独立不串用。

既有 `SettingUpdateEndpointTests` 与 EF `ManagementSettingUpdateHttpWiringTests` 的成功、Forbid、
`400 / 409 / 503`、提交屏障与并发关联契约保持不变。
