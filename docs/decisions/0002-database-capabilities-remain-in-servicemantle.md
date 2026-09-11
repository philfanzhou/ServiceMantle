# ADR 0002：数据库能力保留在 ServiceMantle 中

- 状态：已接受
- 日期：2026-08-30
- 决策 issue：[#179](https://github.com/philfanzhou/ServiceMantle/issues/179)
- 重新评估证据：[#65](https://github.com/philfanzhou/ServiceMantle/issues/65)、[PR #206](https://github.com/philfanzhou/ServiceMantle/pull/206)

## 背景

ServiceMantle 和 [CoddLoom](https://github.com/philfanzhou/CoddLoom) 都有
database-provider 包。这种重叠可能让人以为应该把 ServiceMantle 的数据库能力移入
CoddLoom 并共享其 provider 矩阵。

实际的重叠比包名所暗示的要窄。ServiceMantle 有四项彼此不同的数据库职责：

1. 观察已配置的目标，并显式准备缺失的目标；
2. 使用 provider 特定的锁协调 migration；
3. 在消费方拥有的数据库中持久化 ServiceMantle 自有记录；以及
4. 验证 Bootstrap 数据库配置。

只有第三项职责属于 ORM 集成范畴。其余三项运作在连接、服务器或编排边界上，
CoddLoom 的 ORM 抽象并不为这些边界建模。

## 决策

ServiceMantle 将这些能力保留在本仓库中。每个可选 provider 包直接引用其 ADO.NET
驱动。provider 无关的契约保留在核心包中，而驱动特定的实现保留在对应的可选
provider 包中。

现有的 EF Core 持久化包仍然是一个可选适配器。它不会被 CoddLoom 取代，核心包
仍然独立于两个 ORM。

### 目标观察、准备与 Bootstrap 验证

`IDatabaseTargetPreparationProvider` 和 `IBootstrapDatabaseProvider` 是异步的，
每个操作都接收一个 `CancellationToken`。观察必须把连接失败转化为安全的结构化结果，
而不是放任打开连接的异常逃逸。准备也带有 ORM 表访问层之外的 provider 特定职责：

- 解析并验证目标与管理连接信息；
- 区分服务器、认证、权限、目标存在性与目标冲突等结果；
- 在 provider 要求时，把特权连接与连接池和消费方环境事务隔离；
- 为目标创建 DDL 安全地引用 provider 标识符；以及
- 对已存在目标保持非破坏性规则。

在所测量的修订版本上，CoddLoom 的 `DbExecutor` 是同步的，并在其构造函数中打开
连接。因此它无法提供 ServiceMantle 的异步取消和安全观察边界。它的 builder 在已经
选定的数据库内部对表进行操作，不能替代服务器级的目标准备。

### Migration 锁

`IDatabaseMigrationLockProvider` 以有界超时和调用方取消的方式异步获取 provider
特定的租约。所需的原语是 provider 特定的，例如 PostgreSQL 会话 advisory lock、
SQL Server 应用程序锁、MySQL 命名锁或 Oracle `DBMS_LOCK`。这些原语在命名、生命
周期、超时、取消和失败映射上各不相同。

CoddLoom 没有 migration 锁契约。适配其同步执行器既不能消除 provider 特定的锁
逻辑，还会让取消依赖阻塞工作线程。因此 ServiceMantle 把锁 provider 保留在其
migration 编排契约旁边。

### 持久化

`ServiceMantle.Persistence.EntityFrameworkCore` 的存在是为了让使用 EF Core 的
消费服务能把 ServiceMantle 的映射加入自己的 `DbContext`，并生成一份 migration
历史。消费方保留对该 `DbContext`、事务和 migration 执行的所有权；适配器操作按照
其公开契约声明的方式保存或仅暂存更改。ServiceMantle 核心暴露 provider 无关的
持久化契约；EF Core 是适配器而非核心依赖。

用 CoddLoom 替换该适配器会让一个 EF Core 消费方在同一个业务数据库上拥有两套
schema 与持久化栈。它还会破坏预期的集成模型——消费方拥有共享的 `DbContext` 及其
migration。如果有真实消费方需要，未来可以在 EF Core 适配器旁边增加一个 CoddLoom
持久化适配器。

### 包边界

本决策保持以下依赖方向：

- `ServiceMantle` 包含 provider 与 ORM 无关的契约；
- `ServiceMantle.AspNetCore` 包含托管与注册集成，不依赖数据库驱动；
- 每个 `ServiceMantle.Database.*` 包引用核心包及其 ADO.NET 驱动；以及
- 每个 `ServiceMantle.Persistence.*` 包是针对一种持久化栈的可选适配器。

仅仅为了共享少量 provider 特定的连接字符串或标识符辅助代码，不会把 CoddLoom
加入包依赖图。

在所测量的修订版本上，CoddLoom 核心以 `netstandard2.0` 为目标，其 Oracle 包以
`netstandard2.1` 为目标，而 ServiceMantle 以 .NET 10 为目标。把 ServiceMantle
契约下移，要么会缩减其目标框架与 API 面，要么会让 CoddLoom 背上由下游驱动的多
目标框架要求。以所测量的复用程度，这两种交换都不成立。

## 实测重新评估

issue #65 中的 Oracle provider 决策检验了“CoddLoom 只能消除很少 provider 代码”
这一估计。检查使用 CoddLoom commit
[`f23f611`](https://github.com/philfanzhou/CoddLoom/tree/f23f611cda9afaa0eef12cf644af75c3e916de9f)。

在该修订版本上：

- `OracleExecutor` 接受完整的调用方提供的连接字符串，并委托给同步的
  `DbExecutor`；
- `OracleBuilder` 根据调用方提供的标识符生成 ORM 表 SQL，不暴露可复用的 Oracle
  标识符引用契约；
- Oracle 包不提供连接字符串 builder 抽象；以及
- CoddLoom 不提供 migration 锁抽象。

一个 Oracle ServiceMantle provider 仍然需要直接使用 ODP.NET 来完成连接字符串解析、
身份验证、安全的管理 DDL、Oracle 错误分类、特权连接隔离和 `DBMS_LOCK`。引入
CoddLoom 会保留这些工作，同时额外带来一个 ORM 依赖。

这是 #179 要求的第一份非 PostgreSQL provider 实测结果。它确认了本决策，因此
不需要开启依赖迁移 issue。

## 影响

- provider 包可能重复少量连接字符串和标识符处理。这种重复是被接受的，因为安全
  规则和失败分类本来就是 provider 特定的。
- ServiceMantle 无需适配同步 ORM 执行器即可保持异步取消和安全的结构化失败。
- ServiceMantle 的服务器创建和 migration 锁特权不会加入 CoddLoom 的依赖图。
- EF Core 消费方继续拥有一个 `DbContext`、一份 migration 历史和自己的事务边界。
- 增加另一个 provider 需要新的可选 provider 包及其直接驱动依赖；不改变核心包
  边界。

## 明确非保证

- 本决策不声称每个未来的 provider 直接实现总是更便宜。它记录的是当前架构和一
  份 Oracle 实测结果。
- 它不评估 CoddLoom 的实现质量、正确性或性能。
- 它不禁止未来的 `ServiceMantle.Persistence.CoddLoom` 适配器。这样的适配器将是
  增量式、由消费方驱动的，而不是现有能力的搬迁。
- 它不保证 EF Core 适合每个未来的消费方。当前的 EF 适配器保持可选。
- 它不在 ServiceMantle 现有公开契约之外，跨 provider 标准化连接字符串解析、
  标识符引用、错误分类或锁语义。
- 它不移动目标准备、migration 锁、持久化或 Bootstrap 验证代码，也不改变任何
  运行时行为。

## 重新评估触发条件

出现以下任一情况时，重新开启 #179，而不是做 provider 局部的架构例外：

- CoddLoom 提供异步执行，且不再在执行器构造函数中打开连接；
- 某个不使用 EF Core 的 ServiceMantle 消费方需要共享持久化集成；
- 某个已实现的 provider 表明共享的连接字符串、标识符或能力处理的规模明显大于
  Oracle 实测结果；或
- ServiceMantle 需要一个 CoddLoom 已覆盖的 provider，且实测实现成本明显高于
  跨仓库依赖成本。
