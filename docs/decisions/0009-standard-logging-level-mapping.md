# ADR 0009：标准日志级别配置映射的边界

- 日期：2026-10-05；状态：提议，合并本ADR后接受
- 决策任务：[#631](https://github.com/philfanzhou/ServiceMantle/issues/631)，父跟踪：[#622](https://github.com/philfanzhou/ServiceMantle/issues/622)
- 代码/实验基线：main `f4b47c67543f774615a6894ba1caa8c4bcd72635`；.NET 10.0.400、Serilog 4.4.0、Serilog.Extensions.Hosting 10.0.0
- 本项只有决策产物，不改运行时、包依赖、日志安全、#597冲突检查或任何消费方。

## 决策

选择选项3：暂时维持现状，由消费方显式映射和承担其宿主/日志路径责任。本轮不新增标准配置读取入口、预设、None开关或采用任务。
薄映射能够表达Trace到Critical六级，却不能用现有SerilogOptions完整表达标准None。把局部MEL过滤与选项拼成结果会引入第二套
注册、路径覆盖与优先级协议；这超出“读取配置节并产生选项片段”的职责。没有足够证据需要为了共享少量解析代码，改变
现有单一管线启用模型。未来若要统一关闭，应先独立定义并验证同时控制MEL和直接Serilog路径的显式管线契约，再重新评估映射，
不能提前创造实现前置或把本决策当成承诺。

此结论不声称调用方现有映射天然正确。调用方应定义输入接受域和无回显失败，拒未定义枚举、处理None边界；
不能以消费方一条MEL规则证明库所有sink都关闭。标准配置来源可以由调用方显式取节，与库“不隐式回退读取业务配置”的立场一致。
当前库不自动探测Logging:LogLevel，也不建立配置优先级、热加载、默认Warning预设或配置与代码的合并规则。

## 三个候选与纯键值变体

|候选|收益/归属|限制与结论|
|---|---|---|
|1：显式IConfigurationSection→SerilogOptions片段|调用方明确选择来源，不必违背禁止隐式读取；若将来提供，签名依赖配置框架及SerilogOptions，归ServiceMantle.Logging.Pipeline适配包，依赖登记进eng/packages.json，不能放核心裸Logging域|None不能表达；若返回MEL规则又需声明第二协议的完整路径/注册/优先级。section已经折叠的相同键无法恢复，不应承诺检测所有源重复。当前不提供|
|1的纯键值变体：显式有限pairs→片段|减少配置依赖，保留原始pairs可校验trim重复；可复用原Normalize验证六级|仍不能表达None；避免一个依赖没有解决主语义缺口。当前不提供，不把未实现API写成已保证|
|2：常见两条Warning预设|没有配置读取，便于代码组合；覆盖AspNetCore与EF SQL分类|两条字典赋值已能表达，无法满足运维标准配置或None，自动注册会改变默认；新增公开入口价值不足。当前不提供|
|3：维持调用方显式映射|不改库路径/启用/安全与#597，消费方有自己的配置来源和禁用责任|重复解析暂留；None必须按调用方实际日志路径验证。这是本次选择|

## 唯一有限输入与两条日志路径演算

下表中六级候选映射只是评估模型；现有库没有配置mapper。成功的选项均继续经过原SerilogConfiguration.Normalize/Resolve，
默认值与冲突规则不因来源变化。空配置维持Information/no overrides。MEL路径为ILoggerFactory→RuntimeLoggerProvider→同一Serilog管线；
直接路径为runtime.Logger.Write，没有SourceContext时只受global限制；调用方另行ForContext产生SourceContext才受category限制。

|输入/事件|评估映射或现有检查|MEL结果|直接Serilog结果|
|---|---|---|---|
|空配置|原Information、无overrides|Information放行，Debug拒绝|无SourceContext同样global|
|Default=Trace/Debug/Information/Warning/Error/Critical|分别Verbose/Debug/Information/Warning/Error/Fatal|按阈值过滤|同一global阈值；六级既有测试全覆盖|
|Default=None，错误映为Critical|只设Fatal最低级别不是关闭|若只设该选项，Critical仍放行；另加MEL None才拒绝MEL|Fatal仍放行，反例成立|
|category Example=None，错误映为Critical并加MEL None|Serilog override Fatal与MEL rule是不同层|Example.Child Critical被MEL拒绝|ForContext Example.Child Fatal仍放行；无SourceContext Information仍受global放行|
|None直接给现有global/override|Normalize拒绝，minimum_level_invalid / minimum_level_overrides_invalid|启动失败固定诊断|不建立有效runtime，不能声称运行时关闭|
|Microsoft=Error、Microsoft.AspNetCore=Debug；AspNetCore.Hosting Information|case-sensitive最长dot prefix命中Debug|放行|带对应SourceContext放行，无SourceContext按global|
|仅Microsoft.AspNetCor=Error；Microsoft.AspNetCore Information|不是dot边界，不命中短词|按global放行|带该SourceContext按global放行|
|空/空白key、trim后长于256、undefined42|候选应拒绝；现有Normalize实际拒绝|启动失败、不回显提交材料|无有效runtime|
|Enum.TryParse("42")|解析成功但Enum.IsDefined=false，不能只检查TryParse|当前库Normalize拒绝42|同样拒绝|
|raw keys K与“ K ”同级|原Normalize trim后同键，幂等，排序不重要|一个有效override|同一override|
|raw keys K与“ K ”不同级|原Normalize有限确定拒绝|启动失败minimum_level_overrides_invalid|同样无有效runtime|
|两次注册同等规范化片段/空与null map|原Resolve幂等，只有一个runtime|成功、统一过滤|同一runtime|
|两次不同片段，或不同global/flush/scopes|原Resolve不采用last-wins|启动失败registration_conflict|同样无有效runtime|
|同样原始配置key在IConfiguration上游被折叠|mapper不能复原原source重复，显式边界必须承认|不能宣称已检查折叠前冲突|纯pairs仅能检查实际收到的pairs|

非法输入候选应采用固定错误，不回显外部key/value/inner；本ADR不新定诊断码或更改原异常。
Default大小写/来源重复的解析优先级、配置provider合并均由调用方明确定义，不把Dictionary赋值顺序当共享契约。
现有Normalize会复制snapshot，启动后改变字典不热加载；两条路径同样按启动snapshot工作。

## 真实来源与去重

完整核对[LexarborLoggingSetup真实历史来源](https://github.com/philfanzhou/Lexarbor/blob/9aad9d5f128aa8b2f942d64f33c94cceeba38f8b/src/Lexarbor.Host/LexarborLoggingSetup.cs)：
Default→global、category None单独返回，其他值trim后字典；Enum.TryParse缺IsDefined使42通过解析阶段，最终仍会被库拒绝。
这是待迁移代码的例子，本项不修改消费仓。核对[Lexarbor #213](https://github.com/philfanzhou/Lexarbor/issues/213)实际为Correlation ID/InstanceId采用，
它依赖#628，不是本日志映射的采用任务；不能凭编号建立错误依赖或复制任务。
本轮未提供能力，故不创建实现/四仓采用Issue，也不增加原生Blocked by。明确不做理由写回#622；已有共享缺口/采用跟踪身份保留，
不把#631提议文档提交当成父tracker结项。

## 实证与可复现最小实验

实际运行独立控制台实验11个断言全部通过；只有固定probe文本，sink仅计数，不输出事件材料。实验拥有并using释放Serilog logger与
LoggerFactory，factory拥有所加provider，provider dispose:false不双重释放logger；没有host、数据库、网络、secret或新异步协议。
用AddProvider显式构造provider匹配本次要测的MEL规则边界。初始使用上游AddSerilog扩展的试跑因它注册provider-specific最低级别规则，
通用SetMinimumLevel(None)未达到预设状态而失败，已作为无效实验前置保留，不计通过；不能把上游注册差异归责本库。

在任意临时目录创建以下Spike.csproj和Program.cs，运行 `dotnet run --project Spike.csproj -c Release`。

```xml
<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App"/><PackageReference Include="Serilog" Version="4.4.0"/><PackageReference Include="Serilog.Extensions.Hosting" Version="10.0.0"/></ItemGroup>
</Project>
```

```csharp
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); Console.WriteLine(name + "=passed"); }
var sink = new Counts();
using (var logger = new LoggerConfiguration().MinimumLevel.Fatal().WriteTo.Sink(sink).CreateLogger())
{
    using var factory = LoggerFactory.Create(builder => builder.AddProvider(new Serilog.Extensions.Logging.SerilogLoggerProvider(logger, dispose: false)).SetMinimumLevel(LogLevel.None));
    factory.CreateLogger("Example.Child").LogCritical("probe");
    Check(sink.Count == 0, "global_MEL_None_drops_MEL_Critical");
    logger.Fatal("probe"); Check(sink.Count == 1, "global_None_as_Critical_still_passes_direct_Fatal");
}
sink = new Counts();
using (var logger = new LoggerConfiguration().MinimumLevel.Verbose().MinimumLevel.Override("Example", LogEventLevel.Fatal).WriteTo.Sink(sink).CreateLogger())
{
    using var factory = LoggerFactory.Create(builder => builder.AddProvider(new Serilog.Extensions.Logging.SerilogLoggerProvider(logger, dispose: false)).SetMinimumLevel(LogLevel.Trace).AddFilter("Example", LogLevel.None));
    factory.CreateLogger("Example.Child").LogCritical("probe"); Check(sink.Count == 0, "category_MEL_None_drops_MEL_Critical");
    logger.ForContext("SourceContext", "Example.Child").Fatal("probe"); Check(sink.Count == 1, "category_None_as_Critical_still_passes_direct_Fatal");
    logger.Information("probe"); Check(sink.Count == 2, "direct_without_SourceContext_uses_global");
}
sink = new Counts();
using (var logger = new LoggerConfiguration().MinimumLevel.Information().MinimumLevel.Override("Microsoft", LogEventLevel.Error)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Debug).MinimumLevel.Override("Microsoft.AspNetCor", LogEventLevel.Fatal).WriteTo.Sink(sink).CreateLogger())
{
    logger.ForContext("SourceContext", "Microsoft.AspNetCore.Hosting").Information("probe"); Check(sink.Count == 1, "longest_dot_boundary_wins");
    logger.ForContext("SourceContext", "Microsoft.Extensions.Hosting").Information("probe"); Check(sink.Count == 1, "shorter_prefix_drops_Information");
    logger.ForContext("SourceContext", "Microsoft.AspNetCor.Hosting").Information("probe"); Check(sink.Count == 1, "exact_dot_prefix_applies");
    logger.Information("probe"); Check(sink.Count == 2, "no_SourceContext_uses_global_Information");
}
Check(Enum.TryParse<LogLevel>("42", true, out var invalid) && !Enum.IsDefined(invalid), "TryParse_42_succeeds_but_IsDefined_rejects");
Check(Enum.TryParse<LogLevel>("None", true, out var none) && none == LogLevel.None, "None_is_defined_separate_from_Critical");
sealed class Counts : ILogEventSink { internal int Count; public void Emit(LogEvent value) => Count++; }
```

输出11行passed：global/category MEL None分别拒绝Critical，但direct Fatal分别放行；无SourceContext用global；
最长dot prefix/短词边界；TryParse42虽成功但未定义；None是独立枚举。所有Check失败均退出非零，不以人工看日志代替断言。

原库真实契约执行 `dotnet test --project tests/ServiceMantle.Logging.Tests -c Release --filter-class '*SerilogHostTests'`，37/37通过，
覆盖六级、global/override None和42、空/长key、trim重复两种顺序、两次注册等价/冲突、prefix最长/dot边界、两条日志路径与安全。
实现规则与断言来源为 `src/ServiceMantle.Logging/Pipeline/SerilogOptions.cs`、`SerilogRegistration.cs`、`SerilogRuntime.cs` 和
`tests/ServiceMantle.Logging.Tests/SerilogHostTests.cs`，不拿只有链接或Markdown格式检查当语义证据。

## 适用边界与交付

本ADR没有新运行时状态、持久化、凭据流、transaction/migration或取消协议；并发/异步完成/数据回滚不适用。
不会改变LOGGING_SECURITY的结构化清理保证与自由文本非保证；调整日志阈值不能被当成DLP。
文档合并后#631可按决策交付验收，产品仍维持现状；本执行阶段只创建独立PR，不合并或关闭原任务。
重新评估的退出条件是存在明确需要统一None、两个日志路径一致且不引入隐式来源优先级的有限公共契约和独立验收证据。
