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

### 实施过程中

- 调用方取消应与内部异常明确
  区分；测试应证明安全保证的有效范围，但不得在已声明的非保证边界上作相反承诺。
- 异步完成语义按贡献指南核对正常、异常及
  清理后的适用检查点；有副作用不等于可以省略结果检查。
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

## 验证与包边界

按改动风险运行最小充分验证。常规本地入口为：

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
