# 服务器-数据库准备身份契约

PostgreSQL、MySQL、MariaDB 和 SQL Server 的准备 provider 在接受已存在的目标或发出
`CREATE DATABASE` 之前，验证管理会话与目标 endpoint 到达同一台服务器。这实现了
`IDatabaseTargetPreparationProvider.PrepareAsync` 上与 provider 无关的要求，且不向核心包
添加驱动引用，也不引入持久的服务器身份注册表。

## 证据与会话所有权

管理连接使用临时会话锁创建一个全新的随机 128 位质询。使用**目标凭据和 endpoint** 的另一个
独立连接必须在同一服务器锁命名空间中观察到该质询。连接字符串文本、主机名、端口或用户名都不
用作服务器相等性的证据。创建探测继续在**同一个未池化的管理物理会话**上进行；它绝不先验证
一个连接然后另开一个连接去创建数据库。该质询不是迁移锁，也不会将准备调用串行化。

| Provider | 共享命名空间 | 必需的证据 |
| --- | --- | --- |
| PostgreSQL | 管理连接的维护数据库；未指定时为 `postgres` | `pg_catalog.pg_locks` 中的两个随机 64 位 advisory 锁，带有管理后端 PID、排他的已授予模式和当前数据库 OID |
| MySQL / MariaDB | 清空 `Database` 的服务器级连接 | 一个随机命名的 `GET_LOCK`，其 `IS_USED_LOCK` 所有者等于管理连接 ID |
| SQL Server | `master`，principal 为 `public` | 一个全新的 `APPLOCK_TEST` 在管理员会话 `sp_getapplock` 之前可授予，之后不可授予 |

所有质询 SQL 都使用参数和较短的命令超时。两个连接都禁用池化和环境事务参与。SQL Server
禁用连接重试。目标证明连接在验证后被释放；管理会话保留其质询，直到创建探测现有的 `finally`
将其关闭。在失败、取消或超时时，两个会话都被清理，且不运行任何后续创建。释放错误不会暴露
底层异常，也不会替换安全的外层结果。

## 缺失目标与受限账户

验证使用维护命名空间，因此它不需要连接到缺失的目标数据库。但它确实需要在那里认证目标凭据。
错误的目标密码不能用管理凭据替代。PostgreSQL 目标登录需要在所选维护数据库上有 `CONNECT`
权限以及对 `pg_catalog.pg_locks` 的可见性；SQL Server 目标登录需要访问 `master` 及其公开的
application-lock 函数。MySQL 和 MariaDB 使用公开的命名锁函数，不要求目标在
`INFORMATION_SCHEMA.SCHEMATA` 中可见。

缺失的目标和不可访问的目标绝不被当作服务器身份的证明。如果认证、权限或连接性阻止了验证，
准备以失败关闭，返回相应的现有安全错误码。已完成但为否定或不支持的证明返回
`database_target_preparation.invalid_target`。调用方取消优先于返回的拒绝或超时，并被净化。
任何结果或诊断都不包含目标/管理凭据、endpoint 身份、质询值或驱动错误。

创建过程不会创建登录、不会修复授权，也不会使目标凭据能够使用新创建的数据库。消费方在准备
之后仍然自行观察/连接目标。现有的标识符、所有权、排序规则、并发和非破坏性创建规则在验证
之后继续适用。

## 信任与路由边界

消费方必须按照自己的 TLS/凭据策略认证并信任两个 endpoint。受支持的拓扑是一台稳定的单一
服务器，可直接或通过别名/代理访问，该代理的部署契约独立于数据库和用户将会话固定到该服务器。
不同的合法别名都可能成功。托管服务只有在提供这些路由和维护命名空间保证并暴露证明操作时
才可使用。

显式多主机列表、Npgsql 多路复用、SQL Server 只读 application intent 和 SQL Server 故障转移
伙伴都被拒绝。PostgreSQL 恢复节点以及报告 `read_only` 的 MySQL/MariaDB 服务器也会拒绝证明。
管理会话丢失不允许重新打开连接或重放创建；重试需要一次新的准备调用和全新的验证。

本契约**不**发现隐藏的代理策略，也不支持数据库/用户/语句路由、跨区域路由、透明会话替换或
集群故障转移。在这些路由保证未知时不要调用准备。它不认证伪造协议/锁响应的不可信服务器，
不防御恶意服务器管理员，也不为之后的应用连接提供永久的身份/隔离保证。随机质询避免意外的
别名混淆；它们不能替代服务器认证。创建之后丢失的 DDL 确认不会被此验证步骤回滚。

## 数据库参考

- [PostgreSQL advisory 锁表示与数据库作用域](https://www.postgresql.org/docs/18/view-pg-locks.html)
- [MySQL 命名锁与连接所有权](https://dev.mysql.com/doc/refman/8.4/en/locking-functions.html)
- [MariaDB IS_USED_LOCK](https://mariadb.com/docs/server/reference/sql-functions/secondary-functions/miscellaneous-functions/is_used_lock)
- [SQL Server APPLOCK_TEST 与数据库作用域](https://learn.microsoft.com/en-us/sql/t-sql/functions/applock-test-transact-sql)
