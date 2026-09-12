# 结构化日志安全契约

`ServiceMantle.Logging.StructuredLogSanitizer` 为结构化日志创建一个全新的、与 sink 无关的对象图。它不配置任何日志 provider 或 sink。

## 保证的边界

- 内置的敏感形状字段名（password、secret、token、API key、connection string、credentials、private/root/master keys、setup code、authorization 以及 cookie）会被整体替换。配置的拒绝字段会扩展这份列表；allow 规则永远不会覆盖 deny。
- 认证/cookie Header 以及配置的拒绝 Header 会被整体替换，且不区分大小写。
- `ISensitiveLogValue`、配置的敏感类型、Bootstrap 配置、凭据、认证 Header 值、数据库连接/构建器、加密私钥类型、HTTP 消息/内容/Header、证书以及二进制值永远不会被解构。
- 字典、JSON、公开对象成员、集合以及非字符串标量会被递归复制进一个新的、已清理的对象图。
- 非法名称会被移除。循环引用、深度/集合上限、反射失败、枚举失败以及清理异常都会产生稳定的安全标记。清理器绝不会回退到原始对象或原始值。
- 异常消息、堆栈跟踪和 `Data` 不会被输出；只保留异常类型结构。

## 输出保证

清理后的对象图只由以下形状构成：

- `null`、`string`、`bool`
- 有限的数值标量（`byte`、`sbyte`、`short`、`ushort`、`int`、`uint`、`long`、`ulong`、`float`、`double`、`decimal`）
- `DateTime`、`DateTimeOffset`、`DateOnly`、`TimeOnly`、`TimeSpan`、`Guid`
- 由上述类型组成的 `IReadOnlyDictionary<string, object?>` 与 `IReadOnlyList<object?>`

`JsonSerializer.Serialize` 在默认选项下保证不会对清理结果抛异常。枚举被归一化为 `long`；枚举字典键使用该归一化值的不变区域性十进制文本。对于以 `ulong` 为底层类型、取值超过 `long.MaxValue` 的枚举，归一化通过一次 unchecked 转换保留 64 位位模式，因此会产生对应的负数 `long`；它不保留原始的无符号数值。非有限的 `float`/`double` 值（`NaN`、`PositiveInfinity`、`NegativeInfinity`）并非所有 sink 都能表示，因此它们在标量变为输出的唯一位置被替换为 `[UNREPRESENTABLE_VALUE]`；任何输入路径都无法绕过这一点。

## 成本上界与明确的不保证

`MaximumDepth`、`MaximumCollectionCount` 和 `MaximumStringLength` 限定清理器自身的递归、元素枚举和字符串处理。它们在读取子值之前生效，包括在 `SanitizeFields` 和 `SanitizeHeaders` 入口处，因此惰性或无限序列会在配置的数量处终止。

这些上限不限定发生在调用方自有类型内部的工作。单个成员 getter、自定义 `JsonConverter` 或 `IEnumerator.MoveNext` 调用在清理器重新取得控制之前可能分配或计算任意多的资源；清理器只保证在达到上限后停止读取更多成员。

清理器不是拒绝服务边界。不要把直接由不可信输入构建的对象图交给它并依赖这些上限提供保护；应在信任边界处限定对象图。

## 自由文本边界

自由文本清理刻意只做尽力而为。它移除日志注入控制字符，并识别显式的敏感赋值、连接字符串、携带凭据的 URI、bearer token、类 JWT 值以及 PEM 私钥块。

它无法可靠识别外观像普通标识符的、未加标注的不透明秘密，也无法识别每一种编码、变换、加密或产品特有的秘密格式。绝不要把秘密插入自由文本。把可能敏感的数据放进被拒绝的结构化字段/Header、实现 `ISensitiveLogValue`，或在 `StructuredLogSanitizerOptions.SensitiveTypes` 中注册其类型。

清理器只约束经过它传递的值。它无法保护独立的 sink 配置、消息模板、scope 数据或绕过清理器的字段。

## ASP.NET Core 敏感 Header 快照

可选的 `ServiceMantle.AspNetCore` `AddSensitiveHeaders` 能力构建一个不可变的、不区分大小写的启动快照。它始终包含
`StructuredLogSanitizerDefaults.BuiltInDeniedHeaderNames`；消费方可以追加合法的 HTTP token
名称，但不能移除内置项。重复名称与大小写变体会合并为一个条目。
配置集合在 Host 启动时枚举一次，之后的修改会被忽略。

DI 提供的 `StructuredLogSanitizer` 与 `RequestHeaderDiagnosticProjector` 消费该快照。
projector 把 ASP.NET Core 请求 Header 集合复制进清理器；
被拒绝的单值与多值 Header 因此只产生 `[REDACTED]`。请求 Header
枚举失败只产生 `[SANITIZATION_FAILED]`，绝不回退到原始值。

该能力不会修改 `HttpRequest.Headers`、不配置日志或跟踪、不检查 Activity
tag，也不约束第三方诊断。产品特有的 Header 名称没有内置，必须由消费服务自行注册。不支持运行时更新、移除和热重载。
