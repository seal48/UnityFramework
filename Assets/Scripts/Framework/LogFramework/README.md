# LogFramework 统一日志

一个入口（`GameLog`）、一套级别（`LogLevel`）、一套标签（`LogTag`），三个出口：控制台 / 文件 / 内存。

## 初始化

由 `GameController` 在 `Awake` 里最先初始化 —— 它必须早于其它框架，否则启动早期的日志没人接。

```csharp
GameLog.Init(logOptions);   // Awake
GameLog.Tick(Time.unscaledDeltaTime);   // Update，负责把文件缓冲落盘
GameLog.Shutdown();         // OnDestroy 最后
```

没初始化也能用：日志会直接进 Unity 控制台，不会被吞掉（只是没有文件和级别过滤）。

## 用法

模块里推荐缓存一份，避免每条日志都拼标签：

```csharp
private static readonly ModuleLog Log = GameLog.Get(LogTag.UI);

Log.Info("打开面板 " + panelName);
Log.WarnFormat("面板 {0} 已经打开过", panelName);
Log.Error("加载失败", exception);
```

零散地方直接用静态方法：

```csharp
GameLog.Info(LogTag.Storage, "落盘完成");
```

判断某级别的日志开没开（拼字符串很贵时）：

```csharp
if (Log.IsEnabled(LogLevel.Debug)) Log.Debug("大对象：" + Dump());
```

## 级别

`Fatal < Error < Warn < Info < Debug`，数值越大越啰嗦。过滤规则是「级别数值 <= 阈值」：

- 阈值 `Info` → 输出 Info / Warn / Error / Fatal，挡掉 Debug；
- 阈值 `Debug` → 全输出；
- 阈值 `None` → 全不输出。

编辑器 / Development Build 用 `MinLevelInDevelopment`，正式包用 `MinLevel` —— 也就是开发期能看 Debug，上线自动收口。

## 标签

`LogTag` 里集中登记（`Startup` / `Resource` / `UI` / `NET` …）。要临时压掉某个模块的日志，把标签填进 `LogOptions.MutedTags`。

## 输出

- **控制台**：`[时间][级别][标签] 消息`，带颜色（真机 logcat 建议把 `ColoredConsole` 关掉，否则满屏转义符）。
- **文件**：`{日志目录}/yyyy-MM-dd.log`，按天分文件、只留最近 `MaxFileCount` 个；缓冲 `FlushInterval` 秒落盘一次，**Error / Fatal 立刻落盘**（进程被杀时至少留得下错误）。
- **内存**：环形保留最近 `MaxHistory` 条，`GameLog.Memory` 可取。游戏内控制台、崩溃上报都从这里拿；`Memory.Received += ...` 可以做「出错弹提示」。

日志目录默认 `Application.persistentDataPath/Logs`；`RootPathOverride` 可以指到别处（编辑器下 `GameController` 会指到工程根 `Logs/`，方便直接翻看）。

## 和网络框架的关系

`NetFramework` 的 `INetLogger` 是给「后端独立进程」和 Unity 共用的接口（后端不能引用 UnityEngine），所以保留不动。
Unity 侧的 `UnityNetLogger` 已经转接到 `GameLog`，网络日志同样受级别 / 标签 / 文件控制。

## 还没做的

- 错误上报（把 `Fatal` / `Error` 发到服务器）留了钩子：订阅 `GameLog.Memory.Received` 即可。

## 关于 `Debug.Log`

运行时框架已经全部改走 `GameLog`（Startup / UI / Pool / Audio / Scene / Resource / Storage / Timer /
EventBus / Procedure / 网络客户端），`Scripts` 下只剩两类例外，都是有意保留的：

- **日志出口自身** —— `GameLog.cs` 和 `UnityConsoleSink.cs` 必须调 `UnityEngine.Debug.Log`，
  它就是 `GameLog` 落到控制台的那一步。
- **编辑器菜单** —— `NetFramework/Editor/ServerMenu.cs`、`ResourceFramework/Editor/YooAssetBuildMenu.cs`、
  `Config/Editor/ConfigExporterMenu.cs`。它们不保证 `GameLog.Init` 已经跑过，直接用 `Debug.Log` 更稳。