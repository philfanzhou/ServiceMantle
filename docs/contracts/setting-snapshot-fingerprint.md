# 设置快照：指纹编码的 UTF-16 保真

`ServiceSettingSnapshotLoader` 在激活一个完整快照前，会把规范化后的设置内容摘要成一个进程内
指纹，用于同版本比较：指纹相同视为无变化（复用当前快照、不重新激活），不同则判为
`configuration.snapshot_conflict`。本文档固定该指纹的编码边界，说明它如何保证不同的字符串
不会被折叠成相同摘要，以及哪些内容刻意留在承诺之外。

## 编码规则

指纹对每个参与摘要的字符串（规范化后的键与值）使用统一的无损编码，再进入既有 SHA-256：

1. **长度前缀。** 先写入该字符串的 UTF-16 code-unit 数量（4 字节大端），字段边界由它确定。
2. **逐 code-unit 手写编码。** 随后按确定字节序（大端）把每个 UTF-16 code unit 原样写成
   2 字节，包括孤立（未配对）的 high/low surrogate。

修复前使用的 `Encoding.UTF8` 带有替换回退，会把孤立 surrogate 静默折叠为 U+FFFD 字节，使
`\uD800`、`\uD801`、`\uFFFD`、`\uDC00`、`\uDC01` 等不同字符串在摘要前就变成相同字节，同版本
不同值被误判为无变化并复用旧快照。这不是 SHA-256 碰撞，而是编码折叠。**不得**改回仍有替换
回退的 `Encoding.Unicode`：它对孤立 surrogate 同样折叠（回退为 `?`）。只有逐 code-unit 手写
编码能保持每个不同的 code-unit 序列在摘要中可区分。所有参与摘要的字符串都走同一 helper。

指纹只在进程内比较、从不持久化，因此编码方案没有跨版本或跨进程契约，修改它不涉及数据库、
`sm:v1` 信封或任何持久化格式。

## 保持不变的既有语义

- **String 原样接纳。** String 类型继续保留原始 .NET 字符串（含孤立 surrogate），本层不校验、
  不规范化、不拒绝其 Unicode 良构性；非法 UTF-16 的拒绝属于敏感值保护契约，不在本项。
- **规范化等价。** Number 的 `2.50`/`2.5`、Boolean 的 `TRUE`/`true`、以及既有 JSON 文本规范化
  的等价规则保持：等价表示产生相同指纹，同版本复读成功但不重新激活。
- **元数据参与比较。** 键的排序（Ordinal）、`HasValue`、`IsDefault` 与值类型都进入摘要；可选值
  的有无、不同键的字段切分都因长度前缀而可区分。
- **版本规则。** 更高版本可激活并保留原值；更低版本仍为 `configuration.snapshot_stale`；同版本
  指纹相同为无变化，不同为 `configuration.snapshot_conflict`。
- **安全与取消。** 成功、失败与取消结果都不输出原始值或指纹；预取消、source 失败、锁释放与
  并发完整快照的既有保证不变。

## 不承诺的内容

- **有限的编码保真，不是密码学保证。** 本项保证有限输入矩阵上 code-unit 序列的无损可区分，
  不声称 SHA-256 在数学上永不碰撞。
- **不改变语义等价。** 不做 Unicode 规范等价（NFC/NFKC）、不改变 JSON 属性顺序或数值语义；
  两个语义等价但 code-unit 不同的字符串仍按不同指纹处理，除非既有规范化规则已把它们归一。
- **上游往返。** 只处理原始值已进入 loader 后的比较，不保证数据库、HTTP 或加密上游能往返非法
  Unicode；那属于各自的边界。
- **不是对外协议。** 摘要不持久化、不对外暴露，也不是跨进程一致性校验协议；不为值规模或同步
  编码增加耗时上限。

## 如何被覆盖

`SettingSnapshotFingerprintTests`（核心，独立 source/registry helpers）驱动：

- 三对折叠样本（`\uD800`/`\uD801`、`\uD800`/`\uFFFD`、`\uDC00`/`\uDC01`）及反向次序、前后夹带
  ASCII、多个孤立 surrogate：同版本不同 String 值返回 Conflict，旧 accessor 引用保持不变，失败
  结果不返回旧值。
- 相同 code-unit 序列重读成功但不激活；更高版本同类字符串可激活并保留原值；较低版本仍为 Stale。
- 空串、ASCII、中文、合法 surrogate pair、真实 U+FFFD 与 NUL 的边界样本编码可区分；两键
  `("ab","c")` 对 `("a","bc")` 验证字段边界由长度前缀确定。
- `2.50`/`2.5`、`TRUE`/`true` 与 JSON 文本规范化等价保持；可选值有无验证 `HasValue` 参与比较。
- 通过公开 `ServiceSettingQueryService` 验证冲突返回空投影（`Succeeded=false`、`Values` 为空、
  `Version=null`），成功/失败结果不回显原始值。

既有 `ServiceSettingSnapshotLoaderTests`、`ServiceSettingQueryServiceTests` 与设置查询 HTTP
回归保持通过。
