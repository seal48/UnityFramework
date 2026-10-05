# 多环境配置（Environments）

解决什么问题：以前**服务器地址 / 资源地址 / 日志级别**是序列化在场景（`SampleScene`）里的，
切环境要手动改场景、容易漏、也容易把正式包配错。现在这些值集中在**一个 ScriptableObject** 里，
编辑器一键切换，构建时按环境烤进包。

## 一眼看懂

```
Assets/Resources/EnvironmentConfig.asset      ← 唯一数据源（进包）
        │
        │  GameController.Awake → ApplyEnvironment()
        ▼
  服务器地址(gameClient) / 资源模式+地址 / 日志级别 / 推送日志 / 存档目录
```

## 三套环境

| | 开发 Development | 测试 Test | 正式 Production |
|---|---|---|---|
| 服务器 | `127.0.0.1:7777` | `127.0.0.1:7777` | `127.0.0.1:7777` |
| 资源模式 | Host | Host | Host |
| 资源主站 | `http://127.0.0.1:8000/Android/DefaultPackage` | 同左 | 同左 |
| 日志级别 | Debug | Info | Warn |
| 推送日志 | 开 | 关 | 关 |
| 存档目录 | 游戏目录旁（好翻看） | 平台标准目录 | 平台标准目录 |
| Bundle 校验 | High(CRC) | High | High |

> **测试 / 正式现在都指向本地服务器**，是为了开发期能直接切过去联调。
> 拿到真实的测试服 / 正式服地址后，改 `Assets/Resources/EnvironmentConfig.asset` 里这两个环境即可。
>
> 忘了改也不会静默出事：构建时会检查地址里有没有 `127.0.0.1` / `localhost` / `example.com` / `0.0.0.0` ——
> **正式环境命中打 Error**（构建日志里很显眼），测试环境打 Warning。

本地联调需要两个服务器都起着：

```
游戏服    start-server.cmd                → 127.0.0.1:7777
资源站    菜单 Tools/YooAsset 里的本地资源站  → 127.0.0.1:8000
改了资源  构建资源（菜单 2） + 同步产物到本地服务器目录（菜单 6）
```

## 怎么切

| 场景 | 做法 |
|---|---|
| **编辑器里试** | 菜单 `Tools/环境/切换到/开发｜测试｜正式`（当前环境带勾）。下次 Play 生效 |
| **看当前配置** | 菜单 `Tools/环境/当前环境`（打一份到 Console） |
| **改参数** | 菜单 `Tools/环境/打开配置资产`，或在 Project 里点 `Assets/Resources/EnvironmentConfig.asset` |
| **按环境打包** | 菜单 `Tools/环境/构建/开发｜测试｜正式`，产物在 `Builds/<环境>/` |
| **CI 命令行** | 见下 |

```bash
Unity.exe -quit -batchmode -projectPath <工程> \
  -executeMethod GameFramework.Environments.Editor.EnvironmentBuilder.BuildFromCommandLine \
  -environment Production \
  -buildTarget Android
```

`-environment` 接受 `Development` / `Test` / `Production`（也认 `开发` / `测试` / `正式`）。

**构建时切换是可靠的**：`EnvironmentConfig` 是 `Resources` 下的资产，构建时它的值会被序列化进包，
所以正式包不会意外连到开发服。构建脚本会在**构建前**写入目标环境、**构建后**还原编辑器里的选择
（包里已经是目标环境，还原只影响编辑器）。

## 覆盖范围（重要）

`GameController.Awake` 里 `ApplyEnvironment()` 会**覆盖**场景上的这些序列化值：

| 被覆盖 | 来自配置的字段 |
|---|---|
| 服务器地址 | `ServerHost` / `ServerPort` → `gameClient.SetServer(...)` |
| 资源模式 / 主站 / 备用站 / 校验级别 | `ResourceMode` / `ResourceHostUrl` / `ResourceFallbackUrl` / `FileVerifyLevel` |
| 日志最低级别 | `LogLevel`（`MinLevel` 和 `MinLevelInDevelopment` 都设成它，避免两套规则打架） |
| 推送日志开关 | `LogPushMessages` |
| 存档根目录 | `StorageInGameFolder`（游戏目录旁 / 平台标准目录） |

**不要再去改场景里的这些值** —— 会被环境配置覆盖。想临时调，就改配置资产（或加一套新环境）。

`UIInitOptions` / `PoolInitOptions` / `SceneInitOptions` / `AudioInitOptions` / `TimerInitOptions` /
`EventBusOptions` / `LocalizationOptions` / `PlatformOptions` **与环境无关**，仍然在场景里配。

**没有配置资产时**：`ApplyEnvironment()` 直接返回，一切退回场景里的默认值（并打一条警告）。
所以这个模块是可选增强，不是硬依赖。

## 加一套环境 / 加一个配置项

1. **加配置项**：在 `EnvironmentEntry` 里加字段 → 三套环境各填一次 →
   在 `GameController.ApplyEnvironment()` 里把它映射到对应的 options。
2. **加环境**（比如「预发布」）：在 `GameEnvironment` 枚举里加一项（**放在最后**，因为数组下标 = 枚举值）→
   在资产里把 `environments` 数组加大、补齐数据 → `EnvironmentConfig.Count` 同步改。

## 为什么命名空间是复数 `Environments`

`GameFramework.Environment` 会**遮蔽 `System.Environment`** —— 项目里 `NetLog.cs` 用了
`Environment.NewLine`，加了单数命名空间后整个 `GameFramework.*` 下所有 `Environment.xxx`
都会解析到我们的命名空间，直接编译失败（`CS0234`）。用复数 `Environments` 彻底避开。

## 文件

| 文件 | 作用 |
|---|---|
| `EnvironmentConfig.cs` | `GameEnvironment` 枚举 + `EnvironmentEntry` + SO（含 `Instance` 运行时加载） |
| `Editor/EnvironmentMenu.cs` | 切换环境 / 创建 / 查看配置资产 |
| `Editor/EnvironmentBuilder.cs` | 按环境构建（菜单 + `-executeMethod` 命令行入口） |
| `Assets/Resources/EnvironmentConfig.asset` | 配置资产本体 |
