# AGENTS — 本仓库工作指引（DSH 每次会话自动加载）

> 这是给 AI 助手的工作指引，每次会话自动注入。**动手前先看「红线」。**
> 详细文档见 `ARCHITECTURE.md` 和各模块 `README.md`。

## 项目是什么

Unity 2021.3.26f1c1 手游框架：`Assets/Scripts/Framework/`（17 个模块、33 个程序集，可复用）+ `Assets/Scripts/Game/`（示例业务，换项目整个替换）。后端 `ServerHost/`（net8.0）与客户端**共享同一套纯 C# 代码**（协议/安全/计时内核/配置表）。

**代码热更（HybridCLR）**：业务层（`Game` / `GameFramework.Business` / `GameFramework.ServerSelect`）是**热更程序集**（IL2CPP 构建时编成 DLL 动态加载），框架层 AOT 留在包里。入口：场景挂 AOT 的 `HybridCLRBoot`（`Assets/Scripts/Boot/`），它加载热更 DLL 后反射调用 `Game.HotEntry.Boot()` → `GameController.CreateRoot()`。

## 依赖方向（红线 #1）

```
Game ──► Framework     单向；框架绝不允许引用 Game（asmdef 会编译报错）
AOT ──► 热更           不允许：AOT 代码不能静态引用热更程序集（反射可以）
```

- 入口 `Assets/Scripts/Game/GameController.cs`：创建并持有全部 manager；`Startup/` 一串 Procedure 是启动流程
- 生命周期契约在 `Framework/Core/GameModule.cs`：`IGameModule`（`IsInitialized`/`Shutdown` 幂等）、`ITickable`（`Tick(float,float)`），`GameController.Update` 显式顺序驱动

## 程序集（33 个）

- **纯 C#（`noEngineReferences:true`，= `ServerHost/GameServerHost.csproj` 编译范围，两边必须一致）**：
  `Core, Security, Timer.Core, Config(Runtime+Generated), Net.Shared, Net.Server, Net.Client, Net.ServerHost`
  —— 往这些里写 `using UnityEngine;` 会**编译报错**（这是特性不是 bug）。
- **热更程序集（HybridCLR，真机编成 DLL 加载）**：`Game`、`GameFramework.Business`、`GameFramework.ServerSelect`
- **AOT 引导**：`HybridCLRBoot`（场景挂载，含 `GameFramework.Boot.GameOptions` 启动配置资产）
- 其余按模块各一个 asmdef；Editor 程序集 `includePlatforms:["Editor"]`；`ConfigManager` 在 `Config/Unity/`。

## 红线（改代码前必读）

1. **依赖方向**：框架不依赖 Game；`GameController` 只在 `Game/` 里出现。
2. **别手动改生成物**：`Config/Generated/`、`ConfigData/`、`ConfigDataServer/`、`Assets/StreamingAssets/yoo/` 都是生成的，改表后跑 `Tools/配置表/导出全部表` 重生成。
3. **partial 类不能跨程序集**：`ConfigDatabase` 声明在 `Config/Runtime/`、实现在 `Generated/`，必须同程序集——别再拆。
4. **改了 asmdef / 移动脚本目录 → 必须重建资源包**：`Tools/YooAsset/2. 构建资源` → `菜单 6 同步` → 清 `yoo/` 缓存。否则旧 bundle 的 MonoBehaviour 引用失效（现象：卡 UI 初始化 + 报「根节点没有 Panel 组件」）。
5. **环境配置在 `Assets/Resources/EnvironmentConfig.asset`（唯一数据源），不在场景里**。命名空间是复数 `GameFramework.Environments`（单数会遮蔽 `System.Environment`，历史坑）。
6. **服务端共享代码保持纯 C#**（见程序集一节）。
7. **敏感数据不入库**：`LocalData/`（存档含密码）、`GameData/`（账号库）已被 `.gitignore` 排除，别加回来。
8. **热更程序集不能被场景/预制体序列化**（场景脚本必须 AOT）。GameController 不在场景里，由 `HybridCLRBoot` 运行时创建。
9. **改完热更程序集代码 → 重新编译热更 DLL 并拷到设备目录**：`HybridCLR/CompileDll/ActiveBuildTarget` → 把 `HybridCLRData/HotUpdateDlls/<平台>/` 下 `Game.dll`、`GameFramework.Business.dll`、`GameFramework.ServerSelect.dll` 拷到 `Assets/StreamingAssets/HotUpdate/`（再构建资源包）。编辑器里不加载 DLL（直接用编译进编辑器的程序集），所以编辑器改代码不用重编 DLL。
10. **别动 `Assets/HybridCLRGenerate/`**（AOTGenericReferences.cs / link.xml，由 `HybridCLR/Generate/All` 生成）。

## 常用工作流

| 事情 | 做法 |
|---|---|
| 改 Excel 表 | `Tools/配置表/导出全部表` → 重建资源（红线 4） |
| 改预制体/资源 | `Tools/YooAsset/2` + `6` + 清 `yoo/` |
| 切环境 | `Tools/环境/切换到/开发\|测试\|正式`；打包 `Tools/环境/构建/...` |
| 起后端 | `start-server.cmd`（7777）；自测 `--selftest` |
| UI 字段绑定 | `Tools/UI/绑定/生成全部面板的绑定代码`（面板类要 `partial`） |
| 改业务代码（热更） | `HybridCLR/CompileDll/ActiveBuildTarget` → 拷 3 个 DLL 到 `StreamingAssets/HotUpdate/` → 重建资源 |
| HybridCLR 元数据 | 改 AOT 程序集后跑 `HybridCLR/Generate/All` |
| UI 图集打包 | 把 Sprite 图片放进 `Assets/UI/Atlas/<图集名>/` → 菜单 `Tools/UI/图集/从目录创建图集` → 重建资源 |
| 图集配表 | `Assets/Config/Excel/UISprite.xlsx` 加一行（Id 逻辑ID → Atlas + Sprite）→ `Tools/配置表/导出全部表` → 重建资源 |
| 运行时取图 | `GameController.Instance.SpriteAtlases.GetSpriteById("逻辑ID")`（或 GetSpriteByIdAsync）；图集在 bundle 里可热更 |

## 框架能力（一句话）

Log 分级落盘 / Event 总线 / Timer 计时(纯C#) / Procedure 流程 / Storage 存档(AES+HMAC) / Security 加密(纯C#) / Resource YooAsset热更+CRC / Net 协议加密+心跳+断线重连+服务端 / UI Panel+层级+绑定生成 / Config Excel工具链 / Localization 多语言 / Audio BGM+音效池 / Scene 进度加载 / Pool 对象池 / Platform 生命周期+安全区+权限+返回键 / Environments 多环境 / Core 契约 / **HybridCLR 代码热更** / **UI 图集（SpriteAtlasManager）**。
