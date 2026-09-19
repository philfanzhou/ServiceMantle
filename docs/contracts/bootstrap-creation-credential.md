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

「预配」一节的存在性证据规则同样约束重新签发；两节的错误码与语义见上文各表。

记录以独占创建方式建立，因此在本进程或任何其他进程中，操作系统只放行恰好一个写入者，每个失败
者都会看到不含明文的 `already_exists`。

## 重新签发

`IBootstrapCredentialReissuer.ReissueAsync`（由 `BootstrapCredentialFileStore` 实现）是 store 拥有
的恢复动作：为每次进入 Bootstrap 模式的进程打印一个新凭据，或在凭据未消费而过期、进程重启后取
回一个新明文，而不需要手工操作记录文件。与预配一致，它是显式的本地运维动作，没有任何管理
endpoint 提供重新签发。

| 条件 | 结果 |
| --- | --- |
| 证明 Bootstrap 文件存在 | `bootstrap_credential.bootstrap_configured`，记录不变 |
| 无法确定 Bootstrap 文件是否存在 | `bootstrap_credential.unavailable`，不读取记录 |
| 证明不存在，无记录（含已消费） | 同 `ProvisionAsync`：`Provisioned`；竞争失败者 `already_exists` |
| 证明不存在，记录可解析（未过期或已过期） | `Provisioned`，新明文；记录被原子替换，旧明文立即失效 |
| 证明不存在，记录读取失败、超大、损坏或协议非法 | `bootstrap_credential.unavailable`，原记录逐字节不变 |
| 暂存写入或替换移动失败 | `bootstrap_credential.unavailable`，旧记录不变，暂存文件已删除 |
| 写入前调用方已取消 | 抛出携带调用方 token 的取消，无任何文件改动 |

替换是原子的：新记录先以独占创建写入同目录的随机暂存文件（Unix 权限 `0600`，与记录一致），
`Flush(flushToDisk: true)` 后以 `File.Move(staging, FilePath, overwrite: true)` 覆盖（Windows 上为
`MoveFileExW` + `MOVEFILE_REPLACE_EXISTING`，Unix 上为 `rename(2)`）。记录路径上任何时刻只有旧
记录或完整的新记录。明文只出现在返回值中；旧明文的校验与消费都因摘要不匹配而得到
`bootstrap_credential.invalid`，不需要新的判定路径。调用方取消只在写入之前的边界被检查（入口、
存在性探测后、记录解析后、替换开始前）；替换一旦完成，结果照常返回，不再检查取消，因为返回值是
新明文唯一的副本。

不保证：与并发 `ConsumeAsync` 或并发 `ReissueAsync` 之间的结果顺序。并发重新签发时只有最后完成
替换的那个明文有效，较早返回的明文可能已失效；进行中的消费若在替换之后才完成认领，会认领到新
记录、复核摘要不匹配而返回 `invalid`，并使新记录被消费掉，此时需要再次重新签发。在写入窗口内
被独占创建句柄拒绝读取的并发调用按上表「读取失败」行报告 `unavailable`（共享语义由运行时与平
台决定：Windows 与 .NET 10 的 macOS 强制共享模式，Linux 不强制）。对外部独占句柄、杀毒软件、任
意 ACL、被杀死进程的行为沿用 store 既有非保证；掉电持久性不超出 `flushToDisk` 的语义；损坏的记
录不会被修复。

调用方责任：只在确认没有进行中的创建请求时调用（典型用法是 Bootstrap 模式宿主启动、开始接收
请求之前）；通过安全通道输出明文；损坏的记录由运维人工处理。

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
4. 被认领的记录会在一个不共享删除的只读句柄上读回并重新检查，只有内容仍然匹配的认领才会成
   功；该句柄的排他性依据见「认领期间的记录共享」。

过期、格式错误、不匹配、已消费和从未预配全部收敛为 `bootstrap_credential.invalid`，因此调用
方无法区分它们。损坏、超大、访问拒绝与 I/O 失败为 `bootstrap_credential.unavailable`。竞争期间
被在途认领拒绝的读取与重命名按「认领期间的记录共享」重新观察：记录已不在的失败者得到
`invalid`，拒绝仍无法解释时保持 `unavailable`。在读取与认领之间被替换的记录会失败关闭：认领不
会被恢复，运维人员需预配一个新凭据。

消费是单向的。拥有 Bootstrap 创建的 endpoint 在调用 `BootstrapConfigurationManager.CreateAsync`
**之前**消费凭据，因此之后的验证、写入、响应或进程失败都会使凭据保持已消费状态。

## 不消费的校验

`IBootstrapCredentialVerifier.VerifyAsync`（由 `BootstrapCredentialFileStore` 实现）提供一个不改
变任何持久状态的候选校验：不认领、不删除、不重写记录，不延长有效期，不写入任何文件。可观察的
唯一效果是返回值。

- **分类与消费收敛为同一个封闭集合，且不比消费泄漏更多**：过期、格式错误、不匹配、已消费、从
  未预配全部收敛为 `bootstrap_credential.invalid`，四者不可区分；损坏、超大、访问拒绝与 I/O 失
  败为 `bootstrap_credential.unavailable`。可解析候选值与存储摘要的比较沿用消费路径的固定时间比
  较实现，不引入第二套比较逻辑；记录读取继续经由同一个 `OpenRecordForRead` 打开点。
- **校验通过不构成授权**：它只表示「此刻这个候选值可解析、匹配且未过期」。两次校验之间、以及
  校验与消费之间没有任何保留、租约或顺序保证；记录可能在下一次观察前被其他进程认领、过期或被
  替换。「谁是唯一成功的消费者」仍然只由 `ConsumeAsync` 的原子认领决定。

调用方责任：为暴露校验的入口自行提供限流与最小披露；不把校验结果当作后续写操作的授权。本契约
不引入校验次数限制、锁定或退避——限流是消费方入口的责任。

不保证：校验通过之后的消费一定成功；两次调用之间的一致性；对外部独占句柄、杀毒软件、任意 ACL、
被杀死进程或并发重新预配的行为（沿用 store 既有非保证）；抵抗对该入口的暴力尝试；同步且不协作
的文件 I/O 有硬性时间界限。调用方取消在每个操作边界被检查并原样传播。

## 认领期间的记录共享

对记录的每次读取——初始读取、状态读取以及对被认领记录的重新检查——都经过同一个位置，只读，共
享读与删除。共享删除正是认领所需要的：认领会重命名记录，而在 Windows 上这要求对记录持有
`DELETE` 访问权，且两个句柄只有在各自的共享模式覆盖对方被授予的权限时才彼此兼容。缺少它，读
取者与并发认领会互相拒绝，而一次被拒绝的读取会对仅仅是错误的候选值报告
`bootstrap_credential.unavailable`。Unix 的重命名从不查询已打开的句柄，因此该规则没有 Unix
对应物。

该标志只放宽*其他*句柄被允许请求的内容。读取句柄仍然只有读取访问权、别无其他，没有文件权限被
放宽，读取者在认领重命名记录之后仍继续观察它打开的那条记录。

竞争期间被拒绝的观察按证据而非猜测分类，三个方向都已关闭：

- **读取方向**。重命名在途的一瞬，认领自身的句柄持有记录且不共享读，任何按名的打开都会被拒，
  无论读者申明什么共享模式。这种拒绝不是存储故障的证据：被共享冲突拒绝的打开会有界地重新观察
  （每次 1 毫秒、至多 32 次，总计几十毫秒；这是重试，不构成时间上界承诺）。认领要么落地——下
  一次打开随即报告记录不存在，这是真实答案，也是调用方普通的 `invalid` 结果——要么拒绝在重新
  观察后仍然存在，原样报告为 `unavailable`。损坏、超大与真正不可访问的记录不会被重新分类。
- **认领方向**。认领的重命名被拒时（Windows 把同一记录上已在途的认领报告为访问拒绝，而非不存
  在），记录经同一读取边界重新观察：已不在 → 被别人认领 → `invalid`（真实答案）；仍可读，或
  重新观察本身又被拒 → `unavailable`。
- **复核方向（排他性）**。认领的重命名落地后，被认领的记录在一个**不共享删除**的只读句柄
  （claim-guard）上复核。guard 打开成功本身就证明「此刻不存在被授予 `DELETE` 的句柄」，持有期
  间它拒绝一切新的 `DELETE` 打开；而认领文件名含不可预测的随机成分，任何其他调用都无法按名字
  打开它。因此在 Windows 上，唯一还能移动该文件对象的途径——一个在本调用改名落地之前就已打
  开、被授予 `DELETE` 的陈旧句柄——恰恰使 guard 打开被共享冲突拒绝：该调用得到非 Consumed，
  且认领文件**不由本调用删除**（持续的共享冲突证明另一个认领者仍持有该文件对象，删除可能销毁
  它唯一的一次成功）；若陈旧句柄的改名已先落地，guard 打开得到不存在 → `invalid`。任何交错下至
  多一个调用返回 Consumed。guard 只请求读取访问，不放宽任何文件权限；guard 失败时留下的孤儿认
  领文件已在凭据路径之外，任何调用都无法再消费，与 `TryDelete` 失败时的既有理由一致。

这只关闭了 store 自身读取与认领之间的不兼容，且仅在权限可用的正常本地文件系统上。它不承诺在面对
外部独占句柄、杀毒软件、任意 ACL 或文件系统、被杀死的进程或并发本地重新预配时消费成功——guard
打开被外部句柄持续拒绝时，本调用报告 `unavailable`，这正落在该非保证内；有界重试不是时间承诺。
它也不会把存储失败变成 `bootstrap_credential.invalid`：损坏、超大、访问拒绝与非竞争 I/O 故障
仍然是 `bootstrap_credential.unavailable`。Unix 上共享删除被运行时掩去、重命名从不查询已打开的
句柄，本节的 Windows 机制在 Unix 上是惰性的，跨进程一次性语义逐字不变。

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
- 重新签发不保证与并发消费或并发重新签发之间的结果顺序（见「重新签发」一节）；也不保证对外部
  独占句柄、杀毒软件、任意 ACL、被杀死进程的行为，或超出 `flushToDisk` 语义的掉电持久性。
