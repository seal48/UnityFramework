# 平台适配层（PlatformFramework，Android）

Android 平台相关的一层封装：**App 生命周期（切后台 / 焦点）、系统返回键、安全区 / 刘海屏、
运行时权限申请、回前台断线自动重连**。由 `GameController` 创建并转发 Unity 生命周期，
业务代码通过 `GameController.Instance.Platform` 使用。

## 一眼看懂

```
GameController.Awake      platform = new PlatformManager(...)         只创建
ProcedureInitPlatform     GameController.Instance.Platform 就绪      依赖 UI / 存储 / 网络
GameController.Update     platform.Tick(...)                          驱动返回键 / 安全区 / 回前台处理
GameController 转发       OnApplicationPause / OnApplicationFocus    平台层收到通知并发事件
业务                      Platform.HasPermission / Platform.Request(...)  权限
退出                      GameController.OnDestroy -> platform.Shutdown()
```

## 1. App 生命周期（pause / focus）

`GameController.OnApplicationPause / OnApplicationFocus` 原样转给平台层，平台层维护状态并发事件：

| 事件 | 触发时机 |
| --- | --- |
| `PlatformApplicationPausedEvent` | `OnApplicationPause(true)`（切后台） |
| `PlatformApplicationResumed`（就是 `Paused=false`） | `OnApplicationPause(false)`（回前台） |
| `PlatformApplicationFocusedEvent` | `OnApplicationFocus` 变化 |

```csharp
Listen<PlatformApplicationPausedEvent>(e => { if (e.Paused) 停掉战斗计时; else 恢复; });
```

注意：**权限弹窗等系统对话框也会触发 pause + focus 丢失**，但通常不到 1 秒。
平台层用 `PlatformOptions.BackgroundThresholdSeconds`（默认 1.5s）区分「真切后台」和「系统对话框」，
只有前者才会触发断线重连。订阅事件做业务逻辑没问题；判断「是否真的离开过 App」请用
`Platform.IsPaused` + 暂停时长语义，或直接听 `PlatformSessionLostEvent`。

## 2. 返回键（Android Back / 桌面 Escape）

`PlatformOptions.TrackBackKey = true` 时生效（编辑器里用 Escape 模拟）。

处理顺序：

1. 发 `PlatformBackPressedEvent`（播音效 / 统计用）；
2. 问一遍注册的拦截器（`AddBackHandler(Func<bool>)`，后注册的先问）——返回 `true` 表示已消费；
3. 默认逻辑：关掉最上层的 **Popup / Top** 层级面板（`UIManager.Close`）；
4. 没有可关的面板（根界面）：触发 `BackOnRoot` 事件，并按 `PlatformOptions.BackOnRoot` 处理
   （`MinimizeApp` 默认把 App 退到后台 / `DoNothing` 只发事件）。

```csharp
var platform = GameController.Instance.Platform;

// 例：根界面按返回键弹「确认退出」（用拦截器，不然默认会退后台）
platform.AddBackHandler(() =>
{
    if (GameController.Instance.UI.IsOpen("ConfirmExitPanel")) return false; // 已有弹窗，交给默认逻辑关
    GameController.Instance.UI.Open("ConfirmExitPanel");
    return true;
});
```

要点：

- 需要「连按两次返回退出」这类交互：在拦截器里计数即可，不要用 `BackOnRoot`（它拦不住默认行为）；
- 想彻底接管返回键：拦截器全部返回 `true`，或把 `TrackBackKey` 关掉自己监听 `Input.GetKeyDown(KeyCode.Escape)`；
- `BackOnRoot` 事件在默认行为**之前**触发，拿来做埋点可以，拿来阻止默认行为不行。

## 3. 安全区 / 刘海屏

`PlatformOptions.ApplySafeArea = true` 时，把 UI 根 Canvas 的锚点收缩到 `Screen.safeArea` 内，
四个层级（Background / Normal / Popup / Top）跟着一起内缩。旋转 / 尺寸变化会自动重调：

```csharp
platform.ApplySafeArea();                     // 手动重调（一般不用，Tick 会自动做）
Rect safe = platform.SafeArea;                // 当前安全区（像素）
Listen<PlatformSafeAreaChangedEvent>(e => { /* 有需要再处理，界面通常不用管 */ });
```

注意：

- 这套是「整体内缩」方案，适合全屏 UI。如果某个面板要**顶到刘海里去**（比如全屏战斗 UI 想用满屏），
  把它从层级容器里挪到 Canvas 直接子节点即可（安全区只作用于 Canvas 根）。
- 依赖 UIRoot 已加载，所以初始化放在 `ProcedureInitPlatform`（UI 之后）。

## 4. 权限申请

`PlatformPermissions` 封装 `UnityEngine.Android.Permission`（异步、回调在主线程）；
**非 Android 平台一律返回「已授权」**，编辑器里可以直接联调。

```csharp
// 查询
bool ok = PlatformPermissions.HasPermission(PlatformPermissions.Camera);

// 申请单个
PlatformPermissions.Request(PlatformPermissions.Camera, result =>
{
    if (result.Granted) 开相机; else 提示用户;
});

// 批量（已授权的直接进结果，需要弹窗的走系统回调，全部收齐后回调一次）
PlatformPermissions.Request(
    new[] { PlatformPermissions.Camera, PlatformPermissions.Microphone },
    results => { foreach (var r in results) { /* r.Permission / r.Granted / r.DontAskAgain */ } });
```

**AndroidManifest 必须声明对应的 `<uses-permission>`**，否则申请会被系统直接拒绝。
工程当前没有自定义 Manifest（`Assets/Plugins/Android/` 不存在），按需在
`Assets/Plugins/Android/AndroidManifest.xml` 加，例如：

```xml
<manifest xmlns:android="http://schemas.android.com/apk/res/android">
    <uses-permission android:name="android.permission.CAMERA" />
    <uses-permission android:name="android.permission.RECORD_AUDIO" />
    <uses-permission android:name="android.permission.ACCESS_FINE_LOCATION" />
    <uses-permission android:name="android.permission.READ_EXTERNAL_STORAGE" />
    <uses-permission android:name="android.permission.WRITE_EXTERNAL_STORAGE" />
</manifest>
```

注意：

- **隐私协议同意之后再申请权限**（商店要求）。存档里有 `Account.AgreementsAccepted`，
  在同意回调里再调 `PlatformPermissions.Request(...)`。
- 用户选「不再询问」后，`result.DontAskAgain = true`，此时再申请也不会弹窗；
  应该引导用户去系统设置里手动开（用 `AndroidJavaObject` 打开应用详情页，需要的话再加）。
- Android 13+ 的存储权限有变化（`READ_MEDIA_*` / 分区存储），按你的实际用途选权限名。

## 5. 回前台断线重连

流程：

```
切后台（pause 超过阈值）           记录「切后台时已登录」
回前台（下一帧 Tick）              连接还在？→ 不用管
                                  连接断了 + 切后台时已登录？
                                    → 取凭据（CredentialProvider）
                                      有账号+密码 → 自动重连（最多 N 次，间隔 M 秒）
                                      没有密码   → 发 PlatformSessionLostEvent
重连成功 → PlatformReconnectStateChangedEvent(Reconnected) + 游戏业务自动恢复
重连失败 → PlatformReconnectStateChangedEvent(Failed) + PlatformSessionLostEvent
```

要点：

- **登录是异步的**：`GameClientBehaviour.LoginAsync(...)` 在后台线程跑阻塞的登录流程，
  事件都回主线程，重连不会卡 UI（同步 `Login()` 会卡，只保留给人手点按钮用）；
- **密码默认记住**：登录成功后，若 `LocalSettings.RememberPassword`（默认开）为真，
  密码会以 `LocalSecret` 混淆形式存进 `account.json`（和 token 同一套机制），
  回前台自动重连直接用存储的凭据，不用再输密码；
- **密码存储是混淆不是加密**：密钥在包里，拿到设备的人能解出来。真正的安全靠服务端
  （改密码 / 封号 / 换设备让旧凭据失效）。介意的话关掉 `RememberPassword`；
  或者以后做「服务端下发短期 token 重登」，登录协议支持后覆盖 `CredentialProvider` 换掉默认来源；
- **退出登录会清掉记住的密码**：登录界面的「退出登录」按钮记得调
  `GameController.Instance.Storage.Account.ClearRememberedPassword()`（账号名 / token 不受影响）；
- **登录界面会自动预填**：`ProcedureLogin` 会用存档里的上次账号 + 记住的密码预填
  `LoginPanel`，手动重登也只需要点一下；
- 重连次数 / 间隔：`ReconnectMaxAttempts`（默认 3）、`ReconnectRetryDelay`（默认 2s）；
- 重连中再次切后台会停掉重试计时器，回前台重新判定；
- 断线重连后，`GameClientBehaviour.ClientCreated` 会让 `ProtocolHub` 自动重绑路由表，
  **业务协议订阅不用重新注册**（这是网络框架已有的能力）。

## 6. 接入 GameController

- `Awake`：创建 `PlatformManager`（`Platform` 属性可访问）；
- 启动链新增 `ProcedureInitPlatform`（在 `ProcedureInitAudio` 之后、`ProcedureLogin` 之前）；
- `Update`：`platform.Tick(...)`；
- `OnApplicationPause / OnApplicationFocus`：转发；
- `OnDestroy`：`platform.Shutdown()`。

## 7. 目录

| 文件 | 作用 |
| --- | --- |
| `PlatformManager.cs` | 主管理器：生命周期 / 返回键 / 安全区 / 断线重连 |
| `PlatformPermissions.cs` | Android 运行时权限封装（非 Android 直接放行） |
| `PlatformOptions.cs` | 配置 + `BackRootBehavior` / `AccountCredentials` / `PlatformReconnectState` |
| `PlatformEvents.cs` | 事件（struct） |

## 8. 已知限制 / 取舍

- **安全区是整体内缩方案**：不做「某面板单独避开刘海」的精细化；需要时自己把面板移出层级容器。
- **重连靠「记住密码」**：默认存密码（`LocalSecret` 混淆）才能静默重连；关掉
  `LocalSettings.RememberPassword` 后回前台只会发 `PlatformSessionLostEvent` 提示重新登录。
  密码持久化的安全边界见 StorageFramework README（混淆不是加密，密钥在包里）。
- **返回键默认只关 Popup / Top 层**：Normal 层的界面（如主界面）不参与「按返回关闭」，避免误关。
- **`moveTaskToBack` 只对 Android 真机生效**：编辑器 / 其它平台下 `MinimizeApp` 是空操作。
