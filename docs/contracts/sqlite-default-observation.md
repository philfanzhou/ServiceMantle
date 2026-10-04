# SQLite 默认观察的 WAL 拒绝边界

默认 InspectAsync 在路径与 sidecar 元数据验证之后，用普通只读 FileStream 读取至多100字节，
完整释放后分类。合法 magic 的 read/write 版本仅1/1进入原只读 schema SELECT，WAL2/*、*/2或未知版本
在任何 SQLite connection.Open 前返回 TargetConflict；短文件/错误magic 为 ConnectionFailed，合法空文件
保持既有 connectable 行为。Header 只用于分类，不静态验证整个数据库。

Observe 返回已知存在的 TargetUnreachable；Prepare-existing、publish-winner 返回原 TargetConflict；
Bootstrap Validate 返回 database.connection_failed。四入口共同默认检查，不创建/删除 probe、journal/WAL/SHM。
Prepare 失败只清理本调用temp，保留并发发布者完整数据；清理后仍检查caller取消与内部timeout。
正常、异常、header/schema 资源释放、公开完成出口均观察caller token，安全 OCE 原token/无secret inner。

显式恢复依旧只在已有安全 sidecar 上checkpoint，再用专用 immutable observation；
清理后的 WAL header 不受默认拒绝误拦。enabled但没有sidecar仍走默认拒绝，不暗启恢复。
不承诺默认 WAL connectable、完整静态校验、外部恶意替换、network FS、并发外部DDL或跨进程排他。
调用方需选择已有恢复边界并负责部署/备份/外部协调；撤销本修复会恢复已知sidecar副作用，不是无风险回滚。
