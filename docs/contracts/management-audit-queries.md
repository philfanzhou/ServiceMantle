# 管理审计查询 HTTP 契约

`MapServiceMantleAuditQueries()` 把一个只读 endpoint 选入
`MapServiceMantleManagementApiV1()` 返回的受保护组：

```text
GET {versionedRoot}/audit
```

默认路由是 `GET /management/v1/audit`。该 endpoint 继承 v1 组的管理 phase gate、管理员授权、
管理速率限制、安全响应 Header 与关联 ID 行为。它必须恰好映射一次，作为该组的直接子级，并要求
一个 scoped 或 singleton 的 `IManagementAuditQueryService` 注册。它对每个被接受的请求执行一
次查询，绝不保存或提交消费应用的工作单元。

## 查询输入

精确、区分大小写的查询键允许列表为 `action`、`targetType`、`targetId`、`operatorId`、
`fromUtc`、`toUtc`、`page`、`pageSize`、`sortOrder` 与 `cursor`。未知、重复、大小写不同或空的
值会在调用查询服务之前被固定的管理 `400` 响应拒绝。不接受请求体。

- `page` 默认为 `1`，接受从 `1` 到 `int.MaxValue` 的无符号十进制值。
- `pageSize` 默认为 `50`，接受从 `1` 到 `200` 的无符号十进制值。
- 第 1 页拒绝游标。之后每一页都要求前一响应中的游标。
- `sortOrder` 恰好是 `newest`（默认）或 `oldest`。
- Action、target type、target ID、operator ID 与游标输入在核心审计值类型验证并规范化它们之前
  先做长度检查。游标限制为 512 个字符。
- 时间是不变区域性的 ISO-8601 值，秒为必需，且带 `Z` 或显式的 `±HH:mm` 偏移。一到七位小数可
  选。每个时间最多 40 个字符，并被转换为 UTC。当两个边界都存在时，闭区间范围不能超过 366 天。

## 成功响应

一个 `200 application/json` 响应恰好包含 `items`、`page`、`pageSize`、`totalCount`、
`continuationCursor` 与 `hasNextPage`。每个条目显式且仅包含 `id`、`operator`、`action`、
`target`、`outcome`、`occurredAtUtc`、`clientIp`、`correlationId`、`securityDescription` 与
`metadata`。GUID 使用 `D` 形式，时间为 UTC，结果为 `unknown`、`success`、`failure` 或
`denied`，可空字段显式写为 `null`。持久化实体、异常与序列化的元数据存储绝不直接序列化。

## 失败与并发边界

HTTP 解析失败与 `audit.query_*` 查询失败返回固定的管理 invalid-request 响应。
`audit.entity_invalid`、其他查询异常与内部取消返回 `503 application/json`，body 恰好为
`{"errorCode":"management.audit.unavailable"}`。调用方取消仍然是调用方取消，不产生成功或错误
body。

## 请求取消优先级

查询依赖（包括 scoped 查询服务的 DI 解析）正常或异常落定时，已经观察到的 `RequestAborted`
优先于普通响应映射，也优先于原样传播依赖自己的取消。此时 endpoint 以新建的
`OperationCanceledException` 结束：恰好携带原 `RequestAborted`，固定英文消息
`The management audit query request was cancelled by the caller.`，无 `InnerException`，
不复用底层异常，不泄露其 canary 或合成秘密。

| 查询依赖的落定方式 | `RequestAborted` 已取消 | 交付 |
| --- | --- | --- |
| 返回正常页 | 是 | 调用方 `OperationCanceledException` |
| 返回 null | 是 | 调用方 `OperationCanceledException` |
| 抛 `audit.query_*`（否则 400） | 是 | 调用方 `OperationCanceledException` |
| 抛 `audit.entity_invalid`（否则 503） | 是 | 调用方 `OperationCanceledException` |
| 抛普通异常（否则 503） | 是 | 调用方 `OperationCanceledException` |
| 抛内部 OCE：外来 token、合成 message/inner（否则 503） | 是 | 调用方 `OperationCanceledException`，携带 `RequestAborted`，无 inner |
| DI query factory 取消并抛异常 | 是 | 调用方 `OperationCanceledException` |
| 上述任意情况 | 否 | 原有限映射（200/400/503），保持不变 |

预取消的请求不解析查询服务、不执行查询；至多一次查询，不重试、不保存。未取消时有限响应与
查询输入、权限和安全 Header 行为不变；两个并发请求只取消一个时，另一请求的结果与关联 ID
不受影响。取消的异常同步抛出与异步任务落定遵循同一优先级。

## 不承诺的内容

- **检查点之后的窗口。** 保证覆盖依赖完成后的取消观察点；不承诺返回检查点之后才发生的取消
  仍能拦截，也不修改已开始的响应。
- **不配合的查询。** 不强制终止忽略 token 的查询实现，不对数据库扫描或查询时长设限。
- **秘密边界。** 不对恶意自定义结果集合的任意枚举副作用、第三方日志或进程内存作秘密保证；
  仍按现有封闭投影处理受支持的审计结果。

该 endpoint 保留核心查询的普通 keyset 语义。游标是绑定到查询的不透明续传值，不是签名的授权
token、重放防御或数据库快照。总数可能在请求之间变化，剩余排序区间中被回填的记录可能出现在更后
的页上。输入边界不对数据库扫描、provider 缓冲、进程分配或查询时长施加绝对上界。输出安全覆盖审
计域支持的敏感内容格式、显式响应投影、固定错误，以及本 endpoint 自身的诊断；它不是通用的秘密检
测，也不覆盖第三方日志或已经开始的响应。
