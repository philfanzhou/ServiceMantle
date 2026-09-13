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

- **仓库内的 Markdown 文档一律用中文**，只有一个例外：仓库根目录的 `README.md`。它通过
  `PackageReadmeFile` 随 NuGet 包分发，是使用者在 nuget.org 上看到的包说明，必须保持英文。
  子目录下的 `README.md` 不随包分发，同样用中文。
- **随包分发的代码内文字保持英文**：公开 API 的 XML 文档注释、异常消息、`[Obsolete]` 等特性中的
  文字。这些是调用方在 IntelliSense 和异常里直接读到的内容，属于公开包契约，不属于仓库文档。
- **提交到 GitHub 的 issue 和 PR，正文一律用中文。** Issue 标题用中文；PR 标题使用英文
  conventional commit 格式（`feat:` / `fix:` / `docs:` / `test:` / `refactor:` / `chore:` 等）。
- **Review 全程用中文**：行内意见、review summary、回复，以及向维护者汇报的 review 结论均用中文。
  代码、标识符、诊断码和命令行保持原样。
- 代码标识符和 commit message 保持英文。
- 新增文档直接用中文写。存量英文文档按目录分批翻译，各自由独立 issue 跟踪；轮到之前保持原样，
  不要因为路过就顺手翻译。

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

### 领取任务与推进阻塞

按 `CONTRIBUTING.md` 的“积压收敛与排期”执行，不以 PR 数量或新找到的 ready 数量作为完成目标：

1. 在既有 tracker 保留一份当前队列，记录清理周期基线、期初 task 编号、主线和退出条件。替换过时的
   当前调度段落，历史证据留在评论或编辑历史，不叠加多份相互矛盾的“最新结论”。
2. 先处理已领取任务和开放实现 PR 的未完成验收，再选能推进主线的旧 task。默认一条主线加至多
   一项必要风险/环境专项；用户明确要求不同并行方式时遵从，但不为填满并行槽位新造任务。
   用户要求批量连续执行时，保留数量上限和完整清单，不擅自缩为只做一项；每项独立 PR，禁止合并时
   仍继续不依赖未合并代码的后项。切换或压缩上下文前保留任务、提交、验收、review 轮次及下一步。
3. 无 ready 项时，从主线选择一个阻塞 task，完成其具体技术调查、最小实验和方案收敛。ready 是
   生产实现门禁，不是开始设计调查的门禁。不得把“当前 main 上可独立开 PR”当作唯一价值判据。
4. 在原 issue 写明阻塞类型（实际前置、技术设计、环境或外部决策）、具体问题、下一步、负责角色和
   解除证据。前置已关闭就删除过时的等待理由；排期顺序不得伪装成原生技术依赖。
5. 同一阻塞再次出现且没有新证据时，执行已列实验、落实专项环境路径或提出具体决策问题。能从代码
   和既有契约确定的技术选择自主完成；不能只改写“仍未闭合”再转去找小缺陷。
6. 跨仓 task 在拥有实现的仓库工作，读取该仓规范并回链原 task。不复制一份同范围任务，不因在本仓
   找不到实现就退回公共库巡检。确需迁移跟踪位置时保留映射与未完成范围，不计为功能完成。

新增发现先检索开放及关闭的 issue/PR，再按贡献指南分诊。有对应开放项时补证据，只有独立且未登记
的问题才新建。已关闭任务的验收遗漏应标明原 task、原 PR、遗漏条目和复现，作为返工跟踪；不把
验收遗漏包装成新需求。除当前验收或需立即处置的严重风险外，登记后返回已选主线。

已有完整开工审计应记录基线 commit、实现/测试/契约范围和未决事项。后续先核对基线差异及受影响
调用链：无相关变化就复用证据；相关行为或契约变化时补读受影响的完整成员和测试。不得用无关 main
提交触发反复全仓审计，也不得以旧绿色结果替代实际修改后的验证。

### 在宣布一个 issue 可以开工之前

绝不能只看 issue 描述就判断它 ready。先完整阅读 issue 指向的文件和成员，同时阅读相关测试、公开
契约文档、注册/持久化边界以及会直接受影响的调用路径。

在这些代码中发现、但 issue 没有要求修复的既有缺陷，属于**邻近债务**。每一条都须有去重后的独立
issue 归属，然后在目标 issue 的 `## 已知邻近问题（本次不修）` 中链接；登记不自动改变当前排期。

一个 task issue 只有同时满足以下条件才可标记 `status: ready`：

1. 写清楚了 `## 范围`（或等价的最小修改范围章节），包括明确排除项。
2. 写清楚了可逐条验证的 `## 验收标准`。
3. 安全或健壮性相关任务以与保证同等精度写清楚 `## 明确不包含与不保证`（或等价章节）；不得使用
   无法穷尽验证的“任何输入”“绝不泄漏”“始终有界”等无边界承诺。
4. 将要改动的实现、测试与相关契约已经读过。
5. 邻近债务已经各自关联去重后的 issue 并完成分诊；确认没有时明确写“无”。
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
- 为行为验收逐条记录“条目 → 实际断言 → 执行结果”。异步完成语义按贡献指南核对正常、异常及
  清理后的适用检查点；有副作用不等于可以省略结果检查。没有覆盖的条目保留未完成，不以总测试数
  或 CI 全绿勾选通过。已完成实现暴露旧验收遗漏时回链原证据，不反复创建同义任务。
- 不得顺手重构、改名、升级依赖或修复仅仅靠近 diff 的问题。实施中发现的邻近债务要开 issue，并在
  PR 的“本次刻意不修”中链接。
- 新增或移动类型时按 `CONTRIBUTING.md` 的“命名规范”落 namespace、类型名和文件名，不要新增重复
  产品前缀，也不要把 `ServiceMantle.AspNetCore` 的类型放进核心包 namespace。

### 改名与移动类型时的额外审计

命名或目录调整不是纯文本替换，实施前后必须逐项确认，结果写进 PR：

1. 先出映射清单：旧完整类型名/旧路径 → 新完整类型名/新路径，逐条标注可见性与例外理由；清单
   固定本次改动范围，清单外的类型不动。
2. 核对同名冲突：新名字是否与 BCL、ASP.NET Core 隐式 using 可见的类型、上游库类型或本仓库另一
   namespace 的类型撞名。撞名按职责改名，不靠别名或全限定名硬撑。
3. 核对字符串字面量：日志分类、诊断码、配置键、HTTP 路由与 Header、JSON 字段、数据库表列、
   Data Protection purpose、认证方案名、指标名、`InternalsVisibleTo` 与包/程序集标识都不随类型
   改名变化。确因类型全名改变的反射或诊断输出单独列出并更新验证。
4. 公开类型改名属于源码和二进制破坏性变更：完整映射写入 `NAMING_MIGRATION.md`，
   在后续新版本交付，不覆盖历史版本。
5. 验证用非增量构建加打包产物消费验证：`dotnet build ... --no-incremental` 之后跑 ReleaseTool
   `validate / restore / build / test / pack / verify`，并用 `eng/tests/consumers` 的最小消费项目
   确认调用方没有新增 using 冲突或全限定名负担。消费项目必须覆盖本次改名涉及的每一个包，并且
   同时 using 该包入口所需的框架 namespace——没有被消费项目引用的包，CS0104 不会在 CI 里暴露。

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
2. 核对该 PR 对应 task 的逐条验收证据。已完成则关闭；只完成一部分或仍有后续工作则保持开启，
   并更新说明或链接去重后的 follow-up issue。随后按贡献指南检查直接父项，已完成的父项逐层结项。
   读取父级验收、原生子项和相关未解决缺陷；不靠子项全关机械关闭，不检查无关分支或借机新增范围。
   父项保留时记录具体剩余项和下一步。没有关联 issue 时，在汇报中明确写“无关联 issue”。
3. 仓库开启了 `deleteBranchOnMerge`。确认远端工作分支已删除；仍存在时删除或说明保留原因。
4. 用 `git worktree list` 检查残留 worktree。删除前确认其干净且没有独有提交；移除不再需要的
   worktree 后执行 `git worktree prune`。
5. 更新本地目标分支：`git switch main && git merge --ff-only origin/main`。若 `main` 被其他 worktree
   占用，先安全移除该 worktree，或在持有 `main` 的 worktree 中更新并说明。
6. 清理本地工作分支与远端跟踪引用。`git branch -d` 失败不等于 PR 未合并，尤其是 squash 或 rebase
   合并；先用 GitHub 的 merged 状态、merge commit 和最终 tree/diff 确认改动已进入目标分支，确认后
   才可 `git branch -D`。无法确认就保留并说明，绝不能因 `-d` 失败直接改用 `-D`。
7. 向维护者汇报 PR、issue、远端分支、本地分支、worktree 和验证结果的最终状态；未完成项必须说明
   原因与后续动作。清理周期另报期初 task 完成/剩余、新增分诊、返工、父级结项及解除的阻塞；
   不把父项关闭、拆分或跨仓迁移算成原任务的功能交付。
