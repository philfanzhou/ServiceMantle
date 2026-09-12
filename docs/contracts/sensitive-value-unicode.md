# 敏感值保护：UTF-16 良构性边界

`SensitiveValueProtector` 用外部根密钥与绑定的服务上下文保护敏感配置值。派生上下文
（purpose）与密钥材料（rootKey、plaintext）都要经过 UTF-8 编码进入 HKDF/AES-GCM。本文档
固定该编码边界的良构性规则：哪些输入被拒绝、以什么形式拒绝，以及哪些内容刻意留在承诺之外。

## 规则

参与派生或加密的调用方字符串必须是良构 UTF-16，即不含孤立（未配对）的 high/low surrogate：

1. **purpose。** 构造函数在既有 `Trim` 与长度检查（1–128 字符）之后，对规范化 purpose 校验
   良构性；不合法时构造失败，派生上下文（`derivationInfo` / `associatedData`）不会被建立。
2. **plaintext。** `Protect` 在入口取消观察之后、派生与加密之前校验；不合法时不产生信封。
3. **rootKey。** `Protect` 与 `Unprotect` 均在入口取消观察之后、密钥派生之前校验。

统一编码 helper 使用抛出型（exception fallback）UTF-8 编码器，绝不使用替换回退。修复前
`Encoding.UTF8` 的替换回退会把 `\uD800` 与 `\uD801` 都折叠为 U+FFFD 字节，使不同 purpose 或
不同 rootKey 派生出相同密钥材料，造成跨上下文解密；修复后这类输入在编码前即被拒绝。

新增拒绝是固定英文消息的 `ArgumentException`，参数名分别为 `purpose` / `plaintext` /
`rootKey`，`InnerException` 为 null，不直接传播 `EncoderFallbackException`，消息与
`ToString` 不回显原始字符内容、密钥、明文或信封。既有 null/empty 参数拒绝保持不变；对已
取消且满足原 null/empty 前置的 `Protect` / `Unprotect` 调用，入口先观察调用方取消并交付
携带调用方 token 的 `OperationCanceledException`，Unicode 拒绝不会抢先。

合法输入的编码字节与修复前完全一致：salt、派生上下文构造、nonce/tag 与 `sm:v1` 信封格式
均未改变。修复前用良构输入生成的信封在修复后仍可解密；解密侧继续使用严格 UTF-8 解码，
非法字节仍归一为 `AuthenticationFailed`。

## 不承诺的内容

- **只拒绝未配对 surrogate。** 不做 NFC/NFKC 规范化、熵检查或复杂度限制，不禁止任何合法
  Unicode（含真实 U+FFFD、合法 surrogate pair、NUL）。
- **历史折叠数据。** 修复前靠替换回退用非法 rootKey/purpose 生成的信封不再受支持；库不猜测
  或恢复这类材料，合法 `sm:v1` 信封保持兼容。
- **根密钥生命周期。** 根密钥的来源、轮换与备份属于调用方；本边界不保证进程内存或第三方
  日志的清除，也不为同步加密增加耗时上限。
- **上游往返。** 数据库、HTTP 或加密上游能否往返非法 UTF-16 不属于本契约；本层只保证进入
  派生与加密的字符串良构。

## 如何被覆盖

`SensitiveValueUnicodeBoundaryTests`（核心）驱动：

- purpose / plaintext / rootKey 各自在有限矩阵上拒绝孤立 high、孤立 low、high+普通字符、
  反序 surrogate 与头/中/尾位置样本，断言固定消息、参数名、无 `InnerException` 与不回显。
- 修复前可跨上下文解密的 purpose（`p\uD800` / `p\uD801` 对 `p\uFFFD`）与 rootKey 折叠样本
  对被拒绝；合法且不同的 purpose/rootKey 仍为 `AuthenticationFailed`。
- ASCII、中文、合法 surrogate pair、真实 U+FFFD、NUL 与空 plaintext 正常往返；purpose 的
  `Trim` 与 1–128 长度限制保持。
- 修复前 main 生成的仅含合成材料的固定合法 `sm:v1` 向量在修复后仍可解密。
- 预取消且输入非法时交付携带调用方 token 的 OCE；共享 protector 的 64 操作并发混合有效与
  无效调用彼此独立，既有 `SensitiveValueProtectorTests` 全量保持。
