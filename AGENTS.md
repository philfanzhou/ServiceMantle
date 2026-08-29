# ServiceMantle 协作规范

ServiceMantle 是面向 ASP.NET Core 服务的 .NET 10 共享基础库。核心包保持 provider-agnostic，
ASP.NET Core、数据库 provider 与 EF Core 持久化能力通过独立包提供。

## 维护方式

- 本文件是仓库内 AI 协作流程与约束的唯一事实来源。
- Codex 直接读取本文件；Claude Code 通过根目录 `CLAUDE.md` 导入本文件。
- `CONTRIBUTING.md` 是所有贡献者都必须遵守的工程与 review 政策，本文件负责把它落实为 AI 的工作流程。
- 修改 AI 通用流程时只改本文件。除非某条规则确实只适用于单个工具，否则不要把规则正文写进
  `CLAUDE.md` 或其他工具入口文件，以免内容漂移或互相冲突。
- 修改项目级交付或 review 政策时改 `CONTRIBUTING.md`；若它改变了 AI 的实际操作步骤，同时更新本文件。

## 文档语言

- **仓库内的流程与约束文档用中文**：本文件、`.github/ISSUE_TEMPLATE/` 下的 issue 模板、
  `.github/pull_request_template.md`。
- **提交到 GitHub 的 issue 和 PR，正文一律用中文。** Issue 标题用中文；PR 标题使用英文
  conventional commit 格式（`feat:` / `fix:` / `docs:` / `test:` / `refactor:` / `chore:` 等）。
- **Review 全程用中文**：行内意见、review summary、回复，以及向维护者汇报的 review 结论均用中文。
  代码、标识符、诊断码和命令行保持原样。
- **面向使用者的发布文档用英文**：`README.md`、公开 API 的 XML 文档注释、异常消息、
  `[Obsolete]` 等特性中的文字。这些内容属于公开包契约。
- 代码标识符和 commit message 保持英文。

## 项目边界

- `src/ServiceMantle` 核心包不得引入 ASP.NET Core、EF Core 或具体数据库驱动依赖。
- ASP.NET Core、数据库 provider 与持久化能力各自留在对应可选包中；新增或调整包时以
  `eng/packages.json` 为包、依赖、测试项目和集成测试环境变量的唯一登记源。
- ServiceMantle 提供产品无关的基础能力，不引入 SignaCore 或其他消费方的业务模型、认证细节、
  migration 实现或前端。
- 共享 `DbContext` 的保存、事务和 migration 所有权属于消费方；除非公开契约明确声明，库代码不得
  隐式提交消费方工作单元。

## 范围纪律

本仓库一个 PR 只关闭一个可实施的 task issue。实现以及适用的失败、取消、安全和并发测试必须在
同一个 PR 中闭环。若改动新增第二个独立包、契约或 endpoint 组，拆 issue，不扩张原 PR。

### 在宣布一个 issue 可以开工之前

绝不能只看 issue 描述就判断它 ready。先完整阅读 issue 指向的文件和成员，同时阅读相关测试、公开
契约文档、注册/持久化边界以及会直接受影响的调用路径。

在这些代码中发现、但 issue 没有要求修复的既有缺陷，属于**邻近债务**。每一条都单独开 issue，
然后在目标 issue 的 `## 已知邻近问题（本次不修）` 中链接。

一个 task issue 只有同时满足以下条件才可标记 `status: ready`：

1. 写清楚了 `## 范围`（或等价的最小修改范围章节），包括明确排除项。
2. 写清楚了可逐条验证的 `## 验收标准`。
3. 安全或健壮性相关任务以与保证同等精度写清楚 `## 明确不包含与不保证`（或等价章节）；不得使用
   无法穷尽验证的“任何输入”“绝不泄漏”“始终有界”等无边界承诺。
4. 将要改动的实现、测试与相关契约已经读过。
5. 邻近债务已经各自开成 issue 并链接；确认没有时明确写“无”。
6. GitHub 原生 `Blocked by` 中的前置 issue 已关闭，依赖关系和 `layer:*` 标签一致。

“描述清楚”不等于 ready。没有做过代码与邻近债务盘点的 issue，不能开工，也不能添加
`status: ready`。

本规范不要求批量重写落地前已经存在的 issue；领取既有 `status: ready` issue 时仍须完成上述盘点。
发现邻近债务就先补 issue 和链接。使用新模板创建或重新整理的 issue 必须显式记录“无”。

### 实施过程中

- Issue 的 `最小修改范围` 有约束力。明确排除的行为不要改，即使它确实存在缺陷，也应另开 issue。
- 先找不变量，再决定修改点。若同一缺陷会从多条路径到达同一输出，修复应落在路径汇合处，并用
  输入集合验证该不变量，不要只补报告中出现的单一分支。
- 不变量修复可以覆盖目标契约内的多条路径，但不能借此吸收第二个独立契约。若正确修复确实超出
  原 issue 边界，先更新或拆分 issue，再继续写代码。
- 成功路径以及适用的失败、取消、安全和并发行为必须与实现一起测试。调用方取消应与内部异常明确
  区分；测试应证明安全保证的有效范围，但不得在已声明的非保证边界上作相反承诺。
- 不得顺手重构、改名、升级依赖或修复仅仅靠近 diff 的问题。实施中发现的邻近债务要开 issue，并在
  PR 的“本次刻意不修”中链接。

### Review 过程中

每条意见在写下或实施之前先分类：

- **本 PR 引入的**：缺陷位于本 PR 新增或实质修改的行为上，在本 PR 中修复。
- **既有的**：缺陷位于仅被移动、重新缩进、改名波及或紧邻 diff 的既有代码中。单独开 issue，在
  review 意见和 PR 描述中链接，并明确不在本 PR 范围内。

只有一个越界例外：既有缺陷导致本 PR 某条验收标准无法验证。使用该例外时必须指明具体验收标准，
并确认修复仍属于同一个契约；否则拆分 issue。

意见真实、可复现、证据充分，不等于它在当前 PR 范围内。范围由 issue 的最小修改范围与验收标准
决定，不由意见质量或代码距离决定。

违反已声明保证的发现，无论 review 轮次都必须在当前 PR 修复。不违反保证的资源整形、纵深防御和
内部结构建议最多在当前 PR 处理三轮；之后把意见原文带入 follow-up issue，并在 PR 中链接说明。

### 熔断线

PR 进入第三轮 review 时，先停止写代码，逐个将 commit 和未解决意见对照 issue 的最小修改范围、
验收标准与非保证：

- 无法追溯到某条验收标准的 commit 属于范围违规，应撤出并改成独立 issue。
- 不违反已声明保证的新增建议应按 review 轮次预算转为 follow-up issue。
- 违反已声明保证的问题继续修复，但不得借机吸收邻近债务。

## 验证与包边界

按改动风险运行最小充分验证，并在 PR 中记录实际命令和结果。常规本地入口为：

```bash
dotnet restore ServiceMantle.slnx
dotnet build ServiceMantle.slnx -c Release --no-restore
dotnet test --solution ServiceMantle.slnx -c Release --no-build --no-restore
```

涉及包清单、依赖、打包元数据或发布路径时，使用与 CI 相同的 ReleaseTool 流程：

```bash
dotnet run --project eng/ServiceMantle.ReleaseTool -- validate
dotnet run --project eng/ServiceMantle.ReleaseTool -- restore
dotnet run --project eng/ServiceMantle.ReleaseTool -- build --version 0.0.0-local.1 --commit local
dotnet run --project eng/ServiceMantle.ReleaseTool -- test
dotnet run --project eng/ServiceMantle.ReleaseTool -- pack --version 0.0.0-local.1 --commit local --output artifacts/packages
dotnet run --project eng/ServiceMantle.ReleaseTool -- verify --version 0.0.0-local.1 --commit local --input artifacts/packages
```

PostgreSQL 和 SQL Server 集成测试需要 Docker 及对应环境变量；只在改动触及相应 provider、SQL、映射、
持久化或并发语义时运行，并在无法本地运行时明确说明等待 CI 验证：

```bash
RUN_SERVICEMANTLE_POSTGRES_TESTS=true dotnet test --project tests/ServiceMantle.Database.PostgreSql.Tests -c Release
RUN_SERVICEMANTLE_SQLSERVER_TESTS=true dotnet test --project tests/ServiceMantle.Persistence.EntityFrameworkCore.Tests -c Release
```

## 合并 PR 后

PR 合并不等于工作结束。必须检查并合理处理以下事项，不遗留失效状态或无主分支：

1. 用 `gh pr view <编号> --json state,mergedAt,mergeCommit,baseRefName,headRefName` 确认远端 PR 已合并，
   目标分支已包含合并结果。本仓库允许 merge、squash 和 rebase，不能从本地祖先关系反推合并方式。
2. 检查且只检查该 PR 对应的 task issue。已完成则关闭；只完成一部分或仍有后续工作则保持开启，
   并更新说明或链接 follow-up issue。没有关联 issue 时，在汇报中明确写“无关联 issue”。
3. 仓库开启了 `deleteBranchOnMerge`。确认远端工作分支已删除；仍存在时删除或说明保留原因。
4. 用 `git worktree list` 检查残留 worktree。删除前确认其干净且没有独有提交；移除不再需要的
   worktree 后执行 `git worktree prune`。
5. 更新本地目标分支：`git switch main && git merge --ff-only origin/main`。若 `main` 被其他 worktree
   占用，先安全移除该 worktree，或在持有 `main` 的 worktree 中更新并说明。
6. 清理本地工作分支与远端跟踪引用。`git branch -d` 失败不等于 PR 未合并，尤其是 squash 或 rebase
   合并；先用 GitHub 的 merged 状态、merge commit 和最终 tree/diff 确认改动已进入目标分支，确认后
   才可 `git branch -D`。无法确认就保留并说明，绝不能因 `-d` 失败直接改用 `-D`。
7. 向维护者汇报 PR、issue、远端分支、本地分支、worktree 和验证结果的最终状态；未完成项必须说明
   原因与后续动作。
