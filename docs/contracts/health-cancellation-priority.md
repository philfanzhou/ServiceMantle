# 健康就绪：调用方取消优先级

一次就绪读取在每一层有一个输出检查点。本文档说明当调用方已经取消时，在该检查点会发生什么，
以及哪些内容是刻意留在承诺之外的。

它覆盖同一健康管道的两个公开出口：

- `ReadinessDecisionSource`，默认的 `IServiceReadinessDecisionSource`。
- 就绪 endpoint `GET /health/ready` 和 `GET /health`，作用于**任何**已注册的决策源。

`GET /health/live` 不在此列：它从不解析就绪决策源。

## 规则

如果在这一层将要交回其结果的那个点上，调用方的取消已被请求，则该读取以一个
`OperationCanceledException` 结束，其 `CancellationToken` 是调用方自己的 token。这优先于
该读取已经计算出的所有其他结果：

| 读取自身的结果 | 调用方已取消 | 结果 |
| --- | --- | --- |
| 快照源抛出普通异常 | 是 | `OperationCanceledException`，调用方的 token |
| 快照源抛出 `TimeoutException` | 是 | `OperationCanceledException`，调用方的 token |
| 快照源抛出别人的 `OperationCanceledException` | 是 | `OperationCanceledException`，调用方的 token |
| 快照源返回快照或 `null` | 是 | `OperationCanceledException`，调用方的 token |
| 决策源返回 `Ready`、`NotReady`、`Unavailable` 或 `null` | 是 | 请求以 `OperationCanceledException` 结束，携带请求的 token——不写入任何响应 |
| 决策源抛出任何异常 | 是 | 请求以 `OperationCanceledException` 结束，携带请求的 token |
| 上述任意情况 | 否 | 现有的有限分类，保持不变 |

没有调用方取消时，一切不动：普通的源故障仍然是 `health.probe_failed`，内部预算超时仍然是
`health.probe_timeout`，未就绪的基础快照仍然以其自身错误码投影，就绪快照仍然在共享预算下
运行有序的贡献者。

默认源保持其现有的取消出口：在释放它拥有的链接 token 源之前，它会取消该源，因此配合的
快照源会在它收到的 token 上得到通知。这在失败出口上同样成立，而不仅是在完成快照的出口上。

## 不承诺的内容

- **检查点之后的窗口。** 在检查点与调用方收到结果之间请求的取消不会被捕获。检查点是一个
  边界，不是持续的守卫。
- **传输时序。** 这里没有任何内容约束客户端 socket FIN/RST 与服务器请求 token 被取消之间
  的延迟。
- **第三方行为。** 忽略其 token、阻塞，或在取消回调中抛异常的源不会被强制中断，本层也不会
  等待它完成自己的清理。
- **`TimeoutException` 的含义。** 抛出它的源被分类为超时。那是一种分类，不是配置的预算确实
  已被耗尽的证明。
- **此路径之外的诊断。** 否定性断言覆盖此路径产生的错误和响应，以及测试从中捕获的内容。
  它们对源为自己记录什么不作任何说明。

## 如何被覆盖

`ServiceReadinessCancellationPriorityTests` 驱动两个出口：

- **源**出口通过真实的默认决策源驱动，经由公开 DI 组合，配一个先取消调用方自己的 token、
  然后以上述每种方式结束的快照源。未取消的对照用例断言有限分类保持不变，一个受控的内部预算
  超时断言超时分类得以保留，fixture 释放并等待它阻塞的探测。
- **endpoint** 出口用替换后的决策源在两个就绪路由上驱动，对*处理器*产生的异常进行断言——由
  管道周围的中间件观察到——因为自我取消的 `HttpClient` 无法证明 endpoint 做了什么。

已取消的请求不解析任何决策源，也不调用任何决策源；live 路由同样不解析任何决策源。
