# 阶段放行标记契约

`WithServiceMantlePhaseAdmission` 为管理前缀**之外**的消费方 endpoint 提供一个按阶段限定的
放行标记。它解决的问题是：phase gate 恒装之后，管理前缀之外且无 surface 标记的 endpoint 只有
在服务就绪（`Completed + Succeeded + Reachable`）时才可达，消费方在 `BootstrapConfiguration`
或 `PendingSetup` 阶段没有任何方式让自己拥有的端点（首装页面、静态资源、只读端点）可达。

标记把「本端点只在这些启动阶段可达」声明为 endpoint 自己的元数据，判定继续留在 phase gate
这一个汇合点上；消费方不需要（也不应该）在 pipeline 之前另建短路分支。

## 最小接线

```csharp
var app = builder.Build();
app.UseServiceMantlePipeline();

app.MapGet("/bootstrap", RenderBootstrapPage)
    .WithServiceMantlePhaseAdmission(ServiceStartupPhase.BootstrapConfiguration);
```

## 放行规则

带标记的 endpoint 当且仅当**同时**满足以下两个条件时被放行：

1. 阶段门快照的 `Phase` 属于所声明的非空集合；
2. 快照的迁移状态不是 `Running` 或 `Failed`。

就绪判定（`ServiceHealthEvaluator.Evaluate`）不参与该分支：数据库未就绪不会把带标记的
endpoint 挡在其声明的阶段之外。无标记 endpoint 的行为逐字不变——仍是「就绪才放行」。

| 端点 | 快照 | 结果 |
| --- | --- | --- |
| 前缀外，标记 `{BootstrapConfiguration}` | 阶段 = `BootstrapConfiguration`，迁移 ≠ `Running`/`Failed` | 放行 |
| 同上 | 阶段 = `PendingSetup` 或 `Completed` | `503 {"errorCode":"service.phase.unavailable"}` |
| 前缀外，标记 `{BootstrapConfiguration, PendingSetup}` | 阶段 ∈ 集合 | 放行 |
| 前缀外，标记任意集合 | 迁移 = `Running` 或 `Failed` | 503 |
| 前缀外，标记任意集合 | 快照来源缺失、失败、为 `null`、内部取消或超时 | 503 |
| 前缀外，标记任意集合 | 调用方取消请求 | 抛出携带调用方 token 的 `OperationCanceledException`，endpoint 不执行 |
| 前缀外，无标记 | 就绪 | 放行（现状） |
| 前缀外，无标记 | 未就绪 | 503（现状） |
| 无端点匹配 | —— | 404（现状，早于本判定） |

快照读取、超时预算与取消语义与 phase gate 的其他判定完全共用：每请求恰好一次观察、沿用
既有的取消出口与通知顺序，不存在跨请求缓存。

## 启动验证

出现下列情形时宿主启动失败，消息为 phase gate 既有的固定失败信息：

- 标记被放在管理前缀之下的 endpoint 上（前缀之下的 endpoint 必须携带 surface 标记，两种
  标记互斥，不产生第二种解释）；
- 同一 endpoint 携带两个标记；
- 声明的阶段集合为空；
- 集合中含未定义的枚举值。

## 标记不携带的语义

标记只决定「放行与否」。它不提供、不改变、也不豁免任何认证、授权、速率限制、CSRF 或响应
Header 行为；带标记 endpoint 上的这些行为完全是 endpoint 自身声明的结果。健康 endpoint 的
既有豁免、management surface 与条目的放行规则、MVC 属性路由的分类规则都不因本标记改变。

## 明确不保证

- **放行不是授权**：它只表示「此刻快照阶段属于所声明的集合」。两次请求之间、以及放行与
  后续阶段推进之间没有保留、租约或顺序保证；已在执行的请求不会因阶段推进被中断或召回。
- **被放行 endpoint 的安全性**：匿名可达性、输入校验、限流、审计与凭据门全部由消费方
  负责。在早期阶段可达的 endpoint 不应依赖需要数据库或密钥的能力。
- **快照源质量**：阻塞、忽略 token 或回调抛出的 source 不会被终止，沿用 phase gate 既有
  非保证。
- 不为消费方提供静态文件、SPA、fallback 或任何路由能力。

## 消费方职责

- 为在未就绪阶段可达的 endpoint 自行提供认证或凭据门、限流与最小披露。
- 不在该阶段暴露需要数据库或密钥的能力。
- 放行是 endpoint 自己的声明：不要试图用配置键或路径前缀列表在宿主层面批量复制该语义。
