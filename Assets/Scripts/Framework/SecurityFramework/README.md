# 安全框架（SecurityFramework）

三层实用级防护：**协议加密、存档加密、资源校验（防改包）**。

## ⚠️ 安全边界（先读这个）

这是**「防君子不防贼」的实用级方案**：

| 挡得住 | 挡不住 |
|---|---|
| 改 JSON 存档 / 直接看存档明文 | 逆向高手（IDA / Frida 解出密钥） |
| 抓包看协议明文 | 专业的改包 / 内存修改 |
| 非专业玩家改数据 / 改资源 | 花大成本的定向攻击 |

密钥硬编码在包里（拆段 + 运算混淆，搜不到完整明文），只是提高门槛。真正要强安全，
需要服务端下发会话密钥、加固 / 混淆、甚至服务端权威校验 —— 那是更高成本的方向，
**别拿这套当正式对抗外挂的方案**。它要解决的是「大多数玩家不会改，改了也改不明白」。

## 1. 协议加密（AES-CBC，前后端共用）

- **位置**：`FrameCodec` —— 加密「消息体」字节，包头（协议名 + 长度）保持明文，方便拆包 / 路由。
- **实现**：`NetFramework/Shared/Protocol/FrameCodec.cs` 编解码时调 `SymmetricCrypto`。
- **纯 C#**（`System.Security.Cryptography`），Unity 客户端和后端 ServerHost 编译**同一份**代码，天然一致。
- **开关**：`NetConfig.EnableProtocolEncryption`（默认 `true`）。联调抓包可临时关掉，但**前后端必须一致**。
- 加密会让 body 变长（IV 16 字节 + PKCS7 填充），长度校验放在加密后。

```csharp
// 使用方无感：Send / Router 照旧，加密在 FrameCodec 内部完成
peer.Send(ProtocolId.PlayerMove, new MoveRequest { X = 1 });
```

**验证**：`ServerHost --frametest` 覆盖 整帧 / 半包 / 粘包 / 非法报文 / **篡改 1 字节被拒**。
`ServerHost --selftest` 覆盖登录 / 注册 / 心跳 / 推送 / 超时全链路（加密下）。

## 2. 存档加密（AES，客户端本地）

- **位置**：`StorageFile.LoadFrom` / `WriteTo` —— 写盘前加密字节，读盘后解密再反序列化。
- **实现**：`StorageFramework/StorageFile.cs` 调 `SymmetricCrypto`。
- **开关**：`StorageOptions.EnableEncryption`（默认 `true`）。关闭则写明文，方便调试直接看存档。
- **损坏处理**：解密失败（头不对 / 被改 / 老明文档）→ 走现有 `.bad` 备份 + 重建逻辑，不会崩。

```csharp
// 业务无感：Storage.Save / 读盘 照旧
GameController.Instance.Storage.NotifySettingsChanged();  // 落盘自动加密
```

⚠️ **破坏性变更**：开启加密后，旧的**明文存档**会被当成损坏重建（`.bad` 备份）。开发期无所谓；上线前做好玩家存档迁移策略（或者干脆上线前才开加密）。

## 3. 资源校验（防改包，YooAsset 内置）

- **原理**：打包时 YooAsset 给每个 bundle 算好 `FileSize + FileCRC` 写进清单；
  运行时加载前按 `FileVerifyLevel` 校验。
- **实现**：`ResourceInitOptions.FileVerifyLevel`（默认 `2` = `High`，CRC 校验）。
  `YooAssetResourceService` 初始化时把它传给 buildin / cache 文件系统。
- 改 bundle / 热更包被篡改 → CRC 不过 → 加载失败（不是静默坏数据）。

| 级别 | 校验 |
|---|---|
| `1` Low | 只查文件大小 |
| `2` Middle | 大小 + 存在性（YooAsset 默认） |
| `3` High | **CRC 校验（防改包，推荐）** |

- 配置表另有 `schemaHash`（结构哈希），表结构和代码不一致直接报错，防「改了表没重导出」。
- **注意**：CRC 校验防的是「改文件」，挡不住「整个包换成改过的版本」（重打包）。那需要 APK 签名 / 服务端版本白名单，超出本框架范围。

## 4. 密钥管理（当前做法 + 边界）

- `SymmetricCrypto.BuildKey()`：三段字符串异或拼接出 128-bit 密钥，避免包里直接搜到完整密钥。
- 所有端（客户端 / ServerHost / ConfigTool）编译同一份 `SymmetricCrypto.cs`，密钥一致才能互解。
- **换密钥 = 改 `BuildKey()` + 全端重编译 + 老存档作废（解密失败重建）+ 老协议作废**。
  所以上线后别随便换密钥。

## 5. 目录

| 文件 | 作用 |
|---|---|
| `SymmetricCrypto.cs` | AES-128-CBC + PKCS7 + Magic 头，纯 C# 无 UnityEngine |
| `NetFramework/Shared/Protocol/FrameCodec.cs` | 协议 body 加密 / 解密（前后端共用） |
| `NetFramework/Shared/NetConfig.cs` | `EnableProtocolEncryption` 开关 |
| `StorageFramework/StorageFile.cs` | 存档读写加密 |
| `StorageFramework/StorageOptions.cs` | `EnableEncryption` 开关 |
| `ResourceFramework/ResourceTypes.cs` | `FileVerifyLevel`（bundle CRC 校验） |
| `ResourceFramework/YooAssetResourceService.cs` | 把校验级别传给 YooAsset |

## 6. 自检

```bash
# 协议层：整帧 / 半包 / 粘包 / 非法报文 / 篡改检测
ServerHost --frametest

# 全链路：登录 / 注册 / 心跳 / 推送 / 超时（加密下）
ServerHost --selftest

# 配置表：结构哈希 / 分端字段 / 防脏数据
ServerHost --cfgtest
```
