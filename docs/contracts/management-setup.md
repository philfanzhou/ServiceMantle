# Setup 状态与完成契约（#96）

状态：已实现。本文档描述 ServiceMantle 管理面的匿名 Setup 条目。它细化
[management-entry-authorization.md](management-entry-authorization.md) 中的 Setup 行，
不向其添加任何内容。

## 映射

`MapServiceMantleSetup` 一次性映射两个条目，并要求提供显式的消费方事务执行器：

```csharp
app.MapServiceMantleSetup(async (httpContext, setupCode, cancellationToken) =>
{
    await using var scope = httpContext.RequestServices
        .GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
    // validate read-only, orchestrate, stage the consumption, save once, commit
});
```

这两个条目是所配置版本化根的直接子项，与 `MapServiceMantleManagementApiV1` 返回的受保护组
并列映射，绝不位于其内部。使用默认根时完整路径为 `/management/v1/setup`；自定义版本化根会
同时移动两者。它们至多被映射一次。缺少执行器、缺少共享管理条目能力或管理 API v1 能力，以及
重复映射，都会在宿主启动前失败。

## 准入与安全基线

共享条目基线仅在 `PendingSetup + Succeeded + Reachable` 或 `Completed + Succeeded + Reachable`
时准入这两个条目。`Completed` 保持被准入，使重放到达稳定的冲突而非阶段拒绝。两个条目在 Setup
速率限制策略下均为匿名、使用客户端分区，携带强制安全响应 Header 与关联契约，并且 `POST` 额外
要求恰好一个 `X-ServiceMantle-Request: 1` Header。Phase Gate 拒绝、速率限制拒绝、缺失或畸形
的不安全请求 Header，都不执行任何读取、任何解析器和任何执行器。

## 读取状态

`GET` 和 `HEAD {v1}/setup` 对现有 `IServiceInstallationStore` 读取一次，并恰好投影出
`{"status":"pending"}` 或 `{"status":"completed"}`。它们不暴露任何 Setup Code 的生成、摘要、
签发或过期时间、操作员、贡献者或存储值，也不引入第二个安装权威。`HEAD` 返回相同的状态码和
Header，包括 `Content-Length`，但没有响应体。存储缺失、安装行不存在和存储故障都返回
`503 {"errorCode":"management.setup.unavailable"}`。

## 完成 Setup

`POST {v1}/setup` 首先读取安装权威。已完成的安装返回固定的管理 `409`，**完全不解析、不验证
所提供的 code**，这使重放成为稳定边界，而非凭据预言机。

在待完成期间，请求必须满足以下每一条规则，任何违反都返回固定的管理 `400`，且不回显请求的任何
一个字节：

- `application/json`，可选带 `charset=utf-8` 参数，别无其他；
- 无查询字符串，无 `Content-Encoding` Header；
- 原始响应体至多 4 KiB，在任何解码之前按字节计数；
- JSON 深度至多为 4，无注释，无尾随逗号；
- 顶层对象恰好有一个名为 `code` 的属性，区分大小写匹配，其值为字符串；重复的 `code`、`Code`、
  任何多余属性和非字符串值都被拒绝；
- 值恰好等于现有的 32 字符、区分大小写的 Base64URL Setup Code。它绝不会被修剪或规范化。

解析出的 code 随后交给消费方事务执行器，由其拥有共享工作单元。ServiceMantle 在此不拥有任何
墙钟时间预算，因为 Setup 提交是消费方的事务；由存储或执行器拥有的内部超时映射到固定的
unavailable 结果，而不是由 endpoint 强加。

### 必需的执行器序列

1. 创建一个全新的异步 scope，带有干净的 DbContext，并开启其事务。
2. 在暂存任何内容之前，只读调用 `IServiceSetupCodeStore.ValidateAsync`。
3. 在该干净的暂存 scope 上调用 `ServiceSetupOrchestrator`。
4. 调用 `IServiceSetupCodeStore.StageConsumeAsync`。
5. 恰好调用一次 `SaveChangesAsync`。
6. 提交，只有在那之后才报告 `Committed`。

暂存之后的任何 code 竞争、贡献者失败、暂存、保存、提交、取消或清理失败，都必须回滚，丢弃
整个 scope 且不重试，并绝不复用该 DbContext。

## 响应

| 结果 | HTTP 结果 |
| --- | --- |
| 状态读取 | `200`，带固定状态体 |
| 提交已确认 | `204`，空响应体且无 content type |
| 不可用的媒体类型、结构、大小、深度、code 结构或不安全 Header | 固定管理 `400` |
| 贡献者验证拒绝了完成操作 | 固定管理 `400` |
| 待完成期间无效、畸形、过期、不匹配、从未签发或重放的 code | `401 {"errorCode":"management.setup.credential_invalid"}` |
| 已完成、并发完成或版本冲突 | 固定管理 `409` |
| 存储、编排器、保存、提交、清理故障或内部超时 | `503 {"errorCode":"management.setup.unavailable"}` |
| 调用方取消 | 无 `IResult`：原始 `RequestAborted` token 传播，优先于任何边界结果 |

`401` 不披露任何子原因：无效、畸形、过期、不匹配、从未签发和重放的 code 对调用方不可区分。
null 完成结果和未定义的状态值都视为不可用，绝不猜测。

## 取消优先级

调用方自身的取消优先于边界产生的任何结果。在处理器的三个观察点——安装存储读取、请求体读取与
解析、以及执行器——每一个都在对结果分类之前检查请求的中止。如果 `HttpContext.RequestAborted`
已经取消，处理器抛出恰好携带该 token 的 `OperationCanceledException`，且不返回任何 `IResult`，
无论边界返回了什么：

| 在某个观察点，`RequestAborted` 已取消 | 结果 |
| --- | --- |
| 边界返回了可用值、拒绝或 null | 调用方的取消 |
| 边界抛出普通故障或内部超时 | 调用方的取消 |
| 边界抛出携带另一个 token 的取消 | 调用方的取消，携带调用方的 token |

抛出的异常由处理器创建。它绝不是边界抛出的异常，因此内部取消不会被当作调用方的取消透传，
且其消息、内部异常、所提供的 code，以及任何连接或存储细节都不会进入其中。

当请求**未**被取消时，一切不变：普通故障、内部超时和携带外来 token 的内部取消仍然是唯一的
固定 `503 {"errorCode":"management.setup.unavailable"}`，上表中的每个其他结果都保持其含义。
在两个阶段之间观察到的取消会使请求在此停止：解析器和执行器不会被进入，而在执行器返回或抛出
之后观察到的取消不会再次调用它、不重试它，也不代其回滚任何东西。

该优先级仅覆盖这些可观察的边界。它对处理器返回之后发生的取消不作任何说明，无法终止不配合的
同步 I/O，也不施加任何墙钟时间上限。已提交的消费方事务不会被之后的取消回滚，对畸形或恶意
执行器不提供任何补偿，且 `Response.HasStarted` 和失败的响应写入继续遵循现有序列化器契约。

## 否定性披露保证

每个响应体都是五个固定字节数组之一。任何候选 Setup Code、摘要、生成、过期、安装版本、
贡献者值、存储错误码或异常文本都不会到达序列化器、消息或 ServiceMantle 诊断。解析器租用的
缓冲区（曾持有候选值）在归还池时被清零。

## 明确的非保证

- `ServiceSetupOrchestrator` 成功和 `SetupCodeConsumptionResult.IsStaged` 继续表示已暂存，
  而非已提交。只有执行器的 `Committed` 结果声明共享事务已提交，且只有 endpoint 的 `204` 报告
  这一点。
- ServiceMantle 绝不隐式提交现有的消费方工作单元。畸形或恶意执行器不在 endpoint 保证范围内。
- 数据库回滚无法恢复由不合规贡献者执行的外部副作用；贡献者保持其现有的仅暂存契约。
- 准入不会冻结阶段。Gate 准入请求之后的阶段变化不会将其召回；安装行自身的并发控制仍然是完成
  权威。
- endpoint 不签发、不轮换任何 Setup Code，不提供跨请求幂等键，不重试任何冲突。`Completed`
  本身就是稳定的重放边界。
- `X-ServiceMantle-Request` 与仅 JSON 解析是针对该条目集的有限浏览器 CSRF 缓解措施，不是
  CORS、TLS 或代理策略。
