# 根密钥文件来源

RootKeyFileSource 归核心 ServiceMantle.Configuration，公开契约只有构造 string filePath 和同步 string Resolve()。
构造仅捕获本地全限定普通路径，无I/O；不读取配置/环境、不自动注册，不选择注入值优先级。无公开路径/密钥属性，
ToString固定metadata；固定无inner InvalidOperationException不回显路径、内容或OS材料。Resolve成功string是显式secret渠道。

调用方可把 `source.Resolve` 直接作为现有同步 rootKeyResolver，或在既有DI delegate中显式调用。
例如 `builder.PersistKeysToServiceMantleEfCore<MyDbContext>(serviceId, _ => source.Resolve())`；来源不保存消费方
DbContext/事务、不负责migration。敏感流为RNG/文件→Resolve→caller既有保护器；没有新日志、HTTP或数据库输出。

## 文件生命周期

|事件|target / temp / directory|结果|
|---|---|---|
|构造|无I/O|延迟来源|
|现有合法私有普通文件|字节、mode和timestamp不变，无temp|原密钥|
|损坏、过宽、链接、非普通对象或未知metadata|不创建replacement，不修权限|固定失败|
|缺失目标，受信任专用父目录|同目录唯一CreateNew0600 temp→完整32随机bytes canonical Base64→Flush(true)→close→既有HardLinkPublisher|Published或TargetAlreadyExists均重读校验target胜者|
|link拒绝或写/flush/释放失败|只清理本调用temp，不退回rename/overwrite|固定失败|
|发布后异常或清理失败|完整target保留，可能自有temp残留|后续Resolve仍复用target；清理best effort|

原子link是提交点，不能用临时候选值替代target结果。并发创建者短暂保留第二个temp硬链接名称是正常情况，
不以linkcount==1拒绝合法胜者。独立进程8路发布前barrier证明真实竞发一winner、所有成功解析hash一致，无secret输出。

## 输入与平台

文件严格32bytes canonical Base64，44 ASCII字符，允许唯一末尾LF或CRLF；最多读取47bytes，超过46拒绝，
不trim任意空白。目标read前须positively证明普通文件，FIFO/socket/device/目录/悬空symlink不打开读取。
拒UNC/device/ADS/NUL/URI/dot组件，以及target、parent、祖先的symlink/reparse；不得以File.Exists=false推定可创建。
支持Linux/macOS/Windows x64/arm64私有native metadata helper；未知平台/architecture/能力失败关闭。

已有Unix专用父目录必须owner-only、有owner read/execute，可无write；若直接父目录叶缺失且其父存在，只创建该叶0700并重新验证。
不递归悄建目录树、不chmod已有目录。既有文件0400/0600，新temp在创建当下0600，不能写secret后chmod。
Windows继承caller配置的ACL，仍验证普通对象与reparse；真实CreateHardLinkW、reparse、共享handle及进程竞发由Windows专项CI执行，
本机macOS结果不能代替Windows证据。

## 保证与非保证

在已声明普通本地文件系统、受信任专用目录内，复用合法文件；并发首次创建只发布一个完整target，成功者返回胜者。
格式/权限/metadata失败关闭，不重置损坏文件。同步API没有CancellationToken/async完成取消协议，文件handle在返回/失败前释放。

不保证具有目录写权限的恶意外部进程、校验后的替换/恶意hardlink、网络/同步/特殊挂载原子和durability、断电目录entry fsync、
强杀或拒删下temp清理、Windows ACL等价Unix、轮换/分发、string清零、caller显式记录/反射序列化返回值。
调用方传受信任本地路径/专用目录并在Windows配ACL，避免外部修改，把密钥与加密数据一起备份持久化，不记录返回值。
丢失/损坏不能重新生成后冒充能解密旧keyring；撤销新来源可回退原delegate，已生成文件仍保留。
