# 发布 ServiceMantle

`eng/packages.json` 中的每个包一起发布，使用同一个版本、来自同一个 tag。没有按包单独发布，
也没有手动推送。

## 切一次发布

1. 确认你要发布的 commit 已经在 `main` 上。指向其他任何位置的 tag 会被拒绝。
2. 给该 commit 打 tag 并推送：

   ```bash
   git tag v0.1.0-rc.1 <commit-on-main>
   git push origin v0.1.0-rc.1
   ```

3. 关注 **Package release** workflow。成功后，每个已注册的包及其符号包都以 tag 对应的版本
   出现在 NuGet.org 上，并且一个消费项目已经从 NuGet.org 还原回该版本。

推送到 `main` 会运行相同的验证并产生相同的产物，但不发布任何东西。它们的版本号是
`0.0.0-edge.<run>.<attempt>`，只作为 workflow artifact 存在。

## 版本规则

版本号是去掉前导 `v` 的 tag。`v0.1.0` 发布 `0.1.0`；`v0.1.0-rc.1` 发布预发布版本
`0.1.0-rc.1`。NuGet 把任何带预发布标签的版本视为预发布，因此不需要额外做任何标记。

除非版本满足以下条件，否则 tag 会被拒绝：

- 能解析为 NuGet 版本；
- 不携带 build metadata（没有 `+`）；
- 已经是 NuGet 的规范化形式。

最后一条规则就是 `v1.2`、`v01.0.0` 和 `v1.0.0.0` 被拒绝、而不是以 `1.2.0` 或 `1.0.0` 发布的
原因。被 NuGet 改写过的版本不再与告知消费方固定的 tag 一致。build metadata 被拒绝是因为
NuGet 会丢弃它，`v1.0.0+a` 和 `v1.0.0+b` 会碰撞同一个包槽位。

该规则位于 `eng/ServiceMantle.ReleaseTool`（`resolve-version`）并在那里有单元测试。workflow
调用它，而不是重复实现它。

## 发布前必须通过什么

以下全部成功之前，发布 job 无法开始：

| 关卡 | 它证明了什么 |
| --- | --- |
| Tag ancestry check | tag 指向包含在 `main` 中的 commit。 |
| `resolve-version` | tag 命名的版本 NuGet 会原样存储。 |
| `verify`（完整 CI workflow） | 源码可构建且每个已注册的测试项目通过。 |
| `bootstrap-credential-existence` | 仅 Windows 的 Bootstrap 存在性证据已就位。 |
| `publish` | 每个包都已打包并通过 `verify`：精确的产物集合、ID、版本、license、repository commit、依赖集合、框架引用以及匹配的内部版本。 |

其中任何一项失败或被取消，发布 job 都不会运行。该 job 没有任何 `always()` 或 `failure()`
条件可以推翻这一点。

推送的包是从 `publish` job 下载的 artifact，不是重新构建的，因此到达 NuGet.org 的内容与这些
关卡检查过的内容逐字节一致。

## 失败与重跑

多包推送不是事务。如果一次运行中途被打断，NuGet.org 上会有集合中的一部分而没有其余部分。
这是预期行为，修复方式是重跑同一个 tag。

重跑时，feed 上已存在该版本的每个包都会被检查：已发布包的 ID、版本和 `repository/@commit`
与正在发布的这次发布进行比对。

- **相同 commit** - 本次发布的早前运行已推送过它。它被跳过并标记为 `already present`；
  其符号包仍会尝试推送，以修复此前的符号上传失败。
- **不同 commit，或没有 commit 元数据** - 该版本属于其他东西。这个包失败，任何内容都不会被
  覆盖。

推送冲突（HTTP 409）同样需要那次来源检查。如果 feed 尚未索引该包，运行会显式失败；等它可读
之后稍后重试。

运行的结束摘要把每个包列在 `published`、`already present` 或 `failed` 之下，各带 ID 与版本，
因此部分成功的结果是可见的，而不会被报告为成功。任何失败——凭据被拒、HTTP 错误、产物缺失——
都以非零退出。

已发布的版本绝不删除或替换。如果发布的版本有错，发布一个新版本。

要在不推送的情况下演练，给命令加 `--dry-run`：它执行每一项检查和每一次 feed 比对，但不推送
任何内容。

```bash
dotnet run --project eng/ServiceMantle.ReleaseTool -- publish \
  --version 0.1.0-rc.1 --commit "$(git rev-parse HEAD)" \
  --input artifacts/packages \
  --source https://api.nuget.org/v3/index.json \
  --api-key-environment SERVICEMANTLE_NUGET_API_KEY \
  --dry-run
```

## 发布后验证

推送成功后，workflow 从 NuGet.org 把已发布版本还原进一个从未存放过 ServiceMantle 包的 NuGet
缓存，针对它构建一个最小 ASP.NET Core 消费项目并启动它。runner 上的任何东西都无法让一个损坏
或缺失的包看起来可以安装。

推送在 feed 接受之后过一段时间才变得可还原，因此最初的几次尝试预期会失败。预算是有限的——
十次尝试、间隔三十秒——耗尽预算会使运行失败，而不是无限期等待。

## 凭据

发布使用 NuGet.org Trusted Publishing。发布 job 用它的 GitHub OIDC token 换取一个短寿命的
NuGet.org API key，因此本仓库不存储任何长寿命的发布秘密。

`id-token: write` 只授予那一个 job，别处都没有。其他每个 job，以及 pull-request CI workflow
中的每个 job，都以 `contents: read` 运行。

短寿命的 key 通过环境变量传给 release tool，并在 `X-NuGet-ApiKey` header 中送往 feed，因此它
绝不出现在进程参数列表里。publish 命令打印的所有内容都会经过一个以该值为键的 redactor。

## 一次性的 NuGet.org 配置

这些是账户侧的设置。它们无法从本仓库完成，且在它们存在之前发布会失败：

1. 一个 NuGet.org 账户，拥有（或预留）`eng/packages.json` 中的每一个包 ID。截至本文撰写时，
   NuGet.org 上没有注册任何 `ServiceMantle*` ID，因此应在首次发布前预留 ID 前缀
   `ServiceMantle.*` 以保住它。
2. 该账户上针对每个包（或针对预留前缀）的 Trusted Publishing 策略，绑定到：
   - repository owner `philfanzhou`
   - repository `ServiceMantle`
   - workflow file `release.yml`
   - environment `nuget.org`
3. 一个仓库变量 `NUGET_USER`，存放该 NuGet.org 用户名。登录 action 会把它传给 token 交换。
4. 一个名为 `nuget.org` 的 GitHub environment。如果发布前需要人工批准，给它添加必需的
   reviewer。
