# 出站 Correlation ID 传播

显式为受信任的 named client 调用 AddServiceMantleCorrelationIdPropagation()，只挂当前builder，
重复幂等。TryAdd HttpContextAccessor 与 transient handler，不需要 AddServiceMantle host identity。
每次发送只读当前middleware私有已解析槽，复用64字符acceptance规则，不缓存HttpContext/值，
不读原Request.Headers、不生成后台ID。无middleware/context、无效槽或未挂载client不会新增头。
caller已设置x-correlation-id（含多值/非法值）原样保留，其合法性属于caller；库新增值仅用Headers.Add。

端到端：middleware→已解析slot→endpoint→factory client→downstream发送同值。原入站坏/重复头由
middleware生成safe ID，handler只见safe slot。64并发execution contexts与同factory handler复用不串值。
入口取消不调用inner，token原样传inner；正常/异常完成时caller取消优先，释放已经拿到的owned response后
抛原token/无secret inner OCE。未取消时response/异常原样返回；caller request不释放。

调用方控制目的地和自动redirect，信任边界包括redirect。库不检查URL，不承诺下游回传、唯一性、
后台execution context传播或caller显式头合法性；ID不是secret、认证/授权/幂等身份。
没有配置/持久化/数据库/transaction/migration变化；框架拥有accessor/client生命周期。
