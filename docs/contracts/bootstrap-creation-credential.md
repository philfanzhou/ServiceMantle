# 一次性 Bootstrap 创建凭据契约

Bootstrap 创建凭据恰好授权一次匿名的首次 Bootstrap 创建。它是核心
`ServiceMantle.Bootstrap` 能力，不依赖 ASP.NET Core、EF Core 或任何数据库 provider，并且它
不是 Setup Code、不是管理 cookie、不是数据库凭据、也不是 Bootstrap MasterKey。

本文档定义凭据的值、磁盘上的记录以及一次性消费边界。它不定义 HTTP endpoint：使用该凭据的解
析器、phase gate、速率限制、候选值验证与 Bootstrap 文件写入属于 Bootstrap endpoint 任务。

## 凭据

| 属性 | 值 |
| --- | --- |
| 熵 | 来自 BCL 加密随机数生成器的 32 字节 |
| 编码 | 43 字符无填充 Base64URL，`[A-Za-z0-9_-]` |
| 匹配 | 区分大小写，绝不修剪或规范化 |
| 明文暴露 | 仅返回一次，通过 `BootstrapCredentialProvisionResult.Credential.Reveal()` |
| 默认生存期 | 15 分钟；可配置范围为 1 到 60 分钟 |
| 时钟 | store 的 `TimeProvider` |

`BootstrapCredential` 实现 `ISensitiveLogValue`。它的 `ToString()`、调试器显示、结果投影、
状态投影、摘要投影以及记录文件名都绝不包含明文。摘要是 `sha256-v1:` 后跟凭据精确 UTF-8 字节
的 64 个小写十六进制字符；由于长度始终相同，`CryptographicOperations.FixedTimeEquals` 比较
的是两个完整长度的操作数，绝不会因长度差异而提前结束。

ServiceMantle 只保证其自身的持久化、异常、结果与诊断绝不回显明文。它不清理进程内存，也不对调
用方、调试器或第三方请求日志如何处理已揭示的凭据作任何承诺。

## 磁盘上的记录

```json
{
  "formatVersion": 1,
  "digest": "sha256-v1:…",
  "issuedAtUtc": "2026-09-06T12:00:00.0000000Z",
  "expiresAtUtc": "2026-09-06T12:15:00.0000000Z"
}
```

默认路径是
`<AppContext.BaseDirectory>/config/<normalized-service-id>.bootstrap-credential.json`；也可
提供显式路径。在 Unix 上，store 创建的目录为 `0700`，记录为 `0600`；在 Windows 上，记录使用
由当前用户创建的文件的系统 ACL。跨平台的文件所有者与符号链接策略明确不属于本契约。

记录协议是一组封闭的可测试规则。原始文件超过 4 KiB、JSON 嵌套超过 4 层、根不是对象、未知成
员、重复成员、缺失成员、`formatVersion` 不是 `1` 或被写成字符串、未知的摘要版本或格式错误的
摘要、无法往返（round-trip）的 UTC 时间戳，或者过期时间早于或等于签发时间，全部失败关闭为
`bootstrap_credential.unavailable`。损坏的记录绝不会被修复，也绝不会变得可消费。带版本的摘要
编码是摘要到达磁盘的唯一位置。

## 预配

`ProvisionAsync` 是一个显式的本地运维动作。没有任何 ServiceMantle 管理 endpoint 会签发或轮换
凭据。

| 条件 | 结果 |
| --- | --- |
| 无记录，且一次打开证明了 Bootstrap 文件不存在 | `Provisioned`，附带明文、签发时间与过期时间 |
| 记录已存在 | `bootstrap_credential.already_exists` |
| 一次打开证明了 Bootstrap 文件存在 | `bootstrap_credential.bootstrap_configured` |
| 无法确定 Bootstrap 文件是否存在 | `bootstrap_credential.unavailable` |
| 访问被拒绝、I/O 失败 | `bootstrap_credential.unavailable` |

记录以独占创建方式建立，因此在本进程或任何其他进程中，操作系统只放行恰好一个写入者，每个失败
者都会看到不含明文的 `already_exists`。

### 存在性是证据，而不是否定检查

只有被证明的不存在才授权签发。Bootstrap 文件是否存在，由对它的一次只读打开的答案决定：操作系
统报告文件或其目录未找到是不存在的证明，打开成功是存在的证明，而被拒绝或失败的打开两者都不能
证明。`File.Exists` 的否定结果无法作出这一区分——对于调用方无法遍历的目录，它会对实际存在的文
件返回 `false`——因此存在性未被确定时，绝不签发明文，也绝不创建凭据记录。

该打开只取得读取访问权，共享读、写与删除，并且不读取任何字节：Bootstrap 文件的连接字符串与
MasterKey 绝不会被它读取、返回或修改，文件保持原样。

`GetStatusAsync` 同样关闭这一失败路径。当存在性无法确定时，观测结果为 `Unavailable`，
`BootstrapConfigured` 为 false，两个时间戳均为 null，并且完全不读取凭据记录。因此
`BootstrapConfigured` 是*被证明的存在性*的投影：在 `Unavailable` 观测结果上，`false` 值不是
Bootstrap 文件缺失的证据，调用方既不得据此预配，也不得把服务报告为确定未配置。在其他所有状态
上，它保持既有含义。

该证据只覆盖那次打开的时刻。外部进程可能在其后立即创建、替换或删除该文件，并且预配与
Bootstrap 发布之间不提供任何互斥。

## 消费

`ConsumeAsync` 先验证、后认领：

1. 按上述文件协议读取并解析记录；
2. 解析候选值，可解析的候选值与存储的摘要进行固定时间比较——无效候选值绝不会消费有效凭据；
3. 只有匹配且未过期的候选值通过跨进程的原子重命名认领记录；重命名竞争的失败者发现无记录可认
   领；
4. 被认领的记录会被读回并重新检查，只有内容仍然匹配的认领才会成功。

过期、格式错误、不匹配、已消费和从未预配全部收敛为 `bootstrap_credential.invalid`，因此调用
方无法区分它们。损坏、超大、访问拒绝与 I/O 失败为 `bootstrap_credential.unavailable`。在读取
与认领之间被替换的记录会失败关闭：认领不会被恢复，运维人员需预配一个新凭据。

消费是单向的。拥有 Bootstrap 创建的 endpoint 在调用 `BootstrapConfigurationManager.CreateAsync`
**之前**消费凭据，因此之后的验证、写入、响应或进程失败都会使凭据保持已消费状态。

## 认领期间的记录共享

对记录的每次读取——初始读取、状态读取以及对被认领记录的重新检查——都经过同一个位置，只读，共
享读与删除。共享删除正是认领所需要的：认领会重命名记录，而在 Windows 上这要求对记录持有
`DELETE` 访问权，且两个句柄只有在各自的共享模式覆盖对方被授予的权限时才彼此兼容。缺少它，读
取者与并发认领会互相拒绝，而一次被拒绝的读取会对仅仅是错误的候选值报告
`bootstrap_credential.unavailable`。Unix 的重命名从不查询已打开的句柄，因此该规则没有 Unix
对应物。

该标志只放宽*其他*句柄被允许请求的内容。读取句柄仍然只有读取访问权、别无其他，没有文件权限被
放宽，读取者在认领重命名记录之后仍继续观察它打开的那条记录。

共享只解决了一个方向。认领自身的句柄是另一个方向：重命名进行期间，它持有记录且不共享读，竞争
失败者得到的告知方式，本 store 尚无法与存储失败区分。因此在 Windows 上的竞争下，认领失败的有效
候选值仍可能报告 `bootstrap_credential.unavailable`，而真实答案应是
`bootstrap_credential.invalid`。这一残留缺口由单独的任务跟踪；此处没有任何内容重新分类它，任
何调用方都不得把 `unavailable` 读作关于候选值的陈述。

这只消除了 store 自身的读取句柄与自身认领之间的不兼容，且仅在权限可用的正常本地文件系统上。
它不承诺在面对外部独占句柄、杀毒软件、任意 ACL 或文件系统、被杀死的进程或并发本地重新预配时
消费成功，也不会把存储失败变成 `bootstrap_credential.invalid`：损坏、超大、访问拒绝与 I/O 失
败仍然是 `bootstrap_credential.unavailable`。

## 先消费的窗口

消费凭据与发布 Bootstrap 文件是两个文件，不是一个原子事务。本契约选择失败关闭的顺序，并且不
自动补偿：

- 在消费与 Bootstrap 发布之间崩溃，可能既没有留下可用凭据，也没有留下 Bootstrap 文件。
  `GetStatusAsync` 使该状态可诊断——它报告 `NotProvisioned`、`Provisioned`、`Expired` 或
  `Unavailable`，以及 Bootstrap 文件是否存在，不含明文与摘要——本地运维必须显式预配一个新凭
  据；
- 已成功写入但响应丢失的 Bootstrap 文件通过状态 endpoint 恢复：重试凭据会失败，而状态会报告
  Bootstrap 已配置。

## 取消

调用方自己的 token 在每个操作边界被检查，并且原样向上传播。文件调用本身是同步的：已经进入文
件调用的操作无法被强制中断，也不为其承诺墙钟时间上界。

## 明确的不保证

- 凭据消费与 Bootstrap 文件发布在两个文件之间不是原子的。
- 不提供分布式凭据、远程签发、加密硬件、进程内内存清理或外部日志保密性保证。
- 跨平台的文件所有者与符号链接策略不属于本契约。
- 熵的主张基于使用 BCL 加密随机数生成器的 32 字节以及由此得出的编码，而不基于对生成值的统计
  观察。
