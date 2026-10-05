# Assets/Scripts 目录约定

分两层：**`Framework/` 通用框架** + **`Game/` 本项目业务**。

```
Assets/Scripts/
├── Framework/          # 通用框架：可整体复用到别的项目，不含任何本项目内容
│   ├── LogFramework/           日志（分级 / 落盘 / 控制台）
│   ├── EventFramework/         事件总线（模块解耦）
│   ├── TimerFramework/         计时器 / 调度
│   ├── ProcedureFramework/     流程状态机（启动流程的内核）
│   ├── StorageFramework/       本地存档（设置 / 账号 / 缓存，AES 加密）
│   ├── SecurityFramework/      安全（协议 & 存档加密，纯 C#）
│   ├── ResourceFramework/      资源（YooAsset 封装 + IResourceService 接口）
│   ├── PoolFramework/          对象池
│   ├── AudioFramework/         音频
│   ├── SceneFramework/         场景加载（带进度条）
│   ├── UIFramework/            UI 框架（Panel / UIManager / UIRoot，只有设施）
│   ├── NetFramework/           网络（协议 / 客户端 / 服务端 + 后端程序 ServerHost）
│   ├── Config/                 配置表（Excel → 生成代码 + .bytes）
│   ├── LocalizationFramework/  多语言（文本 / 图片 / 字体）
│   ├── PlatformFramework/      平台适配（生命周期 / 安全区 / 权限 / 断线重连）
│   ├── Environments/           多环境配置（服务器 / 资源地址 / 日志级别，见该目录 README）
│   └── Core/                   跨模块契约（IGameModule / ITickable）
└── Game/               # 本项目业务：换项目时整个替换
    ├── Business/               业务系统（PlayerSystem / BagSystem / PlayerModel）
    ├── ServerSelect/           区服列表 / 选服
    ├── Startup/                启动流程（各 Procedure） 
    ├── GameController.cs       全局入口：创建并持有所有 manager
    └── UI/                     具体游戏界面（LoginPanel / MainPanel / LoadingPanel / SelectServerPanel）
```

## 判断标准

| 放哪 | 标准 |
|---|---|
| `Framework/` | 换个游戏还能用；不含任何具体玩法、界面、配置内容 |
| `Game/` | 跟本项目玩法 / 界面 / 数据绑定，换项目要重写 |

**具体游戏界面属于业务** —— 所以 `LoginPanel` 这类放在 `Game/UI/`，`Framework/UIFramework/` 只留
`Panel`（基类）/ `UIManager` / `UIRoot` / `UIAnim` 这些与玩法无关的设施。

## 模块生命周期约定（统一契约）

所有由 `GameController` 持有并驱动的管理器，都实现 `Framework/Core/GameModule.cs` 里的契约：

```csharp
public interface IGameModule
{
    bool IsInitialized { get; }   // 初始化完成后 true
    void Shutdown();              // 关闭并释放，必须幂等
}

public interface ITickable
{
    void Tick(float deltaTime, float unscaledDeltaTime);   // 每帧由 GameController.Update 驱动
}
```

| 阶段 | 约定 |
|---|---|
| **Init** | 参数各模块自定（依赖什么传什么）。异步的带 `Action<bool, string> onComplete` 回调，同步的不带。由启动流程里的 Procedure 依次调 |
| **IsInitialized** | 初始化完成后为 true。没就绪时调别的 API 行为不保证 |
| **Tick** | **签名一律 `Tick(float deltaTime, float unscaledDeltaTime)`**。用不到时间的忽略参数即可 —— 统一是为了驱动方不用记"每个模块该传什么" |
| **Shutdown** | **命名一律 `Shutdown()`**，且必须幂等。由 `GameController.OnDestroy` 按依赖倒序调（后创建的先关，存储 / 日志最后关） |
| **Dispose** | 同时实现 `IDisposable` 的模块，`Dispose()` 转调 `Shutdown()` —— 两种写法行为一致，`using` 也能用 |

**当前实现情况**：14 个模块全部实现 `IGameModule`，其中 9 个需要每帧驱动，实现 `ITickable`。

```csharp
// 新增一个管理器时照这个来
public sealed class FooManager : IGameModule, ITickable
{
    public bool IsInitialized { get; private set; }

    public void Init(FooOptions options, Action<bool, string> onComplete)
    {
        // ... 初始化
        IsInitialized = true;
        if (onComplete != null) onComplete(true, "foo ready");
    }

    public void Tick(float deltaTime, float unscaledDeltaTime) { /* 每帧 */ }

    public void Shutdown()
    {
        if (!IsInitialized) return;   // 幂等
        IsInitialized = false;
        // ... 释放
    }
}
```

> `GameController.Update` 里各模块的调用**顺序是有意的**（日志 → 计时 → 流程 → UI → 平台 → 池 → 场景 → 音频 → 存储 → 事件），
> 所以保留显式顺序，没有改成遍历列表。

## 依赖方向（不要反向引用）

```
Game  ──►  Framework        （业务依赖框架，框架不依赖业务）
```

`Framework/` 里的代码**不允许**出现 `Game/` 下的类型。三处方向异常已全部修正：

| 位置 | 原来 | 现状 |
|---|---|---|
| `Framework/TimerFramework` | 依赖 `NetFramework.Shared.Timing`（纯 C# 计时内核放在网络框架里） | ✅ 内核挪到 `TimerFramework/Core/`（命名空间 `GameFramework.Timer`） |
| `Framework/NetFramework/Client/Unity/ServerSelectManager.cs` | 网络框架里放了读区服表 / 写存档的**游戏逻辑** | ✅ 挪到 `Game/ServerSelect/`（命名空间 `GameFramework.ServerSelect`） |
| `Framework/UIFramework` | 混着具体游戏界面 | ✅ 挪到 `Game/UI/` |

**计时框架怎么做到不依赖网络的**：内核只认自己的日志口 `ITimerLogger`（在 `TimerFramework/Core/TimerLogging.cs`），
网络的 `INetLogger` **继承** `ITimerLogger` —— 所以：

```
NetFramework  ──►  TimerFramework      （网络用计时，方向正确）
```

服务端把同一个 logger 直接传给 `TimerScheduler` 即可，现有 55 处 `INetLogger` 用法一处都没改。

## asmdef（Assembly Definition）—— 已完成

**32 个程序集**，按 `Game → Framework` 的依赖方向分层。现在层次违规会**编译报错**，
而不是靠人自觉。最实在的一条收益：**服务端也要编译的纯 C# 代码加了 `noEngineReferences: true`**，
谁往里写 `using UnityEngine;` 会立刻报错，不用等服务端 `dotnet build` 才发现。

> 编译速度本来就不是瓶颈（162 个脚本约 1 秒）。asmdef 的价值在**边界**，不在速度。
> 附带好处：改 Game 层不会重编框架层。

### 纯 C# 程序集（`noEngineReferences: true`，服务端也编译这几个）

```
GameFramework.Security                  安全（协议 / 存档加密）
GameFramework.Timer.Core                计时内核
GameFramework.Config                    Runtime + Generated（配置表读写）
GameFramework.Net.Shared                协议 / 编解码 / 日志接口
GameFramework.Net.Server                服务端
GameFramework.Net.Client                客户端（不含 Unity 部分）
GameFramework.Net.ServerHost            本地服务器 + 静态资源站（控制台程序）
```

这 7 个正好等于 `ServerHost/GameServerHost.csproj` 的编译范围，**两边必须保持一致**。

### Unity 侧程序集

```
GameFramework.Core        跨模块契约（IGameModule / ITickable）
GameFramework.Log → Event / Timer / Storage / Resource / Pool / Audio / Scenes / Procedure
GameFramework.UI          + DOTween.Modules（DOTween 扩展方法）
GameFramework.Config.Unity      ConfigManager（用 TextAsset，所以不能进纯 C# 的 Config）
GameFramework.Localization / Platform / Environments
GameFramework.Net.Client.Unity  GameClientBehaviour（MonoBehaviour）
GameFramework.Business / ServerSelect
Game                        GameController + UI + Startup
+ 5 个 Editor 程序集（Config / Net / Resource / UI / Environments）
```

### 拆 asmdef 时暴露并修掉的 4 个真实问题

| 问题 | 修法 |
|---|---|
| `AudioKit` / `SceneKit`（框架）伸手拿 `GameController.Instance` | 反向登记：`AudioManager` / `SceneLoader` 在 Init 里把自己登记给 Kit，Shutdown 注销 |
| `LocalizedElement` / `LanguageToggle` 同样依赖 `GameController` | 用 `LocalizationManager.Current` + `EventBus.Global`；资源服务由 `LocalizationManager.Resource` 暴露 |
| `SceneLoader` 直接引用业务层的 `LoadingPanel` | 在 UI 框架定义 `IProgressPanel` 接口，`LoadingPanel` 实现它，加载器只认接口 |
| `Net.Client` 里有个没用到的 `using GameFramework.Net.Server` | 删掉（客户端不该依赖服务端） |
| `EventBus.Global` 的 setter 是 `internal`，拆开后 `Game` 程序集设不了 | setter 放开为 `public` 并写明"由持有者设置" |
| `ConfigDatabase` 是 partial 类，声明在 `Runtime/`、实现在 `Generated/` | **partial 类不能跨程序集** → 两者合成一个纯 C# 程序集，`ConfigManager` 挪到 `Config/Unity/` |

### 两个必须记住的坑

1. **partial 类不能跨程序集**。生成代码（`Generated/`）如果要用 partial 方法往手写类（`Runtime/`）里插逻辑，
   两者必须在同一个程序集里。这是把 `Config` 合成一个程序集的原因。

2. **改完 asmdef 必须重新构建资源包**。脚本换程序集后，**已经构建好的 YooAsset bundle 里的
   MonoBehaviour 引用会失效**（加载出来是 Missing Script）。现象是进游戏卡住 +
   `预制体 XXX 的根节点上没有 Panel 组件`。修法：
   `Tools/YooAsset/2. 构建资源` → `Tools/YooAsset/6. 同步 Android 产物到本地服务器目录` → 清掉 `yoo` 缓存。

> ⚠️ 搬迁目录时：`.cs` 必须和 `.cs.meta` **一起移动**（Unity 靠 GUID 引用）；文件夹自己的 `.meta` 在**父目录**。
> 硬编码路径有这几处：`ConfigExporter.GeneratedFolder`、`ServerHost/GameServerHost.csproj`、`Tools/ConfigTool/ConfigTool.csproj`。
> 这次新增的 `Config/Unity/` 不在服务端 csproj 的 glob 里（只 glob `Config/Runtime` 和 `Config/Generated`），所以是安全的。
