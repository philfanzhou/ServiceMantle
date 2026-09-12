# Management 设置项查询契约

`MapServiceMantleSettingQueries` 向受保护的 management API v1 组添加两个只读 endpoint：
`GET /settings/definitions` 返回已注册的设置项目录，`GET /settings` 在一个完整、刷新成功的
版本中返回每个设置项的当前值。它们是既有的 provider 无关 `ServiceSettingQueryService` 的一层
薄 HTTP 适配：不添加任何 store、root-key source、缓存或写入路径。

本文档描述固定的投影、有界输入、唯一的失败应答，以及明确不覆盖的内容。周边基线在
[受保护的 management API v1 契约](management-api-v1.md) 中描述。

## 接线

```csharp
builder.Services
    .AddServiceMantle(serviceId, instanceId)
    .AddSecurityResponseHeaders()
    .AddSensitiveHeaders()
    .AddRateLimiting()
    .AddManagementCookieAuthentication()
    .AddServiceMantleManagementApiV1();

builder.Services.AddSingleton<IServiceHealthSnapshotSource, ProductSnapshotSource>();
builder.Services.AddSingleton<IServiceSettingDefinitionProvider, ProductSettingDefinitions>();
builder.Services.AddSingleton<IServiceSettingStore, ProductSettingStore>();
builder.Services.AddSingleton<IServiceSettingRootKeySource, ProductRootKeySource>();
builder.Services.AddServiceMantleSettingSnapshots();

var app = builder.Build();
app.UseServiceMantlePipeline();

var management = app.MapServiceMantleManagementApiV1();
management.MapServiceMantleSettingQueries();
```

这些 endpoint 是选择性启用的。`AddServiceMantleManagementApiV1` 和
`MapServiceMantleManagementApiV1` 从不添加它们，因此没有调用 `MapServiceMantleSettingQueries`
的宿主在这里不会暴露任何内容。消费服务拥有 store 或快照源、定义目录，以及——只要目录中包含敏感
设置项——root-key source；`AddServiceMantleSettingSnapshots` 就是把它们绑定在一起的注册。宿主
若不具备该能力却映射这些 endpoint、把它们映射多次，或者把它们映射到
`MapServiceMantleManagementApiV1` 返回的路由组之外的任何路由组（包括其下的嵌套组），都会在宿主
启动前失败，且失败消息固定、绝不重复任何已配置的值。

| 路由 | 默认完整路径 | 刷新行为 |
| --- | --- | --- |
| `GET /settings/definitions` | `/management/v1/settings/definitions` | 从不刷新 |
| `GET /settings` | `/management/v1/settings` | 每个请求恰好刷新一次 |

自定义版本化根会随路由组一起移动这两个 endpoint。两个 endpoint 都不接受写入方法，也都不暴露
更新、Setup、审计或搜索面。

## `group` 查询参数

两个 endpoint 都接受一个可选查询参数，拼写必须完全为 `group`。

| 规则 | 值 |
| --- | --- |
| 出现次数 | 至多一次；重复的 `group` 会被拒绝 |
| 其他参数 | 没有。任何其他查询键都会被拒绝，无论单独出现还是与 `group` 一起出现 |
| 原始长度 | 至多 128 个字符 |
| 规范化 | 先 `Trim()` 再 `ToLowerInvariant()`；结果必须非空 |
| 语法 | `[a-z0-9][a-z0-9._-]*`，与规范化后的设置项键形状相同 |
| 匹配 | `key == group`，或 `key` 以 `group + "."` 开头（ordinal 比较） |

不存在 group 领域模型，也没有 group 注册表：这个值只是对键的过滤器。一个被接受但没有匹配到任何
键的 group 返回空集合——对当前值而言，同时附上刚刚成功完成的那次完整刷新的版本。被拒绝的输入
应答固定的 management `400` 结果（`management.request.invalid`），并且从不回显该值。

过滤器只影响输出的形状。它绝不减少底层加载、解密和校验的工作量，也绝不可能让未知键、损坏值或
完整快照的任何其他失败，为另一个 group 应答 `200`。

## 定义响应

```json
{
  "definitions": [
    {
      "key": "product.retention-days",
      "valueType": "number",
      "isRequired": true,
      "isSensitive": false,
      "hasDefault": true,
      "requiresRestart": false
    }
  ]
}
```

`200 application/json`。每一项恰好携带这六个字段、按此顺序，且各项按键以 ordinal 升序排序。
`valueType` 是 `string`、`number`、`boolean`、`json` 之一。

目录是从核心查询服务已经产出的安全定义投影投射而来。响应中不包含默认值，也不包含约束对象——
只有 `hasDefault`。这个请求从不刷新快照，因此绝不触碰 store、root key 或网络。

## 当前值响应

```json
{
  "version": 7,
  "values": [
    {
      "key": "product.retention-days",
      "valueType": "number",
      "isRequired": true,
      "isSensitive": false,
      "hasDefault": true,
      "requiresRestart": false,
      "hasValue": true,
      "source": "persisted",
      "value": "30"
    }
  ]
}
```

`200 application/json`。`version` 是本请求刷新的那一个完整快照的 int64 版本；每一项都来自
同一版本。每一项恰好携带这九个字段、按此顺序，按键以 ordinal 升序排序。`source` 是 `missing`、
`default`、`persisted` 之一。值为 `null` 的字段总是被写出，绝不省略。

`value` 是非敏感值按既有 invariant 规范化的结果——`number` 是 invariant 十进制数，`boolean`
是 `true`/`false`，`json` 是规范 JSON 文本——并且只要值缺失**或**定义被标记为敏感，它就是
`null`。这对每一种敏感值类型都成立，包括敏感的 `number`、`boolean` 和 `json`。

## 拒绝

| 情况 | 状态码 | 响应体 |
| --- | --- | --- |
| 无效、重复或未知的查询输入 | 400 | Problem Details，`management.request.invalid` |
| 完整快照的任何刷新失败 | 503 | `{"errorCode":"management.settings.unavailable"}` |
| 阶段门、会话、权限和配额拒绝 | 503 / 401 / 403 / 429 | management API v1 基线，保持不变 |
| 未预期的未映射异常 | 500 | Problem Details，`http.internal_server_error` |

`503` 响应体是封闭的。它覆盖既有加载器产生的每一种失败分类——未知或重复的键、混杂或缺失的
版本、类型不匹配、不在受支持封装中的敏感值、损坏的密文、错误或不
可用的 root key、值或复合约束失败、存储错误，以及过期或同版本冲突的快照——并且从不报告是哪一
种，从不点名某个键，也从不应答部分值或先前已激活的值。

调用方取消始终是调用方取消：已取消的请求从不刷新；在另一次刷新持有加载器锁期间的取消，或协作式
读取过程中的取消，表现为调用方自己的取消，而不是 `503` 或 `500`。应答携带六个强制安全响应
Header 和恰好一个 `x-correlation-id`，并且在 Development 和 Production 中完全一致。

## 消费方职责

- 将每个值为机密的设置项标记为敏感。未标记为敏感的值在设计上就是可读的，这个 endpoint 不会
  试图检测其中是否含有机密。
- 拥有 store、定义目录、root-key source，以及持久化版本的准确性。键、身份、版本和格式良好的
  Correlation ID 必须保持可公开披露。
- 拥有超出路由组 `Admin` 基线的授权，并拥有写入：本契约没有更新路径。

## 不包含与不保证

- 没有事务性更新、Setup 流程、审计查询、默认值或约束细节、分页、搜索、缓存或后台刷新。这些由
  各自的任务拥有，**不**在这里交付。
- 保证是：标记为敏感的值绝不出现在此响应体、固定错误或这个 endpoint 自己的诊断中；定义响应不
  携带默认值和约束对象；刷新失败时绝不应答部分值或先前的值。它不覆盖被替换的库服务或策略、
  第三方日志、进程内存，或响应开始写出之后的任何事情。
- 分组只影响输出。对目录大小、值大小、阻塞式 source 或同步校验器没有界限，没有额外的内部查询
  超时，只有 source 和加载器已经提供的协作式取消。
- 一个响应就是一个版本；两个请求可能观察到不同的版本。刷新可能激活进程本地的快照访问器，这是
  既有查询契约的一部分——不写数据库、不提交消费方工作单元，也不隐含任何跨实例原子发布。
- 阶段门放行请求之后的阶段变化不会撤回该请求，本契约也不携带任何跨站写入保护。
