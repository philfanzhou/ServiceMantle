# 管理会话契约（#97）

状态：已实现。本文档描述管理身份登录、当前会话与登出三个入口。它细化了
[management-entry-authorization.md](management-entry-authorization.md) 中 Login、Logout 与
Current session 三行，并且不向其中添加任何内容。

## 映射

`MapServiceMantleManagementSession` 一次性映射全部三个入口，并要求一个显式的消费方登录
adapter：

```csharp
app.MapServiceMantleManagementSession(
    async (httpContext, cancellationToken) =>
    {
        // Parse the media type and schema this service actually supports, inside the 64 KiB
        // envelope ServiceMantle already admitted.
        var credentials = await ReadCredentialsAsync(httpContext, cancellationToken);
        httpContext.RequestServices.GetRequiredService<MyScopedCredentialAccessor>().Set(credentials);
        return await ManagementIdentityProviderInvoker.InvokeAsync(
            httpContext.RequestServices.GetRequiredService<IManagementIdentityProvider>(),
            cancellationToken);
    },
    options => options.LoginTimeout = TimeSpan.FromSeconds(5));
```

这三个入口是所配置版本化根路径的直接子级，映射在 `MapServiceMantleManagementApiV1` 返回的受
保护组旁边、绝不位于其内部，因此匿名登录例外只存在于共享入口基线中，Admin 组仍然拒绝匿名访
问。它们最多只能映射一次。缺少 adapter、登录超时不在 100 毫秒到 30 秒之间、缺少必需能力，以
及第二次映射，都会在宿主启动之前失败。

## 准入与安全基线

共享入口基线仅在 `Completed + Succeeded + Reachable` 状态下放行全部三个入口。Login 在 Setup
速率限制策略下使用客户端分区并标记 `AllowAnonymous`；当前会话读取与登出在按管理操作员分区的
策略下要求**任意**合法的 ServiceMantle 管理身份 cookie——不要求 Admin。两个 `POST` 入口都要
求恰好一个 `X-ServiceMantle-Request: 1` Header。三个入口都携带强制的安全响应 Header 与关联契
约。Phase Gate 拒绝、速率限制拒绝、cookie 缺失或不可接受、claim 无效，以及缺少不安全请求
Header，都不会执行 adapter、不会登录、也不会登出。

## 登录

ServiceMantle 在 adapter 运行之前最多准入 64 KiB 的原始请求体，并拒绝查询字符串或
`Content-Encoding` Header。信封大小是在读取请求体的过程中累计的，而不是取自某个 Header：声明
长度超过限制会在读取任何字节之前得到固定的管理 `400`；完全没有声明长度的请求体——例如分块上
传——受到完全相同的限制约束；而声明长度低于限制并不能豁免其后的任何内容。超出信封的字节最多
只会被读取一个，正是这个字节证明了超限，被准入的副本保存在内存中，绝不写入磁盘。

宿主自身的请求大小限制保持宿主设置的原样。ServiceMantle 绝不放宽它，也绝不收紧它：对分块请
求计数的服务器会把分块帧本身也计入，因此把该限制降到信封大小反而会拒绝一个位于信封之内的请
求体。限制本身就更小的宿主仍然会先行拒绝，其 `413` 会被报告为同一个固定的管理 `400`。信封内
的媒体类型与 schema 仍是 adapter 的义务。

调用 adapter 时，整个被准入的请求体已经在内存中。在该调用内获得的 `Request.Body` 与
`Request.BodyReader` 都能读取到完整的原始字节，无论 adapter 是同步读取、异步读取、通过
`CopyToAsync` 还是通过 `StreamReader`；它们是可选方式而非先后顺序，在同一次调用内交错使用它
们没有定义的结果。只读取前缀或完全不读取的 adapter 无法放宽信封，因为该决定早已作出。当调用
结束时——返回、抛出异常或被取消——请求自身的 body 流与 body pipe 特性会被放回，只有本
endpoint 创建的副本会被释放。

adapter 接收 `HttpContext` 和一个 token，该 token 是请求 token 再叠加登录预算（默认 10 秒）
的边界。它只把凭据放入自己信任的 scoped 访问器并调用 `ManagementIdentityProviderInvoker`；
公开的核心 provider SPI 仍然不接收任何凭据对象。adapter 不得保留或返回原始凭据、不得写响应、
不得执行任何登录。

| 登录结果 | HTTP 结果 | Cookie 效果 |
| --- | --- | --- |
| 已认证且 `SignInAsync` 完成 | `204`，空 body | 签发一个固定 scheme 的 cookie |
| 未认证 | `401 {"errorCode":"management.session.unauthenticated"}` | 无 |
| 失败、null、未定义状态、已认证但无身份 | `503 {"errorCode":"management.session.unavailable"}` | 无 |
| 原始请求体超出信封，或宿主自身的 `413` | `400 management.request.invalid` | 无；不运行 adapter |
| 请求体读取失败、adapter 异常、内部取消、内部超时 | 同样的 `503` | 无 |
| 登录前响应已开始 | 同样的 `503` | 无；不尝试登录 |
| `SignInAsync` 失败 | 同样的 `503` | 它追加或替换的内容会被回滚 |
| 调用方取消 | 原始的 `RequestAborted` token 向上传播 | 无 |
| `SignInAsync` 正常完成，但完成检查点看到请求已中止 | 同上：取消向上传播 | 已追加的 ticket 先被回滚 |

### 登录回滚

响应自身的 `Set-Cookie` 值在 `SignInAsync` 开始之前被复制，该登录的每个失败出口——以及
`SignInAsync` 正常完成、但完成检查点看到 `RequestAborted` 已经取消的出口——都会在应答调用
方取消之前恢复这份副本。回滚会移除登录追加的完整或分块 ticket，并撤销对整个 Header 的替
换，包括 cookie 由处理器写入、随后 `SignedIn` 回调抛出异常，或回调在写完 cookie 之后取消
请求并正常返回的情形。响应原本已携带的
cookie——无关的 cookie 与更早的管理 cookie 一律如此——会以原始的数量、顺序和值恢复，并且不使
用任何 `SignOutAsync` 补偿，固定的 `204` 也不会交付。快照属于拍摄它的那一个请求。如果快照无法读取，则根本不开始登录；
如果恢复无法应用，则中止连接而不是完成响应，因此仍携带该 ticket 一部分的响应绝不会被发出，
原始异常也不会暴露。

未认证的 `401` 复用既有的会话错误码、状态与内容类型。它被直接写出而不是通过质询（challenge）
产生，因此匿名登录响应无法泄露是否恰好出示过某个 cookie。消费方提供的 provider 错误码绝不会被
复制进 HTTP 响应或 ServiceMantle 诊断：语法有效并不能证明一个字符串不是秘密。

## 当前会话

`GET` 与 `HEAD {v1}/session` 精确应答：

```json
{
  "authenticated": true,
  "expiresAtUtc": "2026-09-07T12:34:56.0000000Z",
  "permissions": ["management.read", "management.admin"]
}
```

权限是固定的 `ManagementPermission` 顺序中定义的名称，与 claim 顺序无关。投影会省略操作员标
识符、显示名、身份来源、claim、认证属性、ticket 材料以及任何 provider 数据。`HEAD` 应答相同
的状态码与 Header（包括 `Content-Length`），但没有 body。既有的滑动续期仍可能发生，因为该读
取通过同一个 cookie 处理器完成认证。不再能解析的 principal 保持既有的 forbidden 契约，而不携
带过期时间的 ticket 会应答固定的 unavailable 结果，而不是一个猜测的值。

operator resolver 与 `AuthenticateAsync` 各有一个完成检查点：resolver 或认证操作在
`RequestAborted` 已取消之后落定——正常返回 `Resolved`、`Unauthenticated`、`ClaimsInvalid`、
null、合法 ticket、缺失过期时间的 ticket、`NoResult`，或抛出普通异常、内部取消——都不再交付
`Current`/`Forbid`/`Unavailable` 普通结果，也不再启动下一个依赖，而是以携带原请求 token 的
安全取消结束。未取消时，resolver 的故障仍按原样向上传播，结果分类保持不变。

缺失、过期、损坏以及 claim 无效的 cookie 保持既有的 `401`/`403` 契约不变。

## 登出

`POST {v1}/session/logout` 要求有效身份但不要求 Admin。它调用固定 scheme 的 `SignOutAsync`，
并且只有在 cookie 删除已经写入响应之后才应答无 body 的 `204`。它只删除本客户端自己的宿主作用
域 cookie，不删除任何其他内容。`SignOutAsync` 正常完成、但完成检查点看到请求已取消时，固定
的 `204` 不交付，取消以原始请求 token 向上传播；已写入的删除 cookie 保留在响应上——取消的登
出绝不通过回滚让旧会话复活，也不声称外部注销副作用未发生。

## 明确的不保证

- 登出是本地的、无状态的。它不添加任何服务端吊销权限，也无法使一个已复制的 ticket 失效：同
  一 ticket 从另一个客户端出示时，在过期之前仍然可以认证。cookie 过期、Data Protection 密钥环
  以及跨实例语义均保持不变。
- ServiceMantle 不实现任何具体的账户、密码、OIDC 或产品身份 provider，也不定义通用的凭据
  schema。
- 64 KiB 限制只是 ServiceMantle 的准入信封。消费方 adapter 仍必须严格解析自己的媒体类型与
  schema。
- 一个登录预算同时覆盖读取原始请求体和 adapter 调用，且不会在两者之间重置。预算在请求体仍在
  到达时耗尽的登录会应答固定的 unavailable 结果，绝不调用 adapter。
- 登录预算约束的是对协作式 adapter 与协作式请求体流的等待。它不是一个硬性的墙钟时间上界：忽
  略取消 token 的 adapter、provider 或请求流无法被强制终止。
- 信封覆盖的是本 endpoint 读取并持有的原始请求体字节。它不是对客户端在线路上实际发送的字节
  数、连接数、进程内存、每个分配的精确大小或凭据内存清零的承诺。上游组件已消费的请求体、某
  个组件在处理器运行前保留的流或读取器、已预先解析的表单，以及绕过 `Request.Body` 与 body
  pipe 特性的同进程代码，都在 adapter 的支持范围之外。
- 在到达处理器之前就被服务器或代理拒绝的请求，或响应已经开始的请求，不会被改写为固定的
  `400` 或 `503`。
- ServiceMantle 的凭据不回显保证覆盖其自身的解析器、固定响应、投影与诊断。它不覆盖消费方的
  访问器、provider 或身份系统、第三方请求日志，也不覆盖原始请求捕获。
- 回滚覆盖同步或异步登录在抛出异常之前于可写响应 Header 集合中追加或替换的 `Set-Cookie`
  值。它不修复已经开始的响应、不撤销复制到别处的 ticket、不吊销外部 `ITicketStore` 的副作用，
  也不补偿 adapter——adapter 本来就不应写响应。
- 它不防御绕过响应 Header 的同进程代码、并发修改响应的代码，或在失败之后调度自己的回调来签
  发 ticket 的代码。对于拒绝或丢弃恢复操作的 Header 集合，固定的 `503` 不被承诺：该响应会被
  改为中止。
- 登录之前已存在的 cookie 会被保留。更早会话中已排定的正常滑动续期不属于本次登录的 ticket；
  对于被要求登录的请求，cookie 处理器会抑制该续期。
- `X-ServiceMantle-Request` 与默认的 `SameSite=Strict` cookie 是有限的浏览器 CSRF 缓解措施，
  不是 CORS、TLS、来源或代理策略。
