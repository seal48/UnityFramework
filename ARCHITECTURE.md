# ARCHITECTURE — 架构速查（给 AI / 新人的一分钟版）

> 不用读全部源码和模块 README，看这一份就够开工。详细文档在各模块 `README.md`。
> 改动前**务必看「红线」一节**。

## 一句话

Unity 2021.3.26f1c1 手游框架：`Framework/`（17 个模块、33 个程序集，可复用）+ `Game/`（示例业务，换项目时整个替换）。后端 `ServerHost`（net8.0）与客户端**共享同一套纯 C# 代码**（协议/安全/计时/配置表）。**业务层走 HybridCLR 代码热更。**

## 目录与依赖方向（红线 #1）

```
Game  ──►  Framework         // 单向，框架**绝不允许**引用 Game
Framework/ 里不允许出现 Game/ 下的类型（asmdef 会编译报错）
AOT ──► 热更                 // AOT 代码不能静态引用热更程序集（反射可以）
```

- `Framework/` = 通用框架；`Game/` = 业务（GameController / Startup 流程 / UI / Business / ServerSelect）
- **启动链**：场景挂 AOT `HybridCLRBoot` → 加载热更 DLL（真机）/ 直接用编辑器程序集 → 反射 `Game.HotEntry.Boot()` → `GameController.CreateRoot()`（热更，运行时创建）→ 一串 Procedure
- 生命周期契约：`Framework/Core/GameModule.cs` 的 `IGameModule`（`IsInitialized`/`Shutdown` 幂等）+ `ITickable`（`Tick(float,float)`）；`GameController.Update` 显式顺序驱动

## 程序集（33 个）

- **纯 C#（`noEngineReferences: true`，= `ServerHost/GameServerHost.csproj` 的编译范围，两者必须一致）**：
  `Core, Security, Timer.Core, Config(Runtime+Generated), Net.Shared, Net.Server, Net.Client, Net.ServerHost`
  —— 往里写 `using UnityEngine;` 会**编译报错**。
- **热更程序集（HybridCLR）**：`Game`、`GameFramework.Business`、`GameFramework.ServerSelect` —— 真机编成 DLL 从 `StreamingAssets/HotUpdate/` 加载。
- **AOT 引导**：`HybridCLRBoot`（场景挂载）+ `GameFramework.Boot.GameOptions`（启动配置资产，替代原场景序列化选项）。
- 其余 Unity 侧按模块各一个；Editor 程序集 `includePlatforms:[Editor]`；`ConfigManager` 在 `Config/Unity/`。

## 红线（改代码前必读）

1. **依赖方向**：框架不依赖 Game；`GameController` 只在 `Game/` 里出现。
2. **别手动改 `Config/Generated/`、`ConfigData*`、`ConfigDataServer/`、`Assets/HybridCLRGenerate/`** —— 都是生成的，改表后重跑 `Tools/配置表/导出全部表`、改 AOT 后重跑 `HybridCLR/Generate/All`。
3. **partial 类不能跨程序集**：`ConfigDatabase` 的声明在 `Runtime/`、实现在 `Generated/`，所以两者必须同程序集 —— 别再拆。
4. **改了 asmdef / 移动了脚本目录 → 必须重建资源包**，否则旧 bundle 的 MonoBehaviour 引用失效（现象：卡 UI 初始化 + 报「根节点没有 Panel 组件」）。流程：`Tools/YooAsset/2. 构建资源` → `菜单 6 同步` → 清 `yoo/` 缓存。
5. **环境配置**：服务器地址 / 资源地址 / 日志级别在 `Assets/Resources/EnvironmentConfig.asset`（**唯一数据源**），不在场景里。命名空间是复数 **`GameFramework.Environments`**（单数会遮蔽 `System.Environment`，历史坑）。
6. **服务端共享代码必须保持纯 C#**：别往那 8 个程序集里引 UnityEngine。
7. **敏感数据不入库**：`LocalData/`（存档含密码）、`GameData/`（账号库）都被 .gitignore 排除。
8. **热更程序集不能被场景/预制体序列化**（场景脚本必须 AOT）—— GameController 不在场景里，由 Boot 运行时创建。
9. **改完热更代码 → 重编 DLL 拷设备目录**：`HybridCLR/CompileDll/ActiveBuildTarget` → 拷 `HybridCLRData/HotUpdateDlls/<平台>/` 下 `Game.dll`/`GameFramework.Business.dll`/`GameFramework.ServerSelect.dll` 到 `Assets/StreamingAssets/HotUpdate/` → 重建资源。编辑器改代码不用重编 DLL。

## 常用工作流

| 事情 | 做法 |
|---|---|
| 改 Excel 表 | `Tools/配置表/导出全部表` → 重建资源（见 4） |
| 改预制体/资源 | `Tools/YooAsset/2` + `6` + 清 `yoo/` |
| 切环境（开发/测试/正式） | `Tools/环境/切换到/...`；打包 `Tools/环境/构建/...` |
| 起后端 | `start-server.cmd`（7777，被占自动后移）；自测 `--selftest` |
| UI 字段绑定 | `Tools/UI/绑定/生成全部面板的绑定代码`（面板类要 `partial`） |
| 出包 | `Tools/环境/构建/<环境>` → `Builds/<环境>/`；CI 用 `-executeMethod ...BuildFromCommandLine -environment X` |
| **改业务代码（热更）** | `HybridCLR/CompileDll/ActiveBuildTarget` → 拷 3 个 DLL 到 `StreamingAssets/HotUpdate/` → 重建资源（红线 9） |
| **图集打包** | 图片放 `Assets/UI/Atlas/<名>/` → `Tools/UI/图集/从目录创建图集` → 重建资源；运行时 `SpriteAtlases.GetSprite(<名>,<图>)` |

## 框架能力（一句话各模块）

Log 分级落盘 / Event 总线 / Timer 计时内核(纯C#) / Procedure 流程 / Storage 存档(AES+HMAC) / Security 加密(纯C#) / Resource YooAsset热更+CRC / Net 协议加密+心跳+断线重连+服务端 / UI Panel+层级+绑定生成+**图集(SpriteAtlasManager)** / Config Excel工具链 / Localization 多语言(文本/图片/字体) / Audio BGM+音效池 / Scene 进度加载 / Pool 对象池 / Platform 生命周期+安全区+权限+返回键 / Environments 多环境 / Core 生命周期契约 / **HybridCLR 代码热更**。

## 依赖快照（谁引用谁）

```
Core ← 一切
Log ← Event/Timer/Storage/Resource/Pool/Audio/Scene/UI/Procedure/Localization/Platform/Environments/Config.Unity/Net.*(Unity侧)
Security ← Storage/Net.Shared/Net.*
Timer.Core ← Timer/Net.*
Config(pure) ← Config.Unity/Net.ServerHost/ServerSelect/Localization
Net.Shared ← Net.Client/Net.Server/Net.Client.Unity/Business/Game
UI ← Scene/Platform/Localization(经IProgressPanel) / Game
Game ← 全部（GameController 聚合；热更）
HybridCLRBoot ← 框架 + 反射进 Game（AOT 引导，不静态引用热更）
```
