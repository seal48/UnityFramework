# 场景框架

统一走资源框架（YooAsset）加载场景，所以场景能跟着热更走。
切主场景时自动「关旧界面 → 开加载界面 → 等加载完 → 收尾回调」，业务只写一行。

## 一眼看懂

```
GameController.Awake   scene = new SceneLoader()                     只创建，不初始化
ProcedureInitScene     GameController.Instance.Scene.Init(...)       拿到资源服务 + UI
业务                   SceneKit.Load("Lobby");                       一行切场景（带进度条）
                        └ 关掉所有面板 → 开 LoadingPanel → 每帧喂进度
                          → 加载完激活场景 → 关 LoadingPanel → 回调
切场景                 UI Root 常驻，所以加载界面不会跟着场景一起没
退出                   GameController.OnDestroy -> scene.Shutdown()
```

## 用法

切主场景（Single，旧场景会被卸载）：

```csharp
SceneKit.Load("Lobby");                                   // Assets/Scenes/Lobby.unity
SceneKit.Load("Lobby", (ok, err) => { if (!ok) ... });    // 带回调

SceneLoadOptions options = new SceneLoadOptions();
options.Tip = "正在进入战斗…";
SceneKit.Load("Battle", options, OnLoaded);
```

叠加场景（Additive，不卸载当前场景；UI 场景 / 战斗子场景常用）：

```csharp
SceneKit.LoadAdditive("BattleUI");
SceneKit.Unload("BattleUI");
```

无缝切场景（先加载但不激活，自己挑时机切过去）：

```csharp
SceneLoadOptions options = new SceneLoadOptions();
options.SuspendLoad = true;              // 加载完不激活
options.ShowLoadingPanel = false;        // 自己控进度条
SceneKit.Load("Battle", options, (ok, err) =>
{
    // 资源都加载完了，这里做点准备，然后：
    SceneKit.Manager.ActivateLoadedScene();
});
```

查询：

```csharp
SceneKit.IsLoading;           // 是否正在加载
SceneKit.Progress;            // 0~1
SceneKit.CurrentSceneName;    // 当前场景名
SceneKit.Manager.PendingCount;
```

监听（逻辑层用，比回调适合多处关心）：

```csharp
GameController.Instance.Scene.LoadStarted  += location => { };
GameController.Instance.Scene.LoadFinished += (location, ok, err) => { };
```

## 配置

`GameController` Inspector 的「场景」就是 `SceneInitOptions`：

| 字段 | 作用 |
| --- | --- |
| `ShowLoadingPanel` | 切场景自动开关加载界面（总开关） |
| `LoadingPanelName` | 加载界面面板名，默认 `LoadingPanel` |
| `MinLoadingTime` | 加载界面最少停留秒数，默认 0.6，避免进度一闪而过 |
| `DefaultTip` | 默认提示文本 |
| `CloseAllPanelsOnSingle` | 切主场景前关掉所有面板 |
| `SceneFolder` | 短名从哪找，默认 `Assets/Scenes/`，`"Lobby"` → `Assets/Scenes/Lobby.unity` |
| `LogOnInit` | 初始化完打一条日志 |

单个请求还能用 `SceneLoadOptions` 覆盖（是否显示加载界面、提示文本、最短时间、是否挂起加载等）。

## 目录

| 文件 | 说明 |
| --- | --- |
| `SceneInitOptions.cs` | `SceneInitOptions`（全局）+ `SceneLoadOptions`（单次） |
| `SceneLoader.cs` | 管理器本体：排队 / 进度 / 加载界面 / 叠加场景 |
| `SceneKit.cs` | 全局快捷入口，业务代码用这个 |

资源侧配套（`ResourceFramework`）：`ISceneLoadOperation`、`YooSceneLoadOperation`，
`IResourceService.LoadSceneAsync(location, mode, suspendLoad)`。

## 注意

- **场景必须进 YooAsset 资源收集器**（`Assets/AssetBundleCollectorSetting.asset` 里的 `Scenes` 组，
  目录 `Assets/Scenes`，Filter `CollectScene`），否则加载不到。新增场景放这个目录就自动收集。
- 编辑器模拟模式（`EditorSimulate`）下 YooAsset 走 `EditorSceneManager` 加载，**只能在 Play 模式跑**；
  真机 / Offline / Host 模式走打包资源，改完场景要重新 Build。
- 一次只处理一个请求，同时来的排队（不会出现两个加载界面打架）。
- 加载界面会用 `UIManager.Hide` 收起来（保留实例），下次切场景直接复用。
- 命名空间是 `GameFramework.Scenes`（不是 `Scene`）——`Scene` 会和 `UnityEngine.SceneManagement.Scene`
  在 `GameFramework.*` 命名空间里打架。
- YooAsset 在加载新的 Single 场景时会**自己回收上一个场景句柄**，所以 `YooSceneLoadOperation` 每次访问句柄前都判 `IsValid`；不判的话会抛 `SceneHandle is invalid`，编辑器开了 Error Pause 会直接卡住整个游戏循环。