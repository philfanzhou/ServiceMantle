# 管理设置更新 HTTP 契约

`MapServiceMantleSettingUpdates(executor)` 将一个事务性写入 endpoint 加入
`MapServiceMantleManagementApiV1()` 返回的受保护组：

```text
POST {versionedRoot}/settings
```

默认路由是 `POST /management/v1/settings`。它继承 completed/succeeded/reachable 阶段门、
管理员授权、管理速率限制、安全响应 Header 和关联 ID 行为。该 endpoint 恰好被映射一次，作为
v1 组的直接子项。

必需的 `SettingUpdateExecutor` 是消费方拥有的提交边界。它解析一个全新的 scoped 工作单元，
开启事务，调用 `ServiceSettingUpdateService`，只提交已应用（applied）的结果，并且只有在该
提交完成后才返回 Applied。失败和取消会回滚并丢弃该 scope，不重试。该 endpoint 绝不访问
`DbContext`，绝不提交现有的消费方工作单元，也绝不改变 Applied 的核心含义（已保存但未提交）。

## 请求

该 endpoint 只接受 `application/json`，无查询字符串、无内容编码，请求体不大于 256 KiB。
可选的 charset 必须是 UTF-8。JSON 深度至多为 8。确切的请求体为：

```json
{
  "expectedVersion": 7,
  "changes": [
    { "key": "product.name", "value": "Orders" },
    { "key": "product.optional", "value": null }
  ]
}
```

两个顶层属性都是必需的且区分大小写；未知或重复的属性被拒绝。`expectedVersion` 是从零到
`long.MaxValue` 的整数。`changes` 包含一到 32 个对象，每个恰好有 `key` 和 `value`。每个原始
key 为一到 128 个字符，且修剪后必须保持非空。修剪并按不区分大小写比较后发生冲突的 key 被
拒绝。value 是 JSON 字符串或 `null`；字符串原样传递，null 移除显式持久化的值。现有的更新器
验证已注册的 key 和完整候选值，包括字符串、数字、布尔、JSON、required/default、敏感值以及
组合约束。请求数据绝不选择审计操作员；操作员由 `IManagementCurrentOperatorResolver` 从已
认证的 principal 解析。

## 响应与边界

- 已应用且带正的已提交版本，返回 `200 application/json` 和恰好 `{"version":N}`。
- 格式和验证失败返回固定的管理 `400`。
- 版本冲突或耗尽返回固定的管理 `409`。
- 保护、存储、事务、上下文、畸形结果、异常和内部取消失败返回 `503 application/json` 和恰好
  `{"errorCode":"management.settings.update_unavailable"}`。
- 当前操作员未解析时，调用配置的禁止（forbid）方案，不执行更新。
- 调用方取消仍然是调用方取消。已确认提交之后的取消并不意味着数据库提交可以被撤销。

任何响应都不包含验证细节、key、value、密文或异常文本。该 endpoint 不添加跨请求幂等、自动
冲突重试、快照发布、数据库性能上限、对未知提交结果的补偿，或完整的 CSRF/CORS/TLS 策略。
它的否定性秘密保证覆盖该 endpoint 的响应体、固定错误和诊断，加上更新器现有的仅 key 审计
行为；它不覆盖第三方请求日志、原始请求保留、进程内存、消费方异常接收器，或形似秘密的已
注册 key。
