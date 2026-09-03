# ADR 0004：MySQL 产品身份与只读探测

- 日期：2026-09-03；状态：决策固定，待 PR 合并，尚未实现
- 决策：[#236](https://github.com/philfanzhou/ServiceMantle/issues/236)
- 实现：[#221](https://github.com/philfanzhou/ServiceMantle/issues/221)
- 基线：`9e574c90b57c279cc05195579ba9a9cd72ac2256`

## 证据与能力边界

已阅读 MySQL Bootstrap、目标解析、观察/准备与注册测试、MariaDB 对照实现及产品判断、
核心准备 SPI/registry/安全错误码、README、包登记与 #221/#62/#223。
当前 MySQL 探测只验证 DATABASE()，MariaDB 则用 VERSION() 的正向产品标记；否定后者不能证明前者。

| 信号 | 获取与限制 |
| --- | --- |
| 协议握手版本 H | 连接成功后读取驱动公开 ServerVersion；不读取私有成员或自行解析协议包 |
| VERSION() = V | 只读 SQL；与 @@version 并非独立的发行来源证明 |
| @@version = S | 同一会话只读查询，版本可含构建后缀 |
| @@version_comment = C | 编译注释，可由构建配置改变；不是签名 |
| 驱动版本、连接 ProviderId、用户声明的 ServerVersion | 不能替代服务端产品证据 |

[MySqlConnector 2.6.2 源码](https://github.com/mysql-net/MySqlConnector/blob/2.6.2/src/MySqlConnector/MySqlConnection.cs)
将公开 ServerVersion 映射到会话的 OriginalString；不额外认为驱动提供发行商证明。
[MySQL 系统变量文档](https://dev.mysql.com/doc/refman/8.4/en/server-system-variables.html#sysvar_version_comment)
说明注释是非动态的编译属性；[官方构建源码](https://github.com/mysql/mysql-server/blob/8.4/cmake/mysql_version.cmake)
允许修改注释，默认源码构建标记也不同。这些读取不需要创建对象或管理员权限；连接、代理或
查询权限仍可能拒绝它们，失败时不能使用默认值补齐证据。

## 固定支持集合与判定

首版只支持 Oracle MySQL **Community 官方二进制发行版，且呈现下面精确元组**。
Enterprise、HeatWave、Aurora MySQL、Percona Server、MariaDB、TiDB、NDB、自定义源码构建与
重写信号的托管代理均不在支持集合。Enterprise 商业构建未纳入首版真实矩阵，不能仅按商标扩展支持。

同一打开会话执行一次 `SELECT VERSION(), @@version, @@version_comment`，读取 H，按以下顺序判断：

1. 恰好一行、三个非 null 字符串；H/V/S 长度各为 1–64，C 为 1–128。拒绝控制字符，
   不 trim、不转大小写、不去掉后缀；多行/缺列/类型不符都拒绝。
2. H、V、S 必须 Ordinal 全等；V 必须是 ASCII 三段数字（每段 1–3 位，无多位前导零），
   major 为 8 或 9。只接受三个纯数字段，不接受 `-debug`、`-commercial`、`-MariaDB` 等后缀。
3. C 必须 Ordinal 精确等于 `MySQL Community Server - GPL`。不是 contains、前缀或“没有 MariaDB”。
4. 任一规则不满足返回产品未支持，不尝试其他查询来扩大允许集合。

这固定的是产品识别边界，不承诺每个 8/9 版本的迁移兼容性。原有用户声明版本的语法校验保留；
不新增“声明版本必须等于实际版本”的独立配置契约。真实矩阵固定 Community 8.0/8.4，其他匹配
发行版仍须消费方验证，不据数字形状宣称已跑过真实测试。

[样本文件](mysql-product-samples.json)是合成的判定测试向量，**不是**全部产品真实采样记录。
它固定 Community 成功、商业/云/兼容产品标记、缺失/矛盾/未知信号、大小写与空白边界。
Aurora 有[专有版本变量](https://docs.aws.amazon.com/AmazonRDS/latest/AuroraUserGuide/AuroraMySQL.Updates.Versions.html)，
本任务不增加云产品发现协议；其未知或专有元组按同一正向规则拒绝。

明确限制：与官方元组逐字相同的自定义构建或代理，在这些信号下不可区分；不能承诺识别并拒绝
这种伪装。部署方负责二进制来源与连接信任，库保证仅为上述有限观察规则。对明确不同、缺失或
矛盾的自定义/代理信号失败关闭，不把注释当作密码学证明。

## 四条路径与顺序

| 路径 | 固定顺序 |
| --- | --- |
| 目标连接成功（Bootstrap / Observe 共用） | 连接目标 → 产品查询 → 既有 DATABASE()/大小写规则 → 成功 |
| 目标缺失 UnknownDatabase | 记录原始缺失 → 复制同一目标凭据、仅清空 Database → 至多一次服务器连接/产品查询 → 产品通过才保留缺失 |
| 目标级权限拒绝 DatabaseAccessDenied | 同上，产品通过才保留 PermissionDenied；不得升级/替换凭据 |
| Prepare 管理会话 | 校验请求 → 用既有 unpooled、AutoEnlist=false 管理设置打开一次 → 产品查询 → 存在性检查 → 必要时 CREATE |

AuthenticationFailed、网络失败不尝试服务器级 fallback。fallback 没有产品证据时不能返回 TargetMissing。
所有产品查询都在将要使用的同一会话中完成；不要跨连接或全局缓存“已验证”。Prepare 的存在目标
也必须先查产品，不能从 AlreadyExists 绕过。未通过前不得执行 DDL、DML、GRANT、SET 或创建临时对象。
查询失败/无结果/被拒绝表示证据不可用；不能从权限错误推断产品匹配。

产品查询使用已有 connect 上限 8 秒与 command 上限 5 秒；Prepare 所有步骤共享请求 timeout，
fallback 不重置准备 deadline；不增加重试。调用方取消优先，抛带原调用 token 的安全 OCE，
不保留 raw inner exception。Dispose 失败不得覆盖主要结果。不承诺驱动/服务器内部成本或强杀回滚。

| 失败 | Bootstrap | Observe | Prepare |
| --- | --- | --- | --- |
| 产品不匹配/信号缺失矛盾/产品查询被拒绝 | database.provider_validation_failed | TargetUnreachable(InvalidTarget, null) | InvalidTarget |
| 连接认证失败 | database.authentication_failed | TargetUnreachable(AuthenticationFailed, null) | AuthenticationFailed |
| 连接传输失败 | database.connection_failed | ServerUnreachable(ServerUnreachable) | ConnectionFailed |
| 产品通过后的目标缺失/权限拒绝 | 保留现有分类 | TargetMissing / TargetUnreachable(PermissionDenied, null) | 保留既有创建分类 |
| 准备整体超时 | 不适用 | 不适用 | Timeout |
| 调用方取消 | 安全 OCE | 安全 OCE | 安全 OCE |

产品查询中的传输/command timeout 按连接传输失败处理；Prepare 整体 deadline 已到则 Timeout 优先，
调用方取消优先于两者。未知内部异常保留现有 provider_validation_failed / PreparationFailed 分类。
表内简称均为 WellKnownDatabaseTargetPreparationErrorCodes，不能新增公开错误码。
输出不含连接信息、版本原文、产品注释、查询结果或异常消息。

## 交付与真实矩阵

#221 实现内部统一判定函数与会话探测，四条路径共享它；通过脚本 probe 验证顺序、零创建、
有限样本及每阶段取消/失败。无需公共 SPI；不新增 task。#62 在 #221 后增加真实 MySQL 8.0/8.4
与 MariaDB 10.11/11.4 交叉拒绝，覆盖已有/缺失/权限拒绝目标与管理创建前拒绝。
真实环境已启用却缺少 Docker/镜像/权限时失败；不把合成样本说成 Percona/TiDB/云产品真实验证。
#62 如发现产品实现缺口，另开 issue，不在迁移锁 PR 中补产品代码。

#221 的 README 必须写明实际支持集合、四条路径、信号可伪装及真实矩阵边界；本 PR 不改变当前
README 的已实现能力描述。#221 仍 Blocked by #236，#62 仍 Blocked by #221，决策未合并不解除。

本次刻意不修：[#223](https://github.com/philfanzhou/ServiceMantle/issues/223) 的服务器实例身份。
产品匹配不证明管理与目标指向同一服务器；其余新增邻近问题：无。
