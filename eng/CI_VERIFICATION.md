# CI 验证入口核对

本仓库有两个验证入口，一个 workflow 出现在其中一个不代表它出现在另一个：

- **PR 入口**：`pull_request` 触发的 workflow，在合并前给出证据。
- **Release 复验入口**：`release.yml` 在 main push 与 tag push 上重跑验证，并用 `needs` 把
  `publish` 挡在验证之后。

`release.yml` 通过 `uses: ./.github/workflows/ci.yml` 复用 `ci.yml`，因此 `ci.yml` 里的 job 自动
进入 release 复验。**拥有自己触发器的独立 workflow 不会被自动带上**：它只在自己的 `pull_request`
入口运行，除非 `release.yml` 显式调用它，并把它加进 `publish` 的 `needs`。

`bootstrap-credential-existence.yml` 就是这样一个独立的 Windows 专用 workflow。它同时出现在：

- PR 入口：自身的 `pull_request: branches: [main]` 触发器；
- Release 复验入口：`release.yml` 的 `bootstrap-credential-existence` job（`uses:` 复用调用），
  并且是 `publish` 的 `needs` 之一。

## 新增或修改专用 workflow 时逐项核对

1. 该 workflow 是否需要在合并前给出证据？需要就保留自己的 `pull_request` 触发器，并确认整个仓库里
   只有这一个 PR 触发点，不要再从 `ci.yml` 或其他 workflow 重复调用它。
2. 该证据是否必须在发布前复验？必须就在 `release.yml` 增加一个 `uses:` 调用它的 job。被调用的
   workflow 需要有 `workflow_call` 触发器。
3. 把新 job 的名字加进 `publish` 的 `needs`。只加 job 不加 `needs`，门禁不会生效。
4. 调用 job 只声明它需要的权限。本仓库的复用调用统一是 `permissions: contents: read`。
5. 不要给复用调用或 `publish` 加 `continue-on-error`，也不要用 `always()` / `!cancelled()` 之类的
   `if:` 条件。`needs` 的默认语义就是：被依赖 job 失败或取消时，依赖它的 job 被跳过；加上这些
   写法会把门禁放开。
6. 确认 concurrency 分组仍然可区分。被调用 workflow 里的 `github.workflow` 取的是**调用方**的
   workflow 名，所以同一个被调用 workflow 在 PR 入口和 release 入口会落在不同的分组里，不会互相
   取消。新增调用时确认分组表达式仍然包含 `github.workflow` 或等价的区分项。

## 本地结构校验

合并前可以只用仓库里的 YAML 做结构校验，不需要真的触发 release：

```bash
python3 - <<'PY'
import yaml
release = yaml.safe_load(open(".github/workflows/release.yml"))
jobs = release["jobs"]
called = {name: job["uses"] for name, job in jobs.items() if "uses" in job}
print("被调用的 workflow：", called)
print("publish 的 needs：", jobs["publish"]["needs"])
print("continue-on-error：", [n for n, j in jobs.items() if "continue-on-error" in j])
print("if 条件：", [n for n, j in jobs.items() if "if" in j])
PY
```

`bootstrap-credential-existence.yml` 必须出现在被调用集合里，对应 job 名必须出现在 `publish` 的
`needs` 里，且没有 job 带 `continue-on-error` 或放开门禁的 `if:`。

## 未合并 PR 上能证明什么

结构校验、被调用 workflow 的 PR 运行结果，以及 `release.yml` 的 YAML 差异，都可以在未合并的 PR 上
给出。**release DAG 本身跑不起来**：`release.yml` 只在 main push 和 tag push 上触发，`workflow_dispatch`
单独运行被调用 workflow 也不等于 release DAG 已经跑过。新增或调整 release 接线的 PR 必须写明：
首次 main push 的 release run 才是后置验证点，届时复核新增 job 是否出现在 DAG 中、`publish` 是否
确实被它挡住。
