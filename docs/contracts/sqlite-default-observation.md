# SQLite 默认观察的 WAL 拒绝边界

默认 InspectAsync 在路径与 sidecar 元数据验证之后，用普通只读 FileStream 读取至多100字节，
完整释放后分类。合法 magic 的 read/write 版本仅1/1进入原只读 schema SELECT，WAL2/*、*/2或未知版本
在任何 SQLite connection.Open 前返回 TargetConflict；短文件/错误magic 为 ConnectionFailed，合法空文件
保持既有 connectable 行为。Header 只用于分类，不静态验证整个数据库。

Observe 返回已知存在的 TargetUnreachable；Prepare-existing、publish-winner 返回原 TargetConflict；
Bootstrap Validate 返回 database.connection_failed。四入口共同默认检查，不创建/删除 probe、journal/WAL/SHM。
Prepare 失败只清理本调用temp，保留并发发布者完整数据；清理后仍检查caller取消与内部timeout。
正常、异常、header/schema 资源释放、公开完成出口均观察caller token，安全 OCE 原token/无secret inner。

显式恢复覆盖两种形态：已有安全 sidecar 的恢复（在 sidecar 上 checkpoint 后用专用 immutable observation，
清理后的 WAL header 不受默认拒绝误拦），与 #671 起定稿的 clean-WAL 接入——`enabled` 即对 WAL 目标一次性写入的
知情声明：header 判定干净 WAL（目标存在、无 sidecar、read/write 版本 `2/2`）时，先做与 sidecar 恢复等价的
资格校验（普通文件、硬链接数 1、无符号链接、读写权限），再走同一 `RecoverAndObserveAsync` 管线：一次
`PRAGMA wal_checkpoint(TRUNCATE)`（自有非池化 RW 连接，受 `RecoveryTimeout` 约束）→ 连接释放后确认无
sidecar 残留 → 一次 immutable 只读观察 → `TargetConnectable`。接入窗口内目录可能短暂出现 `-wal`/`-shm`
（不承诺对并发观察者隐藏）；busy/资格校验不过 → `target_conflict`；checkpoint 失败 → `preparation_failed`；
超预算 → `timeout`。默认路径（未 opt-in）对一切干净 WAL 目标仍为零 SQLite 打开的 `target_conflict` 拒绝，
不暗启任何写入。
不承诺默认 WAL connectable、完整静态校验、外部恶意替换、network FS、并发外部DDL或跨进程排他。
调用方需选择已有恢复边界并负责部署/备份/外部协调；撤销本修复会恢复已知sidecar副作用，不是无风险回滚。

已知行为演进（随版本说明发布）：对已使用 `enabled=true` 的既有消费者，干净 WAL 目标从 `target_conflict`
变为按上述管线接入——这是 #671 的声明目的，也是与此前「enabled但没有sidecar仍走默认拒绝」定稿的唯一差异。
