# 包流水线

`packages.json` 是本仓库交付的每个包的唯一登记源。每个条目声明：

- 包 ID 与项目路径；
- 该包是否可选；
- 每一个直接的 NuGet/项目依赖与共享框架引用；
- 一个或多个测试项目，以及它们的集成测试所需的环境变量。

CI 与发布调用 `ServiceMantle.ReleaseTool`；它们不包含按包的构建、测试或打包步骤。要新增一个可选包，创建它的包项目和测试项目，然后在 `packages.json` 中加一个条目。不需要改 workflow 结构。当路径、ID、依赖、框架引用、测试所有权或环境声明与项目不一致时，registry 校验器会失败。

对需要真实数据库的测试项目，把 `realDatabase` 设为 `true`，登记它的
`RUN_SERVICEMANTLE_*_TESTS=true` 环境变量，并用 `tests/ServiceMantle.Testing` 中的
`RealDatabaseTestAttribute` 标注每个真实数据库 fixture。release tool 先证明至少能发现一个
`Category=RealDatabase` 测试，然后运行该项目并把跳过的测试视为失败。本地运行可以不设置该
必需变量并跳过 fixture；一旦 registry 把该环境标记为必需，不可用的服务就无法在 CI 中静默通过。
同一个测试支持项目还提供固定的凭据注入契约，以及 provider 并发 fixture 使用的有界进程内
`TwoActorBarrier`。

每个登记的测试项目都有一个 10 分钟的 Microsoft.Testing.Platform 全局超时。
真实数据库发现预检有单独的 2 分钟超时。每次调用都在
`artifacts/test-diagnostics/<repository-relative-project-path>/test` 或 `list-tests` 下启用
同步 MTP 诊断日志，因此在执行或进程退出期间挂死的 runner 会在 CI job 超时之前失败，同时保留
已写出的诊断输出。测试 job 失败或被取消时，CI 上传该目录；在创建诊断之前就失败的运行不会让
上传步骤变成另一个失败。从 workflow run 下载 `test-diagnostics-<run-id>-<attempt>` artifact
来检查 `.diag` 文件。目录与 artifact 名只由登记的项目路径和 GitHub run 元数据派生，绝不来自
登记的测试环境取值。

登记的测试调用运行在专属的 POSIX 进程组或 Windows job 内。一个小的 ReleaseTool host 在启动
`dotnet` 之前建立该作用域，报告原始的 runner 退出码，并存活到 ReleaseTool 终止该作用域为止。
它还会在成功、内部 MTP 超时或调用方取消时，移除直接父进程已退出的普通后代进程。
内部超时仍是流水线失败；调用方取消仍是退出码 130。host 启动有单独的 30 秒协议截止时间，
无法建立隔离时在启动测试之前失败。测试标准输出/错误与同步诊断文件保持既有路径。
这是进程生命周期管理，不是沙箱：清理不覆盖刻意脱离 POSIX 组的进程、通过外部服务启动的进程
或 Docker 容器。它不承诺优雅关闭子进程，也不承诺 ReleaseTool 自身被强制终止后的清理。

SQL Server 真实数据库登记还在 `packages.json` 中声明它们的 Docker daemon 要求。第一个此类
测试项目启动前，release tool 查询一次实际 daemon，要求 `OSType=linux`、`amd64`/`x86_64`
架构以及至少 `2147483648` 字节内存。之后的 SQL Server 项目复用这个不可变结果。daemon 缺失或
不可达、输出格式错误或 daemon 不受支持，都会在任何 SQL Server 测试进程或容器启动之前使测试
阶段失败；诊断只包含观察到的 OS、架构和总内存。

在 Apple Silicon 上，把 Docker 连接到一台至少 2 GiB 内存的 Linux x86-64 虚拟机或远程 daemon。
通过 QEMU 或其他架构转译层运行 SQL Server Linux 镜像不在支持路径内；客户机及其 .NET 进程可以
保持 arm64，因为预检评估的是 daemon 而不是客户机。

workflow 各阶段的本地等价命令是：

```bash
dotnet run --project eng/ServiceMantle.ReleaseTool -- validate
dotnet run --project eng/ServiceMantle.ReleaseTool -- restore
dotnet run --project eng/ServiceMantle.ReleaseTool -- build --version 0.0.0-local.1 --commit local
dotnet run --project eng/ServiceMantle.ReleaseTool -- test
dotnet run --project eng/ServiceMantle.ReleaseTool -- pack --version 0.0.0-local.1 --commit local --output artifacts/packages
dotnet run --project eng/ServiceMantle.ReleaseTool -- verify --version 0.0.0-local.1 --commit local --input artifacts/packages
```

`eng/tests/package-consumption.sh` 补上 `verify` 无法覆盖的一环：它针对打包产物构建并启动一个
一次性消费项目，使用本地文件夹 feed、把每个 `ServiceMantle*` id 固定到该 feed 的包源映射，以及
一个私有 `NUGET_PACKAGES` 缓存。一次通过的运行证明交付的包能独立成立——没有 `ProjectReference`、
没有兄弟仓库路径、也没有从共享全局缓存静默复用的 ServiceMantle 包。如果消费项目解析到本地 feed
没有提供的 ServiceMantle 库，它同样会失败——某个已退役的包 id 若仍被依赖，就是这样暴露出来的。

```bash
eng/tests/package-consumption.sh \
  --version 0.0.0-local.1 \
  --packages artifacts/packages \
  --consumer eng/tests/consumers/opentelemetry
```

`eng/tests/consumers/` 下的每个目录是一个消费项目：一个包引用使用 `__SERVICEMANTLE_VERSION__`
占位符的 `.csproj`，加上演练该包预期触达的入口点的程序。

`verify` 要求每个登记恰好一个 `.nupkg` 和一个 `.snupkg`。它在产物上传之前校验 ID、版本、MIT license、repository URL/commit、框架引用、完整的依赖集合，以及 ServiceMantle 包之间的同版本引用。

tag 流程、发布必须通过的关卡，以及一次性的 NuGet.org 账户配置，见 [RELEASING.md](../RELEASING.md)。

## 发布版本

`resolve-version` 是决定发布版本的唯一位置，因此 workflow 从不重复该规则：

```bash
dotnet run --project eng/ServiceMantle.ReleaseTool -- resolve-version \
  --ref-name "$GITHUB_REF_NAME" \
  --tagged true \
  --untagged-version "0.0.0-edge.$GITHUB_RUN_NUMBER.$GITHUB_RUN_ATTEMPT"
```

它分行打印 `number=` 和 `publish=`，可以直接追加到 `$GITHUB_OUTPUT`。tag 以去掉前导 `v` 的
名字作为发布版本；其他任何 ref 产生未打 tag 的版本并且 `publish=false`。

两条路径遵守同一规则：版本必须能解析为 NuGet 版本、不携带 build metadata，并且已经是 NuGet
的规范化形式。`v1.2`、`v01.0.0` 和 `v1.0.0.0` 会被拒绝，而不是被悄悄发布为 `1.2.0` 或
`1.0.0`，因为被 NuGet 改写的版本不再与告知消费方固定的 tag 一致。build metadata 被拒绝是因为
NuGet 会丢弃它，这会让 `v1.0.0+a` 和 `v1.0.0+b` 碰撞同一个包槽位。

## 发布

`publish` 把登记的包集合推送到一个 NuGet v3 feed：

```bash
dotnet run --project eng/ServiceMantle.ReleaseTool -- publish \
  --version 0.1.0-rc.1 --commit "$GITHUB_SHA" \
  --input artifacts/packages \
  --source https://api.nuget.org/v3/index.json \
  --api-key-environment SERVICEMANTLE_NUGET_API_KEY
```

它先运行与 `verify` 相同的检查，因此不完整或标错的产物集合会在任何东西公开之前失败。缺失的
本地产物会在任何 feed 访问之前按包 ID、版本和扩展名列出，dry run 也不例外。然后逐包处理：如果
feed 上已有该 ID 和版本，已发布包的 ID、版本和 `repository/@commit` 必须与本次发布匹配。
匹配的普通包按 `already present` 跳过，但其符号产物仍会尝试推送，以便重跑能修复此前的符号
上传失败。元数据不匹配或有歧义会使该包失败。推送冲突（HTTP 409），包括符号冲突，要求同样的
普通包读回校验。如果读取 endpoint 尚未索引它，命令失败并提示稍后重试。摘要分类描述的是普通包：
新被接受的 nupkg 是 `published`；匹配的既有 nupkg 是 `already present`；符号失败会把两种情况
都变成 `failed`。任何内容都不会被覆盖。加 `--dry-run` 可以执行每一项检查和每一次 feed 比对
而不推送。

多包推送不是事务，本命令也不假装它是。一次中断可能让 feed 只持有集合的一部分；重跑同一版本会
完成其余部分，因为已在 feed 上的包走 `already present` 路径。即使某个包失败，每个包仍会被
尝试，因此结束摘要报告 feed 的完整状态——published、already present 和 failed，各带包 ID 与
版本——而不是停在第一个问题上。任何失败，包括凭据被拒，都以非零退出。

feed 停止响应是这类失败之一，不是中断。每个请求有五分钟上限，超出的请求会记到它自己的包上并
计为失败，因此摘要仍会打印，命令以 1 退出。退出码 130 仍保留给调用方真正取消运行的情形。

凭据从 `--api-key-environment` 指定的环境变量读取，并在 `X-NuGet-ApiKey` header 中送往
feed，因此它绝不会进入子进程的参数列表。命令打印的每一行都经过一个以该值为键的 redactor，
这覆盖了由本工具不撰写的 feed 响应组装出的诊断。redactor 把每次写入作为整体扫描，因此一次调用
打印的诊断会被覆盖；它不跨多次写入缓冲。
