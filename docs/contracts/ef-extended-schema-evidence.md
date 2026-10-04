# EF 显式扩展证据

`EfCoreExpectedSchemaDerivationOptions(true, resolver)` 显式启用真实 relational 名称与普通键数量。
旧 Derive(model) 不输出名称、不调用解析器；表映射、identity、stored default 与 schema 规则保持。
解析器为 `Func<ITableIndex, IReadOnlyList<string>?>`，null 视为无 INCLUDE，输出立即复制与核心校验。
异常和非法结果统一安全 InvalidOperationException，无输入回显/inner，不输出半个 ExpectedSchema。

调用方以已知 provider annotation 解析 INCLUDE，并转换 CLR 属性为 store column 名。示例：

```csharp
IReadOnlyList<string>? IncludeColumns(ITableIndex tableIndex, string annotationName)
{
    var store = StoreObjectIdentifier.Table(tableIndex.Table.Name, tableIndex.Table.Schema);
    var names = tableIndex.MappedIndexes.SelectMany(index =>
        index.FindAnnotation(annotationName)?.Value as IReadOnlyList<string> ?? []).Distinct();
    return names.Select(name => tableIndex.MappedIndexes
        .Select(index => index.DeclaringEntityType.FindProperty(name))
        .First(property => property is not null)!.GetColumnName(store)!).ToArray();
}
var npgsql = new EfCoreExpectedSchemaDerivationOptions(true,
    index => IncludeColumns(index, "Npgsql:IndexInclude"));
var sqlServer = new EfCoreExpectedSchemaDerivationOptions(true,
    index => IncludeColumns(index, "SqlServer:Include"));
```

示例只适用于调用方确认的 provider 映射；库不猜 annotation、不新增 driver 依赖，
不解释未明确呈现的表达式。没有 configuration 读取、数据库 I/O、取消或 transaction。
确定性限定相同模型与纯解析器，外部副作用/执行开销属于调用方责任。

端到端：最终 relational model→原表列推导→显式对象名/INCLUDE→核心不可变模型→strict Compare。
名称/键数/INCLUDE 可独立比较；核心规则见 [证据模型](schema-evidence-models.md)。
