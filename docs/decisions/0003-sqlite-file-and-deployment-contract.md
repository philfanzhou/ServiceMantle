# ADR 0003：SQLite File 请求与显式单实例契约

- 状态：决策已固定，待本 PR 合并；实现由独立 task 交付
- 日期：2026-09-03
- 决策任务：[#231](https://github.com/philfanzhou/ServiceMantle/issues/231)
- 代码基线：`9e574c90b57c279cc05195579ba9a9cd72ac2256`

## 现状与证据

已盘点核心 Bootstrap 请求、准备 SPI/registry/结果及测试、迁移编排与 lease 契约、
AspNetCore 注册、SQLite 包骨架/边界测试、PostgreSQL/MySQL 准备路径、README 及 #59/#60/#112。
现有请求要求非空管理连接；迁移入口缺少部署模式且总是要求真实锁。两处都需要核心契约扩展，
不能由 SQLite Provider 用空密码或假锁绕过。

驱动支持相对路径、URI、内存和临时库，默认打开模式可以创建文件；这些驱动能力不自动成为
ServiceMantle 支持范围。[驱动文档](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings)
说明这些输入形态；[SQLite URI 文档](https://www.sqlite.org/uri.html)说明 URI 参数还可选择 VFS/锁行为。
[SQLite 文件别名风险](https://www.sqlite.org/howtocorrupt.html#multiple_links_to_the_same_file)说明
路径相等不能替代文件身份或 journal 身份验证。以下是本库主动选择的更小支持集合。

## File 请求：不携带管理连接

保留现有 `(target, administrativeConnectionString)` 构造函数的签名、trim 和非空验证。
新增 `DatabaseTargetPreparationRequest.ForFile(target)` 工厂，设置只读的
`AdministrativeConnectionString` 为 `null`（属性改成可空注解），仍只保留一个 Target。
工厂不通过 ProviderId 猜 TargetKind；调用处及 provider 根据已注册的 TargetKind 验证组合。

| 目标种类 | 请求 | 处理 |
| --- | --- | --- |
| File | ForFile，管理连接为 null | 允许进入文件验证 |
| File | 旧构造函数，管理连接非空 | InvalidTarget，零 I/O |
| ServerDatabase / ServerSchema | 旧构造函数，非空管理连接 | 保持既有语义 |
| ServerDatabase / ServerSchema | ForFile | InvalidTarget，零连接尝试 |

没有第二个文件路径，因此不设计“管理连接与目标文件相同”的比较协议。Server provider 必须显式
拒绝 null，不能让驱动把 null 当作默认连接。`ToString`、结果、异常不返回路径或连接串。
准备请求不携带部署模式；部署授权属于下面的独立契约。

## 本地文件输入集合

首版支持 Windows、Linux、macOS 的本地普通文件，使用平台绝对路径。解析使用已固定版本的
`SqliteConnectionStringBuilder`；不改变 Bootstrap 存储的原文。

| 输入 | 固定结论 |
| --- | --- |
| `/srv/app/data.db`（Unix）、`C:\app\data.db`（Windows） | 可进入只读文件验证 |
| `data.db`、`./data.db`、`../data.db`、`C:data.db`、`|DataDirectory|data.db` | InvalidTarget |
| 空 Data Source、`:memory:`、Mode=Memory、`file:...` URI | InvalidTarget |
| UNC、设备路径、Windows ADS、带 NUL 的路径 | InvalidTarget |
| `.` / `..` 路径段、重复分隔符、尾部分隔符 | InvalidTarget；不替调用方规范化别名 |
| 已存在目录、非普通文件、可检测的 symlink/reparse point（任一组件） | InvalidTarget |
| 已存在文件硬链接计数不为 1 | InvalidTarget |
| 无法获取文件类型/链接计数的系统或文件系统 | CapabilityNotSupported；不以文本比较放行 |
| 显式非空 Password、非默认 Vfs、Cache=Shared | InvalidTarget（首版不提供加密/VFS/共享缓存协议） |
| Mode 未指定 / ReadWriteCreate / ReadWrite | 接受配置；Observe 不继承创建模式 |
| Mode=ReadOnly | InvalidTarget，不能作为需准备/迁移的服务目标 |

普通连接选项仍由驱动解析；Observe 使用重新构造的最小只读连接（禁用 pooling，private cache），
不执行配置中的连接初始化 PRAGMA。已存在路径组件必须逐段保留文件系统返回的准确拼写，
Windows 盘符统一大写；大小写差异或 Unicode 不同拼写不折叠为等价身份，无法取得准确拼写则拒绝。
新文件叶名按提交的拼写使用。等价比较只对这一规范路径作 Ordinal 比较，不宣称证明对象相同。
硬链接计数和普通文件检查由可选 SQLite 包的平台适配负责，核心不引入驱动或 native 文件 API。

符号/硬链接不受支持；验证后发生的替换、挂载、网络文件系统伪装与别名竞争不在保证内。
文件系统支持不能仅凭操作系统名称推断。测试必须有真实 symlink/hardlink 拒绝样本，平台探测失败关闭。

## 观察、准备与副作用

Observe 不通过试写判断父目录可写，也不创建 SQLite journal、WAL、SHM 或临时探针文件。
父目录不存在：ServerUnreachable(InvalidTarget)；访问父目录被拒绝：ServerUnreachable(PermissionDenied)。
父目录可观察且叶文件缺失：TargetMissing；此结果不保证将来可创建。
已有文件能以无副作用方式读取 SQLite schema：TargetConnectable；已知存在但无法读取：
TargetUnreachable(PermissionDenied / ConnectionFailed, true)。发现 WAL/SHM/journal 或需要恢复的数据库
时拒绝读取为 TargetUnreachable(TargetConflict, true)，不尝试 recovery。

Prepare 首先执行相同输入验证；存在的普通目标仅观察，返回 AlreadyExists，不打开写连接。
目录/别名冲突返回 TargetConflict 或上述输入错误。缺失目标用同目录临时文件初始化，关闭连接后
以不覆盖的原子移动发布；目标在发布前被创建则保留它并重新观察，绝不覆盖。取消/超时在发布前
清理本调用临时文件；发布是提交点，提交后取消可留下完整数据库，重试得到 AlreadyExists。
不承诺进程被杀死后无临时残留，不删除不属于本调用的文件。权限失败由实际创建操作报告
PermissionDenied，不假定 Observe 能预测 ACL、配额或磁盘空间。错误仅用现有准备错误码。

## 单实例：独立部署契约统一拥有拒绝规则

核心新增 `DatabaseDeploymentMode`（Unspecified、SingleInstance、MultiInstance）、不可变的
provider 能力声明与 registry，以及单一部署验证器。SQLite 声明仅支持 SingleInstance。
模式由消费方显式提供，不从连接串、实例数量或缺少锁推导，不持久化进 Bootstrap 连接信息。

验证器在 Unspecified、多实例请求与 single-only 能力、未登记能力或未定义枚举时关闭失败。
准备入口映射 CapabilityNotSupported；迁移入口映射 LockNotSupported。验证器本身无 I/O。
旧迁移重载保留原有“必须真实锁”的行为；新增接收模式的重载在验证后，SingleInstance 走明确的
无分布式锁编排分支，仍保留检查、执行、最终检查和取消分类；不创建假的 IDatabaseMigrationLock。
MultiInstance 始终要求真实锁；SingleInstance 只对明确声明支持该模式的 provider 放行。
同进程针对同一 provider/规范目标的并发单实例编排必须串行化，不能冒充跨进程互斥。

- 公共部署 task 拥有模式、能力 registry、纯验证器与迁移入口，唯一决定允许/拒绝矩阵。
- #59 只注册 SQLite 的 single-only 声明，实现 File 验证/观察/准备；低层 Prepare 不另造启动 Gate。
- #60 在消费方启动路径中，在 Prepare/迁移/Setup 之前调用同一验证器并接入 EF；不重新定义模式。
- #112 只验收显式 SingleInstance 成功、Unspecified/MultiInstance 副作用前拒绝。
  “第二实例拒绝”只指第二实例请求多实例模式；两个进程都谎称 SingleInstance 不可由此契约检测。

## 拆分与依赖

File 请求与部署模式是两个独立公共契约，分别由 [#264](https://github.com/philfanzhou/ServiceMantle/issues/264)
与 [#265](https://github.com/philfanzhou/ServiceMantle/issues/265) 交付；都 Blocked by #231。
#59 Blocked by 两个公共 task；#60 保持 #59/#114 并显式依赖部署 task；#112 保持 #60/#75。
后续全部保持 blocked，决策 PR 未合并不解除前置。本 PR 不修改产品代码、不新增包。

## 非保证与验收

不保证跨进程/多主机互斥、文件系统对象身份的跨调用稳定性、恶意外部进程、网络/同步盘语义、
驱动外部成本或进程强制终止后的清理。消费方不得以 SingleInstance 作为真实分布式部署配置。

后续 task 必须表驱动覆盖上述每行输入、三种模式、错误码与零副作用拒绝；测试现有 server 构造
语义与所有 server provider 对 ForFile 的拒绝。SQLite 文件用例验证字节不变及目录无新增 sidecar。
本 spike 验证方式是代码/契约盘点、决策与 issue 逐项对应、原生依赖回读及 Markdown diff 检查。
新增邻近问题：无；这里明确修订 #59 的可写性预测和 #112 的第二实例表述，不增加运行时保证。
