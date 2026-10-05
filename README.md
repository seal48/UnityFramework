# MCP — Unity 游戏框架 + 示例业务

一个**通用 Unity 手游框架**（`Framework/`）+ 一份**示例业务**（`Game/`），
后端服务端与客户端**共享同一套纯 C# 代码**（协议 / 安全 / 计时内核 / 配置表）。

> 框架部分可以整体搬到别的项目；`Game/` 换项目时整个替换。
> 详细的分层约定、依赖方向、模块清单见 [`Assets/Scripts/README.md`](Assets/Scripts/README.md)。

## 环境要求

| | 版本 |
|---|---|
| Unity | **2021.3.26f1c1**（含 Android Build Support 模块） |
| .NET SDK | **8.0**（编译 / 运行服务端） |
| 平台 | Windows（开发机）；构建目标为 Android |

## 目录结构

```
Assets/
  Scripts/
    Framework/        通用框架（17 个模块，32 个程序集）
    Game/             示例业务：GameController + 启动流程 + UI + 业务系统
  Config/             Excel 配置表源文件（策划改这里）
  ConfigData/         客户端配置表产物（.bytes）
  ConfigDataServer/   服务端配置表产物（.bytes）
  Prefabs/ Scenes/ Audio/ Fonts/ ...
Packages/             内嵌 YooAsset（含 asmdef）
ServerHost/           后端服务端（net8.0 控制台程序，与客户端共享纯 C# 代码）
Tools/ConfigTool/     配置表导出工具（Excel -> 代码 + .bytes）
start-server.cmd      一键启动后端
```

## 快速开始

**1. 起后端**

```bat
start-server.cmd                            :: 127.0.0.1:7777（被占用会自动往后找端口）
start-server.cmd --port 9000                :: 指定端口
start-server.cmd --selftest                 :: 自测（安全 / 加密 / 协议）
start-server.cmd --frametest                :: 帧同步自测
start-server.cmd --protocoltest             :: 协议自测
start-server.cmd --pushtest                 :: 推送自测
```

**2. 用 Unity 打开工程**，打开 `Assets/Scenes/SampleScene.unity`，直接 Play。

登录界面会预填上次的账号；第一次跑可以点「注册」。

> 首次运行需要资源包。如果卡在加载界面，按下面「资源构建」走一遍。

## 常用工作流

### 配置表（改 Excel 后）

```
菜单 Tools/配置表/导出全部表
```
Excel 放在 `Assets/Config/Excel/`，产出：生成代码 + `Assets/ConfigData/*.bytes` +
`Assets/ConfigDataServer/*.bytes` + `Assets/Config/Json/*.json`。
表头约定（`##var` / `##type` / `##group` / `##`）见 `Assets/Scripts/Framework/Config/README.md`。

### 资源（改了预制体 / 资源 / 配置表后**必须做**）

```
1) 菜单 Tools/YooAsset/1. 配置资源收集器（首次运行）
2) 菜单 Tools/YooAsset/2. 构建资源（Android + 拷进首包）
3) 菜单 Tools/YooAsset/6. 同步 Android 产物到本地服务器目录
```

资源站是仓库外的本地静态服务器（`127.0.0.1:8000`，根目录即 `ServerData/Android`）。
改了资源忘了同步，客户端会**静默加载旧资源** —— 排查时先清 `yoo/` 缓存再试。

> ⚠️ **动了 asmdef 或移动脚本目录后必须重建资源包**，否则已构建的 bundle 里
> MonoBehaviour 引用会失效（现象：卡在 UI 初始化 + 报「根节点没有 Panel 组件」）。

### 多环境（开发 / 测试 / 正式）

```
菜单 Tools/环境/切换到/开发｜测试｜正式      :: 编辑器里切
菜单 Tools/环境/当前环境                     :: 看当前完整配置
菜单 Tools/环境/构建/开发｜测试｜正式         :: 按环境打包 -> Builds/<环境>/
```

服务器地址、资源地址、日志级别、存档目录都在
`Assets/Resources/EnvironmentConfig.asset` 里，**不在场景里**。
命令行出包：

```bat
Unity.exe -quit -batchmode -projectPath <工程> ^
  -executeMethod GameFramework.Environments.Editor.EnvironmentBuilder.BuildFromCommandLine ^
  -environment Production -buildTarget Android
```

### UI 图集（SpriteAtlas）

```
1) 把 Sprite 图片放进 Assets/UI/Atlas/<图集名>/  （一个子目录 = 一张图集）
2) 菜单 Tools/UI/图集/从目录创建图集          （生成 <图集名>.spriteatlas）
3) 重建资源包（Tools/YooAsset/2 + 6 + 清 yoo/）
4) 运行时取图：
     GameController.Instance.SpriteAtlases.GetSprite("图集名", "图片名")     // 已加载则同步
     GameController.Instance.SpriteAtlases.GetSpriteAsync("图集名", "图片名", s => ...)  // 异步
```

### UI 绑定代码生成

```
菜单 Tools/UI/绑定/生成全部面板的绑定代码
菜单 Tools/UI/绑定/校验全部面板（只检查不生成）
```
扫预制体生成 `XxxPanel.Bindings.g.cs` 里的字段声明，免去运行时按字符串查找。
面板类需要是 `partial`。

### 代码热更（HybridCLR）

业务层（`Game` / `GameFramework.Business` / `GameFramework.ServerSelect`）是**热更程序集**，
真机 IL2CPP 构建时编成 DLL 从 `StreamingAssets/HotUpdate/` 动态加载；框架层 AOT 留在包里。
入口：场景挂 AOT 的 `HybridCLRBoot` → 加载热更 DLL → 反射 `Game.HotEntry.Boot()` → `GameController.CreateRoot()`。

```
改业务代码后出包：
1) 菜单 HybridCLR/CompileDll/ActiveBuildTarget       编译热更 DLL
2) 把 HybridCLRData/HotUpdateDlls/<平台>/ 下的 Game.dll /
   GameFramework.Business.dll / GameFramework.ServerSelect.dll
   拷到 Assets/StreamingAssets/HotUpdate/
3) 重建资源包（Tools/YooAsset/2 + 6 + 清 yoo/ 缓存）
4) Tools/环境/构建/<环境> 出包
```

> 编辑器里不加载热更 DLL（直接用编译进编辑器的程序集），所以编辑器改代码不用重编 DLL。
> 改 AOT 程序集后跑 `HybridCLR/Generate/All` 重新生成 `Assets/HybridCLRGenerate/`（别手动改）。
> 详见 [`Assets/Scripts/Boot/README.md`](Assets/Scripts/Boot/README.md)。

## 框架能力

| 模块 | 说明 |
|---|---|
| `LogFramework` | 分级日志、多出口（控制台 / 文件 / 内存）、定时落盘 |
| `EventFramework` | 事件总线（同步 Publish / 排队 Post） |
| `TimerFramework` | 计时器 / 调度，纯 C# 内核可与服务端共用 |
| `ProcedureFramework` | 启动流程状态机 |
| `StorageFramework` | 本地存档（设置 / 账号 / 缓存），AES + HMAC 加密 |
| `SecurityFramework` | 协议与存档加解密（纯 C#） |
| `ResourceFramework` | YooAsset 封装、远端热更、下载进度、CRC 校验 |
| `NetFramework` | 协议编解码、客户端（心跳 / 断线重连）、服务端、区服 |
| `UIFramework` | Panel / 层级 / 异步加载 / 绑定代码生成 / SpriteAtlas 图集取图 |
| `Config` | Excel -> 代码 + bytes 工具链，客户端服务端同一份表 |
| `LocalizationFramework` | 多语言：文本 / 图片 / 字体，一个 ID 对应各语言内容 |
| `AudioFramework` | BGM 淡入淡出、音效通道池 |
| `SceneFramework` | 场景加载（带进度条 / 排队 / 附加场景） |
| `PoolFramework` | 对象池（GameObject / 普通对象） |
| `PlatformFramework` | 生命周期、安全区、权限、返回键、回前台断线处理 |
| `Environments` | 多环境配置与构建时切换 |
| `Core` | 跨模块契约（`IGameModule` / `ITickable`） |
| `HybridCLRBoot` | HybridCLR 代码热更引导（AOT，场景挂载）+ `GameOptions` 启动配置 |

## 程序集划分

32 个程序集，纯 C# 部分（服务端也编译）标了 `noEngineReferences: true`：
谁往里面写 `using UnityEngine;` 会**立刻编译报错**，而不是等服务端 `dotnet build` 才发现。

详见 [`Assets/Scripts/README.md`](Assets/Scripts/README.md)。

## 不提交的内容

`Library/` `Temp/` `Builds/` `Bundles/` `ServerData/` `GameData/` `GameLogs/`
`LocalData/` `yoo/` 以及 Unity 自动生成的 `*.csproj` / `*.sln`。

其中两个是**数据安全**相关，务必保持忽略：

- `LocalData/` —— 本地存档，含登录密码 / token
- `GameData/` —— 服务端账号库（SQLite）

## 许可

[MIT](LICENSE)。随便用，保留版权声明即可。

> 附注：源码里出现的 `"dev"/"dev"`（默认登录账号）和 `"pwd123456"`（`--selftest` 演示账号）都是**本地内置演示服务器**的测试凭据，不是任何线上系统的真实口令；任何人 clone 下来都能看到并用它们连本地服，无需担心。
