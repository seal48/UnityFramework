# 计时器 / 调度器

分两层：

- **调度逻辑**在 `TimerFramework/Core/`，纯 C#，**客户端和服务端共用同一份**；
- **Unity 侧**的 `TimerFramework/`（`TimerManager` / `TimerOptions`）只是一层门面：负责时间轴（Scaled / Unscaled）、日志接 GameLog、Inspector 配置。

> 计时内核**不依赖网络框架**。内核的日志口是 `ITimerLogger`；网络的 `INetLogger` 继承它，
> 所以服务端可以把同一个 logger 直接传给 `TimerScheduler`，方向是 `网络 → 计时`，不会反过来。

延迟、循环、下一帧、等条件、绝对时刻，全在一个地方管。业务不用自己算 `Time.time` 差值，
也不用为了「关掉界面就别再回调」而到处记状态。

## 一眼看懂

```
GameController.Awake   timer = new TimerManager(); timer.Init(options);   没有异步步骤，直接起
GameController.Update  timer.Tick(Time.deltaTime, Time.unscaledDeltaTime)  每帧驱动
业务                   GameController.Instance.Timer.Delay(1f, () => ...)
面板                   Timers.Delay(this, 1f, () => ...)                  面板关闭自动取消
退出                   GameController.OnDestroy -> timer.Shutdown()
```

## 用法（Unity）

```csharp
using GameFramework.Timer;   // TimerManager / TimerHandle / TimerMode 都在这

TimerManager t = GameController.Instance.Timer;

t.Delay(1.5f, () => OpenChest());                        // 1.5 秒后一次（游戏时间）
t.Delay(TimerMode.Unscaled, 3f, () => ShowTip());         // 真实时间：暂停时照样走
t.Repeat(1f, () => Refresh(), 5);                         // 每秒一次，共 5 次
t.Repeat(0.5f, () => Poll());                             // 无限循环（times <= 0）
t.NextFrame(() => Layout());                              // 下一帧一次
t.WaitUntil(() => IsReady(), () => EnterGame(), 5f);      // 等到条件成立，5 秒等不到就放弃
t.DelayUntil(t.ServerNowMs + 30_000, () => Open());       // 服务器时间 30 秒后（跨零点也对）
```

拿到的 `TimerHandle` 可以直接停：

```csharp
TimerHandle h = t.Repeat(1f, () => Tick());
h.Cancel();
if (h.IsValid) { /* 还在跑 */ }
```

**按归属批量停**（推荐做法）：注册时把 owner 传进去，之后一句全停。

```csharp
t.Delay(this, 1f, () => NextStep());     // this = 你的面板 / 系统
t.CancelOwner(this);                     // 把所有归属 this 的计时器停掉
```

`Panel` 子类直接用 `Timers`，而且**面板关闭时会自动 `CancelOwner(this)`**。

## 用法（服务端）

服务端没有「帧」，accept / 收包线程都是事件驱动的，所以单独起了一条定时线程：

```csharp
// GameServer.Start() 之后可用
TimerScheduler timers = server.Timers.Scheduler;

timers.Repeat(5f, () => server.Broadcast(...));           // 心跳 / 周期广播
timers.DelayUntil(activityOpenUtcMs, () => OpenActivity()); // 绝对时刻
long nowMs = server.Timers.NowUtcMs;                       // 下发 ServerTimeMs 给客户端校时
```

有两个点要注意：

- 回调跑在**定时线程**（默认 15ms 一轮）上。要动会话、要广播，就经 `Router` 的 Dispatcher 派发回去，或者自己加锁 —— 和收包回调是同一套心智。
- 定时线程的间隔就是精度的上限（`GameServerOptions.TimerIntervalMs`，默认 15ms）。

## 时间从哪来

| 场景 | 时间轴 | 说明 |
| --- | --- | --- |
| Unity `TimerMode.Scaled` | `VirtualTimeBase` | 每帧 `Advance(Time.deltaTime)`，`timeScale = 0` 时自然停住 |
| Unity `TimerMode.Unscaled` | `RealTimeBase` | 单调时钟（`Stopwatch`），暂停、切后台照走 |
| 服务端 | `RealTimeBase` | 同上，服务端自己就是权威 |

内部存的是**到期时刻**而不是「还剩多少秒」，所以宿主 Tick 抖多少下都不会累积成误差。
精度上限 = 宿主一轮的长度（Unity 是一帧，服务端是 15ms）。

`RealTimeBase` 的 UTC 靠一次观测锚定：客户端以后做校时时，
收到服务器的 `ServerTimeMs` 调 `GameController.Instance.Timer.TimeBase.Anchor(serverTimeMs)`，
之后所有 `DelayUntil` / `ServerNowMs` 就都按服务器时间走。

## 配置

`GameController` Inspector 的「计时器」就是 `TimerInitOptions`：

| 字段 | 作用 |
| --- | --- |
| `MaxTimers` | 同时存在的上限，0 = 不限。超过时新注册失败并报错，用来抓「只加不减」 |
| `WarnFiresPerFrame` | 单帧触发超过多少个就警告，0 = 不检查。间隔 0 的死循环在这里现形 |
| `WarnSlowCallbackMs` | 单个回调超过多少毫秒就警告，0 = 不检查 |
| `LogOnInit` | 初始化完打一条统计日志 |

服务端是同名的 `TimerSchedulerOptions`，在 `ServerTimerService` 构造时传。

## 目录

| 文件 | 说明 |
| --- | --- |
| `Core/TimerScheduler.cs` | **两端共用**的调度器本体（纯 C#） |
| `Core/TimeBase.cs` | `ITimeBase` / `RealTimeBase`（单调 + UTC 锚点）/ `VirtualTimeBase` |
| `Core/TimerHandle.cs` | 句柄，带 `Cancel` / `IsValid`，`default` 就是无效句柄 |
| `Core/TimerTask.cs` | 内部条目，外部拿不到 |
| `Core/TimerLogging.cs` | `ITimerLogger` 日志口 + `NullTimerLogger` |
| `TimerManager.cs` | Unity 门面：两条时间轴 + 日志 + 配置映射 |
| `TimerOptions.cs` | `TimerMode` / `TimerInitOptions`（Unity 专有） |
| `UnityTimerLogger.cs` | Unity 侧日志实现（接 GameLog） |
| `Server/ServerTimerService.cs` | 服务端定时线程，`GameServer.Timers`（在 NetFramework 里） |

## 注意

- **回调里可以放心再注册 / 取消。** 本轮新加的计时器留到下一次 Tick 才可能触发；取消只打标记，
  不会打乱正在跑的那一轮。
- **卡顿、断点、切后台回来之后不补发。** 一次最多触发一次，下一次触发至少推后一整个间隔，
  不会解冻瞬间炸出一堆回调。
- `Repeat` 的 `times <= 0` 都是无限循环；只想要一次就用 `Delay`。
- 回调抛异常会被接住并打错误日志（带上归属和回调名），不会中断这一轮其它计时器。
- 计时器是**逻辑层**的东西，只认秒和回调，不碰 GameObject / 协程。
- Unity 侧的时间轴只有 Scaled / Unscaled 两条，`DelayUntil` 固定走真实时间轴。