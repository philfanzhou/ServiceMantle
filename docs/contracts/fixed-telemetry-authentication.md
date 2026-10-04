# 固定遥测认证来源

FixedRemoteTelemetryAuthenticationResolver 属于核心 ServiceMantle.Diagnostics，签名只含中立header/string，
无exporter/framework/provider依赖。显式注册 IRemoteTelemetryAuthenticationResolver，再显式启用OTLP信号。
构造名称严格1–128 ASCII字母数字._-、ordinal不trim，header名称为1–128 RFC HTTP token，值非空无CR/LF，
不额外限制无换行Unicode/control值，不trim/转义。非法输入固定ArgumentException，null允许ArgumentNullException，
无输入回显或inner；ToString只固定metadata，header也保持已有安全ToString。

构造→只读内存→exact match→不可变header.Value→已有exporter编码→caller明确授权collector；
miss（含null/空/大小写不同）false/out null，不残留先前成功值。多次/并发返回同一不可变证据。
不自动读取configuration/environment或注册resolver，不更改已有header契约、disabled/authMissing/网络/取消行为。

不承诺内存清零/rotation、caller显式记录/反射序列化公开Value、第三方exporter/collector诊断的DLP。
secret输出仅为成功lookup的Value，调用方负责取值、保密、collector信任。同步纯解析无token、资源、transaction、
文件/数据库持久化或migration；exporter自己的生命周期仍按既有契约。
