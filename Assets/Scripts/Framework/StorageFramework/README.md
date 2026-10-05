# 本地存储（设置 / 账号 / 缓存）

**先说清楚它不做什么**：它不存玩家养成数据。等级、金币、背包、任务的权威永远在服务端，
本地存一份「快照」只是为了进游戏时先把界面渲染出来，收到服务端数据后立刻覆盖，任何写操作都走服务端。

它存的是**设备级、会话级**的东西：设置、安装 ID、登录 token、协议是否同意、引导标记。

## 一眼看懂

```
GameController.Awake   storage = new LocalStorageManager()        只创建
ProcedureInitStorage   Storage.Init(options, ...)                读盘 + 应用设置
业务                   Storage.Settings / Storage.Account / Storage.Cache
改数据                 Storage.Save(file) 或 Storage.NotifySettingsChanged()
切后台 / 退出          GameController 转发 → 立刻落盘
```

文件布局（`{persistentDataPath}/{FolderName}/`，默认 `LocalData/`）：

| 文件 | 内容 | 谁改 |
| --- | --- | --- |
| `settings.json` | 音量、画质、帧率、震动、语言、自动登录 | 设置界面 |
| `account.json` | 安装 ID、首次启动时间、协议同意、上次账号、token、记住的密码（可选） | 登录流程 |
| `cache.json` | 引导标记、弹窗已读、零散 KV、玩家数据快照 | 各个系统 |

## 用法

设置（设备级，换账号不跟着变）：

```csharp
var s = GameController.Instance.Storage.Settings;
s.VolumeBgm = 0.3f;

// 落盘 + 应用到引擎（帧率 / 画质 / 总音量）+ 通知订阅者，一句话搞定
GameController.Instance.Storage.NotifySettingsChanged();
```

```csharp
// 界面 / 音频系统订阅设置变化，不用互相引用
GameController.Instance.Storage.SettingsChanged += OnSettingsChanged;
```

账号和 token：

```csharp
var account = GameController.Instance.Storage.Account;

// 登录成功之后
account.LastAccount = "dev";
account.PlayerId = response.PlayerId;
account.Token = response.SessionId;
account.TokenExpireUnixSeconds = ...;
GameController.Instance.Storage.Save(GameController.Instance.Storage.AccountFile);

// 下次启动：能不能直接自动登录
if (account.HasUsableToken()) { /* 用 token 登录 */ }

// 退出登录 / 被踢下线
account.ClearSession();
GameController.Instance.Storage.Save(...AccountFile);
```

协议同意（Android 上必须在初始化任何 SDK 之前同步读到，所以只能放本地）：

```csharp
var account = GameController.Instance.Storage.Account;
if (!account.AgreementsAccepted || account.AgreementsVersion < CurrentAgreementsVersion)
    弹协议();     // 同意后：account.AgreementsAccepted = true; account.AgreementsVersion = CurrentAgreementsVersion; 然后 Save
```

引导 / 弹窗标记、零散 KV：

```csharp
var cache = GameController.Instance.Storage.Cache;
cache.GuidanceDone = true;
cache.ShownPopupIds.Add("announce_2026_09");
GameController.Instance.Storage.Save(GameController.Instance.Storage.CacheFile);

// 结构还没定下来的小数据先塞 KV，别为了一个开关就动结构
storage.SetPrefBool("tutorial_skip", true);
bool skip = storage.GetPrefBool("tutorial_skip");
```

玩家数据快照（只用于展示）：

```csharp
// 收到服务端玩家数据之后
cache.SnapshotAccount = "dev";
cache.SnapshotLevel = data.Level;
cache.SnapshotGold = data.Gold;
cache.SnapshotSyncedUnixSeconds = Storage.NowUnixSeconds();
storage.Save(storage.CacheFile);

// 下次进游戏、还没收到数据时
if (cache.SnapshotBelongsTo(account.LastAccount))
    ShowLevel(cache.SnapshotLevel, cache.SnapshotGold);   // 先渲染，收到数据再覆盖
```

自己再加一个存档文件（活动进度、大块数据…）：

```csharp
var file = new StorageFile<MyEventData>("event.json", 1);
file.AddMigration(1, d => d.NewField = 0);     // 改结构时补一条
storage.Register(file);                         // 之后落盘 / 清档都归管理器管
```

## 配置

`GameController` Inspector 的「本地存储」就是 `StorageOptions`：

| 字段 | 作用 |
| --- | --- |
| `FolderName` | 存档目录名，默认 `LocalData` |
| `RootPathOverride` | 自定义根目录（留空 = `Application.persistentDataPath`），调试 / 测试用 |
| `AutoFlushDelay` | 改动后隔多少秒落盘，`0` = 每次改动立刻落盘 |
| `FlushOnPause` | 切后台 / 失焦立刻落盘（移动端建议保持打开） |
| `FlushOnQuit` | 退出时落盘 |
| `PrettyJson` | 缩进输出，方便直接看存档文件 |
| `BackupCorruptFile` | 存档坏了就备份成 `.bad` 再重建 |
| `LogOnInit` | 初始化后打一条日志 |

## 版本迁移（最重要的一条）

改数据结构**不要**直接改字段含义，按这个来：

1. 加 / 改字段；
2. 把 `LocalStorageManager` 里对应的版本常量 +1（`SettingsVersion` / `AccountVersion` / `CacheVersion`）；
3. 如果有老数据需要转换，加一条迁移。

```csharp
// v2 新增了 VolumeBgm，老存档没有这个字段
settingsFile.AddMigration(1, s => s.VolumeBgm = 1f);
```

迁移是逐级跑的（v1→v2→v3…）。**缺哪一级迁移就把整个文件当损坏处理**：备份 `.bad` + 按默认值重建，
所以别偷懒跳版本。存档版本比程序还新（玩家装过更新的版本又装回来）也会重建。

## 目录

| 文件 | 说明 |
| --- | --- |
| `LocalStorageManager.cs` | 总入口：读盘、落盘、清档、KV、应用设置。`GameController` 持有它 |
| `StorageFile.cs` | `StorageFile<T>` 带版本号的 JSON 文件 + 序列化设置；同文件的 `StorageFileBase` 是它的非泛型视图 |
| `StorageOptions.cs` | 初始化配置 |
| `LocalSettings.cs` / `LocalAccount.cs` / `LocalCache.cs` | 三个存档的数据结构 |
| `LocalSecret.cs` | token 落盘时的混淆（不是加密，见下） |

## 注意

- **写盘是「先写 `.tmp` 再替换」**，中途进程被杀也不会写坏正式文件，最多留下一个 `.tmp`。
- **切后台要落盘**：移动端系统杀进程不会给你机会，`FlushOnPause` 保持打开。
- **只在主线程访问**。读盘是同步的（几 KB，微秒级），但别在别的线程里调。
- 存档目录建不出来时**不会卡住启动**：降级成内存模式，`Init` 回调 `false`，`CanPersist` 变 `false`，
  游戏照常能玩，只是关掉就没了。存储是体验问题，不是硬依赖。
- **敏感字段的混淆不是加密**：密钥就在包里，拦不住真想改存档的人。真正的安全靠服务端
  —— token 要有有效期，改密码 / 封号 / 换设备要能让旧 token 立刻失效。
  token 和「记住密码」的密码都走同一套混淆（`LocalSecret`）；介意就关掉
  `LocalSettings.RememberPassword`，或等服务端支持「token 重登」后只存 token。
- `DeviceId` 是首次启动生成的 GUID，**卸载重装会变**，别拿它当账号 ID。做卸载重装归因要另外接平台能力。
- `DeleteAll(keepDeviceId)` 默认保留安装 ID，免得埋点把同一台设备当成新用户。
- 清档 / 改结构之后记得跑一次游戏确认；`RootPathOverride` 指到临时目录就能安全地试。