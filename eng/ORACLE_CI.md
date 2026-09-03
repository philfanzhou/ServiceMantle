# Oracle CI 就绪边界

适用镜像：`container-registry.oracle.com/database/free:23.26.1.0-lite-amd64@sha256:ef1a38683b3783b80e033be6b8f2cb31299dcba5430514ec96e2e8f4f0307d15`。

## 根因与证据

[#260](https://github.com/philfanzhou/ServiceMantle/issues/260) 的来源 run
[33606978013 / attempt 1](https://github.com/philfanzhou/ServiceMantle/actions/runs/33606978013/attempts/1)
在 healthy 后首次 ODP.NET 登录返回 ORA-01017；同一提交 attempt 2 成功。

直接从该 digest 镜像提取的脚本显示：

1. `/opt/oracle/runOracle_lite.sh` 先运行 `START_FILE start`，启动实例并打开 PDB。
2. 然后运行 `PWD_FILE`（默认 `setPassword.sh`），依次设置 SYS、SYSTEM、PDBADMIN 密码。
3. 最后调用 `checkDBStatus.sh`，成功时打印独占一行的 `DATABASE IS READY TO USE!`。
4. Docker 独立运行 `checkDBStatus.sh`。该脚本通过 `sqlplus -s /` 本地认证检查数据库角色与 PDB open mode，未测试 listener 上的 SYSTEM 密码。

因此步骤 1 完成后、步骤 2 的 SYSTEM 密码设置完成前，healthcheck 可以成功；此时新 ORACLE_PWD 的 listener 登录仍可返回 ORA-01017。这是启动顺序竞争，不是已证明的密码格式或 digest 差异。历史 run 未保存容器启动完整时序，无法追溯其实际密码设置时间点。

## 修复与失败边界

`wait-oracle-ready.sh` 同时等待 healthy 与完整启动标记，默认最多检查 90 次、间隔 10 秒；CI 用 `timeout 15m` 为整个轮询进程设置硬上限。
原始容器日志只在管道内匹配，不输出；失败只给出固定分类或 healthy/initialized 布尔值。
此标记用于 CI 每次新建、无复用数据卷、无自定义启动脚本的固定镜像。换镜像时必须重新核对。

标记只证明密码设置步骤已执行；镜像自身的 SQL 错误不一定阻止标记输出。因此后续必须执行一次
真实 ODP.NET 登录和 `SELECT 1 FROM DUAL`，连接超时 8 秒、命令超时 5 秒、测试总超时 30 秒，
另有 1 分钟步骤上限。预检不重试错误凭据或输出底层异常，只报告安全类别；完整真实数据库测试
仍由 ReleaseTool 执行。CI 总上限仍为 45 分钟。

## 复现与验证

- `bash eng/tests/oracle-readiness.sh`：用假 Docker 按顺序产生健康/启动状态，确定性复现 healthy 早于密码初始化完成，证明 gate 不会提前通过；同时覆盖未就绪、退出、读取失败与日志脱敏。
- `dotnet test --project tests/ServiceMantle.Database.Oracle.Tests -c Release`：覆盖安全预检的成功、分类、失败不重试与取消。
- 真机放大竞争的方法（须原生 AMD64）：新建该 digest 容器，通过 `PWD_FILE` 指向包装脚本，使其在调用原 `setPassword.sh` 前等待一个测试信号文件；等到 healthy，使用新 ORACLE_PWD 做 SYSTEM listener 登录，应失败。释放信号后等待完整启动标记，再做同一登录，应成功。包装脚本、密码与信号文件只用于隔离的临时容器，测试后删除，不能保留在正常 CI。
- ARM64 下模拟 AMD64 的 Oracle 启动可能出现实例不可用，不能作为该时序测试或真实产品测试的通过证据；使用本 PR 的 Ubuntu AMD64 CI 验证。
