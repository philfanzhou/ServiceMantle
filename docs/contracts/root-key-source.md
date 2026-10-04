# 注入优先的延迟根密钥来源

`ServiceMantle.Configuration.RootKeySource` 是核心包的同步组合器。构造
`RootKeySource(string? injectedValue, string? filePath)` 只捕获两个字符串，不访问文件、不规范化或验证路径，
也不构造文件来源。公开成员只有 `Resolve()` 和固定 `ToString()`，没有路径或密钥属性。

| 入口与输入 | 状态与产物 | 结果 |
| --- | --- | --- |
| 构造任意输入 | 仅捕获字符串，无 I/O | 成功 |
| Resolve，注入值不是 null/empty/whitespace | 无文件来源或文件产物 | 每次逐字返回注入值，不 trim，不验证无关路径 |
| Resolve，无注入、合法路径 | 首次安全发布并复用一个文件来源引用 | 每次调用文件来源 Resolve，返回文件胜者或原失败 |
| Resolve，无注入、缺失或非法路径 | 文件来源构造失败，无已发布引用 | 原固定失败；下次重新尝试，不缓存异常 |
| 并发首次 Resolve | 可构造多个零 I/O 候选，只发布一个引用 | 全部调用已发布来源，文件竞发结果归前置协议 |
| 后续 Resolve，文件状态变化 | 不缓存文件密钥、异常或状态 | 重新执行文件协议，不能绕过损坏拒绝 |

文件格式、权限、发布、胜者重读、失败与清理只有一个定义：
[根密钥文件来源契约](root-key-file-source.md)。组合器不复制这些规则，也不修权限或覆盖坏文件。
文件来源构造和解析的固定无 inner `InvalidOperationException` 原样透传。
`ToString()` 精确为 `RootKeySource(Lazy=True)`，不插值注入值、路径或解析结果。

## 显式组合

```csharp
var source = new RootKeySource(capturedInjectedRoot, explicitPrivateRootPath);
builder.PersistKeysToServiceMantleEfCore<MyDbContext>(serviceId, _ => source.Resolve());
```

构造、注册、构建 ServiceProvider 和获取 KeyManagementOptions 都不解析根密钥。
空 key ring 的 GetAllElements 可以零调用；实际 StoreElement 或非空读取才通过现有 rootKeyResolver 解析。
敏感流仅为捕获注入值或前置文件 → Resolve 显式 secret → 调用方既有保护器；没有新日志、HTTP、数据库明文出口。
仓库的 `sm:v1:` 加密格式、独立 DbContext/事务与 migration 所有权保持不变。

## 保证与边界

保证非空注入原样返回且不接触文件来源，无注入时显式延迟采用文件协议，引用发布线程安全，诊断不插值敏感材料。
选择器不验证注入值的熵、来源、UTF-16 或适用性；既有保护器仍拒绝不良 UTF-16。
不承诺轮换、live reload、string 内存清零或阻止调用方记录返回值；文件威胁模型和 Windows ACL 边界沿用前置契约。
后续调用重新解析文件不构成外部替换安全保证，测试修复夹具只证明没有结果或失败缓存。

调用方负责安全捕获注入值、显式选择受信任专用目录、配置 Windows ACL，并备份根密钥与加密数据。
改变根密钥不能解密既有 key ring。回退原 delegate 必须保持同一 root，已生成文件保留。
没有配置或环境读取、默认路径、DI 自动注册、新异步接口、HTTP/auth 协议、migration 或 transaction。

同步入口没有 token，正常、异常与清理后完成的异步取消检查点均不适用。选择器不拥有文件 handle；
前置负责释放与临时文件清理。专项验证正常结果与失败分类，并在解析完成及失败后检查文件可独占打开、
没有多余临时文件；前置写入、flush、释放、发布及清理的回归仍由其原测试负责。
