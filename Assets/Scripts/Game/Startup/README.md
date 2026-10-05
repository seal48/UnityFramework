# 启动流程（Procedure / 状态机）

启动顺序不再写在 `GameController` 的回调里，而是一段段看得见的流程。
内核是 `Assets/Scripts/Framework/ProcedureFramework/`（纯框架，不依赖任何业务），
具体阶段在 `Assets/Scripts/Game/Startup/`（项目自己的启动流程）。

## 目前的阶段

| 阶段 | 干什么 | 谁把它切走 |
| --- | --- | --- |
| `ProcedureLaunch` | 进程级准备：网络调度器初始化 | 自己切 `InitStorage` |
| `ProcedureInitStorage` | 读本地设置 / 账号 / 缓存，并把已保存的设置应用到引擎（详见 `Assets/Scripts/Framework/StorageFramework/README.md`） | 存储回调（失败也继续：存储不是硬依赖） |
| `ProcedureInitResource` | 资源系统初始化 + **加载界面**（Host 模式下就是版本检查 + 热更下载） | 资源回调成功且加载界面显示够时长 |
| `ProcedureInitConfig` | 加载配置表（`Assets/ConfigData/*.bytes` 经资源系统读入） | 配置回调成功 |
| `ProcedureInitPool` | 创建对象池根节点，并按配置预热（详见 `Assets/Scripts/Framework/PoolFramework/README.md`） | 池回调成功 |
| `ProcedureInitUI` | 加载 UIRoot 预制体并注册所有面板 | UI 回调成功 |
| `ProcedureInitScene` | 初始化场景管理（依赖资源系统 + UI），之后切场景就能带进度条（详见 `Assets/Scripts/Framework/SceneFramework/README.md`） | 场景回调成功 |
| `ProcedureInitAudio` | 建 `[Audio]` 根节点、铺 BGM / 音效通道；排在 UI 之后，界面一打开就能播声音（详见 `Assets/Scripts/Framework/AudioFramework/README.md`） | 音频回调成功 |
| `ProcedureLogin` | 打开 `LoginPanel`，等玩家登录 | `gameClient.LoggedIn` 事件 |
| `ProcedureEnterLobby` | 关登录界面、异步加载 `Lobby` 场景 | 场景加载完成 |
| `ProcedureLobby` | 大厅常驻阶段（以后开大厅主界面、接业务消息） | 常驻 |

驱动方式：`GameController.Update` → `Procedures.Tick(...)`，只有当前流程会收到 `OnUpdate`。

帧率 / 画质 / 音量不再写死在 `ProcedureLaunch` 里，统一由 `ProcedureInitStorage` 读本地设置后应用。

## 怎么加一个新阶段

```csharp
using GameFramework.Procedure;
using UnityEngine;

namespace GameFramework.Startup
{
    public sealed class ProcedureCreateRole : ProcedureBase
    {
        public override void OnEnter(ProcedureBase from)
        {
            // 开界面、发协议、起协程…然后：
            // ChangeState<ProcedureEnterLobby>();
        }

        public override void OnUpdate(float deltaTime, float unscaledDeltaTime) { }
        public override void OnLeave(ProcedureBase to) { }
    }
}
```

1. 新建类继承 `ProcedureBase`；
2. 在 `GameController.Awake` 里把它 `new` 出来登记到 `ProcedureManager`；
3. 在合适的位置 `ChangeState<T>()` 切过去。

`ChangeState` 可以放心在 `OnEnter` 或异步回调里调用（内部用排队处理，不会递归）。

## 注意

- `GameController` 在 `Awake` 里 `DontDestroyOnLoad`，切场景不会丢：网络连接、UIRoot、流程状态都活着。
- 场景必须在 **Build Settings** 里（目前是 `SampleScene`(0) + `Lobby`(1)），否则 `LoadSceneAsync` 会失败。
- 场景内不要再放 `GameController`，只放启动场景那一份。
- `ProcedureInitResource` 失败会停在本阶段，按 **R** 可以重试（开发期方便调试）。

## 加载界面（LoadingPanel）的时序

加载界面要显示「热更进度」，但界面本身也是资源，所以顺序上有个硬约束：

```
InitializeAsync（读首包清单）
  → 请求版本 → 更新清单 → 清单激活
  → BuiltinReady 事件 ──► 初始化 UI + 打开 LoadingPanel   ← 这一步之后「随包内置」的资源才加载得了
  → 创建下载器 → 下载差异资源（进度实时推给 LoadingPanel）
  → 全部就绪 → 关掉 LoadingPanel → ProcedureInitUI → ProcedureLogin
```

几个关键点：

- **LoadingPanel 必须打进首包**：热更还没跑完时只能加载首包里的东西。
  改完预制体要跑一次 `Tools/YooAsset/2. 构建资源（Android + 拷进首包）`，再跑
  `Tools/YooAsset/6. 同步 Android 产物到本地服务器目录`。
- 进度是靠 `IResourceService.DownloadProgressChanged` 推的，`ProcedureInitResource` 收到后调
  `LoadingPanel.SetHotUpdateProgress(report)`。
- 有「最短显示时间」`MinLoadingTime`（默认 1 秒），免得热更太快时界面一闪而过。
- 流程会**等加载界面真正打开**才开始计时，避免「流程都进登录了，加载界面才冒出来」。
- UI 初始化失败或面板没打开时不会卡死，会直接跳过等待继续走。

## 关于「先热更再重启」

首包里的 LoadingPanel 是**启动用的那一个**（简单、稳定优先）。热更下来的资源里可以带一个更完整的加载界面，
重启后再显示它——这也是很多商业项目的做法。当前框架已经具备这个能力：
`BuiltinReady` 之前用的一定是首包的界面，`PackageVersion` 变成远端版本之后加载到的就是更新后的资源。
「热更完成后主动重启」还没做，要做的话在 `ProcedureInitResource` 成功后调一次重启
（或退出进程让玩家重进），并把新版本写成新的首包。
