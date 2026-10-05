# 网络框架（TCP / 前后端共用）

一套前后端共用的 TCP 消息框架：Unity 客户端与后端服务器使用**同一份收发、编解码、拆包和路由代码**，
只有入口不同（Unity 用 `GameClientBehaviour`，后端用 `GameServer`）。

流程：**主动登录 → 建立连接 → login.request / login.response → 登录成功才开始心跳 → 超时自动断开**。
框架不会自动连接，连接由你的登录动作触发。

## 1. 目录结构

```
Assets/Scripts/Framework/NetFramework/
├── Shared/                     前后端完全共用（纯 .NET，不依赖 UnityEngine）
│   ├── NetConfig.cs            全局常量：默认端口、长度上限、超时、心跳间隔
│   ├── NetLog.cs               日志接口 + 控制台实现 + 空实现
│   ├── Threading/IDispatcher.cs        回调派发器（后端立刻执行 / Unity 塞队列）
│   ├── Protocol/
│   │   ├── Protocols.cs        ★ 全部消息名称的唯一登记处（枚举 + 名字总表）
│   │   ├── FrameLayout.cs      包头字节布局的唯一定义处
│   │   ├── FrameCodec.cs       整帧编码 / 解码（大端序长度字段）
│   │   ├── FrameParser.cs      流式拆包：处理粘包 / 半包
│   │   ├── GameMessage.cs      一条消息：协议名称 + 内容字节 + ProtocolId
│   │   ├── Messages/AccountMessages.cs  register.* / login.* 的数据结构
│   │   ├── Messages/SystemMessages.cs   ping / heartbeat / bye 的数据结构
│   │   ├── IMessageSerializer.cs       内容转译接口
│   │   ├── JsonMessageSerializer.cs    默认实现：UTF-8 紧凑 JSON
│   │   └── PlainTextMessageSerializer.cs  纯文本实现（省掉 JSON 引号/转义）
│   ├── Router/ProtocolRouter.cs        协议名称 → 处理函数
│   └── Transport/PeerConnection.cs     收发核心：接收线程 + 发送锁 + 拆包 + 派发
├── Server/                     后端
│   ├── GameServer.cs / GameServerOptions.cs
│   ├── PeerSession.cs          每条连接的会话状态（是否已登录、playerId、sessionId）
│   ├── Persistence/            账号与玩家数据存储
│   │   ├── AccountModels.cs    AccountRecord / PlayerData / ItemStack
│   │   ├── IAccountStore.cs    存储接口（增删改查），换数据库只实现它
│   │   ├── AccountService.cs   账号业务层：注册/登录校验/改密码/等级/属性/道具
│   │   ├── PasswordHasher.cs   PBKDF2-SHA256 密码哈希
│   │   ├── JsonAccountStore.cs     单文件 JSON 存储（纯 .NET，Unity 可用）
│   │   └── SqliteAccountStore.cs   SQLite 存储（推荐，仅独立后端进程）
│   └── Builtin/
│       ├── AccountProtocols.cs  注册/登录协议处理 + ILoginValidator 接口
│       └── SystemProtocols.cs  ping / heartbeat / bye
├── Client/                     前端
│   ├── GameClient.cs           登录、发送、请求-响应、心跳与超时断开
│   └── Unity/
│       ├── GameClientBehaviour.cs   挂到空物体上，由你调用 Login()
│       └── UnityNetLogger.cs        日志转到 Unity 控制台
├── Editor/ServerMenu.cs        编辑器菜单：起/停本地服务器 + 登录开发工具
└── ServerHost/Program.cs       独立进程后端的入口（Unity 不编译这个文件，被 #if 排除）
```

后端可执行程序（构建脚本在 Assets 之外，避免 bin/obj 被 Unity 当资源导入）：

```
ServerHost/GameServerHost.csproj
```

## 2. 消息格式

长度字段一律 **大端序 Int32**（网络字节序），协议名称用 **UTF-8**：

```
 0        4                       4+nameLen      8+nameLen
 +--------+-----------------------+--------------+------------------+
 | nameLen| 协议名称 (nameLen 字节)  |   bodyLen    | 内容 (bodyLen 字节)|
 +--------+-----------------------+--------------+------------------+
 |<------------ 包头 ------------->|<-------- 消息内容 -------->|
```

- `nameLen`：协议名称的字节长度 —— 包头第一段就是它。
- `bodyLen`：消息内容的字节长度 —— 收发双方靠它判断"剩下的内容"取多少字节。
- 内容默认是 UTF-8 的**紧凑 JSON**（不输出空格换行）。
- 收发双方都会校验：`nameLen` 必须在 1~128，`bodyLen` 必须在 0~1MB，越界直接判定非法报文中断连接。
  想改布局（加版本号、加校验位、把长度挪到最前）只需要改 `FrameLayout` 和 `FrameCodec`。

TCP 是字节流，一次 `Receive` 可能拿到半条、一条或多条消息，`FrameParser` 负责恢复成一条条完整消息。

## 2.1 消息名称总表（唯一登记处）

**所有消息名称都写在 [Shared/Protocol/Protocols.cs](Shared/Protocol/Protocols.cs) 一个文件里，前后端共用。**

加一条新消息，只改这个文件的两处：

```csharp
public enum ProtocolId
{
    ...
    PlayerMove,          // ① 加一个编号
}

private static readonly Dictionary<ProtocolId, string> Names = new Dictionary<ProtocolId, string>
{
    ...
    { ProtocolId.PlayerMove, "player.move" },   // ② 写上线上的名字
};
```

改完就能直接用，别的地方不用动：

```csharp
peer.Send(ProtocolId.PlayerMove, new MoveRequest { X = 1 });               // 服务端发
client.Send(ProtocolId.PlayerMove, new MoveRequest { X = 1 });             // 客户端发
router.RegisterOrReplace(ProtocolId.PlayerMove, (msg, peer) => { ... });   // 注册处理函数
server.Broadcast(ProtocolId.PlayerMove, new MoveRequest { X = 1 });        // 广播
var id = msg.Id;                                                            // 收到时直接知道编号
```

自动衍生出来的能力：

- `Protocols.AllNames`：所有协议名的列表
- `Protocols.NameOf(ProtocolId.LoginRequest)` / `Protocols.IdOf("login.request")`：名字与编号互转
- **启动自检**：编辑器或 Debug 构建下自动校验"枚举是否都登记、有没有重名、名字有没有超长"，写错立刻抛异常
- `start-server.cmd --protocoltest`：打印整张表并校验

字符串重载依然保留（`Send("player.move", ...)`），配置驱动、转发未知协议时用得上；
没登记过的名字会识别成 `ProtocolId.Unknown`，不影响收发。

当前登记的消息：

| ProtocolId | 线上名字 | 方向 | 说明 |
|---|---|---|---|
| `RegisterRequest` | `register.request` | C → S | 注册请求（账号/密码/玩家名/客户端版本/平台） |
| `RegisterResponse` | `register.response` | S → C | 注册结果（成功标记、原因、playerId） |
| `LoginRequest` | `login.request` | C → S | 登录请求（账号/密码/客户端版本/平台） |
| `LoginResponse` | `login.response` | S → C | 登录结果（成功标记、原因、playerId、sessionId） |
| `SystemPing` | `system.ping` | C → S | 手动测往返延迟 |
| `SystemPong` | `system.pong` | S → C | 延迟回包 |
| `SystemHeartbeat` | `system.heartbeat` | C → S | 心跳（登录成功后每 5 秒一条） |
| `SystemHeartbeatAck` | `system.heartbeat.ack` | S → C | 心跳回包 |
| `SystemBye` | `system.bye` | C → S | 断开前告知原因（超时/主动退出） |

## 3. 区服列表 / 选服

区服列表是**客户端内置**的（Excel 配置表 `ServerList.xlsx`），服务端不需要改。玩家在登录界面选服，
登录时连到选中服的 `Host:Port`。

### 区服表（`Assets/Config/Excel/ServerList.xlsx`）

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` | int（主键，`##group=B`） | 服 ID |
| `Name` | string（C） | 服名（显示用） |
| `Host` | string（C） | IP / 域名 |
| `Port` | int（C） | 端口 |
| `Status` | `enum:ServerStatus`（C） | `Normal=1` / `Maintenance=2` / `New=3` |
| `IsRecommended` | bool（C） | 推荐服（没选过时默认选它） |
| `SortOrder` | int（C） | 排序，小的在前 |
| `Announce` | string（C） | 公告（维护说明等） |

> 主键必须 `##group=B` 是配置框架的约束（见 Config README）；其余字段设 C 表示只客户端用。

### 选服管理器

```csharp
var select = GameController.Instance.ServerSelect;

select.All;              // 所有服，按 SortOrder 排序
select.Current;          // 当前选中的服（null = 区服表空）
select.Select(2);        // 选中 id=2 的服，并记忆到存档
```

- **记忆**：选中的服 ID 存 `Storage.Account.LastServerId`（设备级），下次启动自动恢复；
  没选过则默认选 `IsRecommended` 的服，再兜底第一个 `Normal` 服。
- **登录时应用**：`LoginPanel.Login()` 会先 `gameClient.SetServer(server.Host, server.Port)` 再登录。
  也可以自己调：

```csharp
var s = GameController.Instance.ServerSelect.Current;
GameController.Instance.gameClient.SetServer(s.Host, s.Port);
GameController.Instance.gameClient.Login(account, password);
```

- **换服通知**：`Select()` 会发 `ServerChangedEvent`，登录界面 `Listen` 它自动刷新"当前服"显示。

### 界面

- **登录界面**：顶部 `ServerSelectButton` 显示当前服（含状态后缀，如「测试服-3服（维护）」），点击打开选服弹窗。
- **选服弹窗**（`SelectServerPanel`，Popup 层）：列出所有服（服名 + 状态颜色 + 推荐标签），点一个即选中并关闭。
  预制体 `Assets/Prefabs/SelectServerPanel.prefab`，列表项由 `Content/ServerItem` 模板克隆生成。

### 加一个服

改 `ServerList.xlsx` 加一行 → 导出配置表 → 构建资源（Host/Offline 模式）。客户端不用改代码。

## 3.1 注册与登录

### 注册

客户端没有账号时先注册 —— `register.request` → `register.response`：

```csharp
// 前端：只注册（成功后还需要 Login）
var net = GetComponent<GameClientBehaviour>();
bool ok = net.Register("myAccount", "myPassword123", out var response);
if (!ok) Debug.LogError(response.Message);   // 例如"账号已存在"、"密码至少 6 位"

// 前端：注册并登录（一站式，账号已存在时会直接登录，可以重复点）
net.RegisterAndLogin("myAccount", "myPassword123");

// 不用 MonoBehaviour 也行
var client = new GameClient();
var reg = client.Register("127.0.0.1", 7777, "myAccount", "myPassword123", playerName: "小明");
if (reg.Success) client.Login("127.0.0.1", 7777, "myAccount", "myPassword123");
```

注册规则（`AccountService` 里可改）：账号 3~20 个字符且只能是字母/数字/`_`/`-`/`.`；密码至少 6 位；
账号已存在会被拒绝。失败原因用常量判断，别去比中文：

**账号名会自动规范化**（前后端共用 `AccountNameRules`）：

- 去掉首尾空格（复制粘贴常见）
- **全角转半角**：中文输入法下打出来的 `１２３４５６ＡＢＣ` → `123456ABC`
  （后端不依赖 ICU，是自己做的转换，`InvariantGlobalization` 环境下也有效）

注册和登录用同一套规范化，所以玩家用全角数字注册、用半角登录（或反过来）进的是同一个账号。
真正不合法的输入会精确指出问题所在，例如中间夹空格：

```
账号的第 3 个字符不合法：空格（U+0020）。只能用半角字母、数字、下划线、中划线、点，且不能有空格
```

| RegisterReasons | 含义 |
|---|---|
| `invalid_account` | 账号格式不合法（太短/太长/有非法字符） |
| `weak_password` | 密码太短 |
| `account_exists` | 账号已存在 |
| `register_closed` | 服务器关闭了注册（`GameServerOptions.AllowRegister = false`） |
| `rejected` | 其它原因（存储写入失败等） |

服务器侧：`RegisterProtocols` 已经挂好了，用 `server.Accounts.Register(...)` 也能在后台开号；
线上想只让运营开号就把 `AllowRegister` 设成 false。

### 登录

### 登录流程

```
你的代码调用 Login(账号, 密码)
        ↓
建立 TCP 连接
        ↓
发 login.request  →  服务器校验  →  回 login.response
        ↓
成功：IsLoggedIn = true，开始心跳
失败：连接被服务器断开，返回 LoginResponse.Message（不会抛异常）
```

服务器侧默认 `RequireLogin = true`：**未登录的连接，除 `login.request` / `system.bye` 以外的消息会被丢弃并打警告**，
所以不存在"没登录就偷偷发业务消息"的情况。

### 前端（登录）

```csharp
var net = GetComponent<GameClientBehaviour>();

// 方式一：用 Inspector 里填的账号（account/password 字段）
net.Login();

// 方式二：运行时传参
bool ok = net.Login("myAccount", "myPassword");
if (!ok)
{
    // net.LastLoginMessage / LoginFailed 事件里有失败原因
}

net.LoggedIn += b => Debug.Log($"登录成功，playerId={b.Session.PlayerId}");
net.LoginFailed += (b, response) => Debug.LogError($"登录失败：{response?.Message}");
```

不想用 MonoBehaviour 也可以直接用核心类：

```csharp
var client = new GameClient();
var response = client.Login("127.0.0.1", 7777, "dev", "dev");
if (response.Success) { /* ... */ }
```

### 后端（登录校验）

本地开发直接用内存账号表：

```csharp
var server = new GameServer(new GameServerOptions { Port = 7777 });
server.AddAccount("dev", "dev", "player-dev");   // 账号 / 密码 / playerId
server.Start();
```

接数据库或 HTTP 鉴权时实现 `ILoginValidator`：

```csharp
public sealed class DbLoginValidator : ILoginValidator
{
    public LoginResponse Validate(LoginRequest request, IMessagePeer peer)
    {
        // 查库、校验封禁状态、限流……
        return 密码正确
            ? LoginProtocols.Success(playerId)
            : LoginProtocols.Failure(LoginReasons.InvalidCredentials, "账号或密码错误");
    }
}

var server = new GameServer(new GameServerOptions { LoginValidator = new DbLoginValidator() });
```

登录成功后，服务器侧可以这样取玩家信息：

```csharp
server.Router.RegisterOrReplace(ProtocolId.PlayerMove, (msg, peer) =>
{
    var session = PeerSession.Get(peer);
    if (session == null || !session.LoggedIn) return;   // 双保险
    Debug.Log($"{session.PlayerId} 移动了");
});
```

## 3.2 账号与玩家数据存储（等级 / 属性 / 道具）

**选型结论：正式用 SQLite，开发期用 JSON 文件。** 两者都在 `IAccountStore` 接口后面，换的时候业务代码不用动。

| | 单文件 JSON | SQLite（推荐） |
|---|---|---|
| 依赖 | 无（纯 .NET，Unity 里也能跑） | `Microsoft.Data.Sqlite`（嵌入式，不需要装服务） |
| 可读性 | 记事本直接看/改 | 需要工具看，但有 SQL |
| 写入 | 每次全量重写，写坏=整库损坏（已用"临时文件+替换"降低风险） | 事务，要么全成功要么全失败 |
| 并发/规模 | 适合几十~几百账号、低频写 | 几千~几十万账号，可索引、可按条件查 |
| 适用 | 本地调试、Unity 编辑器内跑服务器 | 正式后端进程 |

为什么不是 XML：XML 更啰嗦、解析更慢、嵌套列表表达力还不如 JSON，除了"要和旧系统对接"之外没有理由选它。
什么时候再换 MySQL/PostgreSQL：多台服务器同时在线、需要跨进程共享数据、要做运营后台报表的时候 —— 那时再实现一个 `IAccountStore` 即可。

### 数据模型

```csharp
AccountRecord          // 账号：Account(主键) / PasswordHash / PlayerId / CreatedAtMs / LastLoginMs / Banned
  └─ PlayerData        // 玩家数据：Level / Exp / Gold / Attributes(字典) / Items(列表)
       └─ ItemStack    // 道具：ItemId / Count / ExtraJson(附魔、耐久等自定义字段)
```

SQLite 里对应三张表（`accounts` / `player_state` / `player_items`），`player_items` 以 `(account, item_id)` 为联合主键，
`item_id` 上有索引，删账号时外键 `ON DELETE CASCADE` 会把等级和道具一起删掉。
属性用 JSON 列存（属性名随便加，不用改表结构），需要按属性排名时再把它拆成独立列。

### 增删改查 API

```csharp
// 增：注册
server.Accounts.Register("alice", "pwd123", out var error, playerId: "player-1");

// 查：账号 + 玩家数据
server.Accounts.TryGet("alice", out AccountRecord account);
var data = server.Accounts.GetPlayerData("alice");
var list = server.Accounts.List(50, search: "ali");        // 列表/搜索

// 改：等级 / 经验 / 金币 / 属性
server.Accounts.SetLevel("alice", 30);
server.Accounts.AddExp("alice", 1500);
server.Accounts.AddGold("alice", -200);                    // 负数就是扣
server.Accounts.SetAttribute("alice", "strength", 25);
server.Accounts.AddAttribute("alice", "agility", 3);
server.Accounts.RemoveAttribute("alice", "luck");

// 道具：增删改查
server.Accounts.AddItem("alice", "sword_fire", 1, extraJson: "{\"durability\":100}");
server.Accounts.AddItem("alice", "potion_hp", 10);
server.Accounts.RemoveItem("alice", "potion_hp", 3);       // 数量不足会返回 false，不会扣成负数
server.Accounts.SetItemCount("alice", "gold_key", 1);
var potions = server.Accounts.GetItemCount("alice", "potion_hp");
var items = server.Accounts.GetItems("alice");

// 改密码 / 封禁 / 删账号
server.Accounts.ChangePassword("alice", "pwd123", "newpwd", out var pwdError);
server.Accounts.SetBanned("alice", true);
server.Accounts.Delete("alice");
```

密码用 **PBKDF2-SHA256（10 万次迭代 + 随机盐）** 存哈希，从不存明文；登录失败时"账号不存在"和"密码错误"返回同一句提示，避免被撞库探测账号是否存在。

### 存储文件在哪 / 怎么配

```csharp
// 独立后端进程：SQLite（默认，D:\unity\game\MCP\ServerHost\GameData\accounts.db）
start-server.cmd                       // 自动创建 GameData/accounts.db
start-server.cmd --data-dir D:\savedata   // 换数据目录

// Unity 编辑器内跑服务器：JSON（D:\unity\game\MCP\GameData\accounts.json）

// 自己指定：
var server = new GameServer(new GameServerOptions {
    AccountStore = new SqliteAccountStore(@"D:\savedata\accounts.db")
});
```

SQLite 开了 WAL 模式，所以目录里会看到 `accounts.db`、`accounts.db-wal`、`accounts.db-shm` 三个文件；
**备份时要一起拷**（或先停服务器再拷 `accounts.db`）。

### 后端控制台命令（本地运营/GM 用）

服务器启动后在终端里可以用：

```
clients                         查看连接与登录状态
accounts [关键字]                列出账号
account <账号>                   查看账号详情（等级/属性/道具）
register <账号> <密码>            注册账号
delete <账号>                    删除账号
setlevel <账号> <等级>            改等级
setattr <账号> <属性名> <值>       改属性
additem <账号> <道具id> <数量>     加道具
delitem <账号> <道具id> <数量>     扣道具
```

## 4. 心跳与连接超时

**登录成功后**才会开始心跳（框架不会在未登录时发心跳）：

- 登录成功立刻发第一条
- 之后每 5 秒一条
- 超过 5 秒没回包 → 记错误日志 → 发一条 `system.bye`（带超时原因）→ **主动断开**

默认值在 [NetConfig.cs](Shared/NetConfig.cs)：

```csharp
public const int DefaultHeartbeatIntervalMs = 5000;   // 心跳间隔
public const int DefaultHeartbeatTimeoutMs  = 5000;   // 回包超时
```

Unity 侧可在 Inspector 的"心跳"分组里逐实例改，代码里也能改：

```csharp
var client = new GameClient();
client.HeartbeatIntervalMs = 5000;
client.HeartbeatTimeoutMs  = 5000;
client.HeartbeatEnabled    = true;

client.HeartbeatReceived  += (c, rtt) => Debug.Log($"心跳往返 {rtt}ms");
client.ConnectionTimedOut += (c, message) => ShowReconnectDialog(message);  // 此时已断开，可接重连
```

几个刻意的实现选择：

- 心跳回包在**接收线程**上直接判定，不走主线程派发队列 —— 否则 Unity 卡顿/失焦导致 `Update` 不跑时会被误判成超时。
- 超时处理顺序：记日志 → 尽力发 `system.bye` → 触发 `ConnectionTimedOut` → 关 socket → 触发 `Disconnected`。
- 服务器端心跳只做最小回包、不打日志；心跳的**帧日志**也被 `GameServerOptions.FrameLogExclusions` 默认排除，
  否则 5 秒一条会把 Console 刷满。排查心跳问题时把这两项移除即可看到。
- 想让服务器也清理掉线客户端，用同一套机制反过来做：服务器侧记录每个连接最后一次心跳时间，超时 `peer.Close(...)`。

## 4.5 断线自动重连（Unity 客户端）

`GameClientBehaviour` 内置**通用断线自动重连**：运行中连接意外断开（心跳超时、服务器重启、网络闪断、被踢）时，
自动用上次登录的账号密码重新登录，业务数据随之恢复（重登录后服务端会推全量快照）。

### 配置（Inspector 的"断线自动重连"分组）

| 字段 | 默认 | 说明 |
|---|---|---|
| `autoReconnect` | true | 意外断开时自动重连；主动 `Disconnect()` 不触发 |
| `reconnectMaxAttempts` | 3 | 最大尝试次数，用尽发 `ReconnectFailed` |
| `reconnectRetryDelay` | 2f | 每次失败后的重试间隔（秒） |

### 事件

```csharp
client.Reconnecting  += c => ShowTip("连接断开，正在重连…");
client.Reconnected   += c => HideTip();                  // 已重新登录
client.ReconnectFailed += (c, reason) => ShowLoginPanel(); // 次数用尽，请重新登录
```

### 行为要点

- **触发**：`Disconnected`（非主动）→ 自动重连。心跳超时（`ConnectionTimedOut`）底层会接着触发 `Disconnected`，
  所以重连只在 `OnDisconnected` 启动一次，不会重复触发。
- **主动断开不重连**：`Disconnect()`（退出登录/换账号）设主动标志，后续断线不重连；下一次 `Login`/`LoginAsync` 清除该标志。
- **重连流程**：`LoginAsync(LastLoginAccount, LastLoginPassword)` → 成功 `Reconnected`；失败按间隔重试；
  重试期间如果检测到已连接（例如平台层「回前台重连」先成功），自动停止。
- **与平台层协调**：`PlatformManager` 的「回前台重连」和网络层自动重连共用 `LoginAsync`（带 `_loginInFlight` 锁），
  不会并发跑两个登录；回前台时如果网络层已重连成功，平台层的 `IsConnected` 检查会直接跳过。
- **重连成功后的业务恢复**：重登录 → 服务端推全量快照 → `PlayerSystem`/`BagSystem` 自动填充，业务无感。

## 5. 快速开始

### 后端（独立进程，推荐）

```bash
start-server.cmd                    # 工程根目录，监听 127.0.0.1:7777，自带开发账号 dev/dev
start-server.cmd --port 9000        # 指定端口
start-server.cmd --no-dev-account   # 不添加开发账号（所有登录都会失败，直到你自己加）
start-server.cmd --selftest         # 全套自检：协议表 + 报文层 + 登录 + 心跳超时
start-server.cmd --protocoltest     # 只打印协议名总表并校验
start-server.cmd --frametest        # 只跑报文层自检（半包/粘包/非法长度）

# 等价命令（注意是工程根目录下的 ServerHost，不是 Assets/Scripts/Framework/NetFramework/ServerHost）
dotnet run --project ServerHost
```

端口被占用会自动往后找（7777 → 7778 …）并打印提示；显式 `--port` 时不会自动改端口，直接报错退出。
启动后可用命令：`clients`（看连接与登录状态）、`quit`。

后端进程内置了静态资源站（热更资源），默认监听 `http://127.0.0.1:8000/`，
根目录会自动从当前目录逐级向上找 `ServerData`。也就是说**逻辑服和热更资源站只需要起一个后端进程**，
不用再单独开 Node 静态服务器：

```bash
start-server.cmd                    # 同时提供 127.0.0.1:7777（逻辑服）和 127.0.0.1:8000（热更资源）
start-server.cmd --web-port 9000    # 换资源站端口
start-server.cmd --no-web           # 只跑逻辑服，不提供热更资源
start-server.cmd --web-root <目录>  # 手动指定资源根目录
```

资源站端口被占用时只打警告，不影响逻辑服启动。

### 后端（就在 Unity 里跑）

菜单 `Game Framework`：

- `Start Local Server` / `Stop Local Server` / `Server Status`
- `Dev Tools > 添加开发账号（dev/dev）`
- `Dev Tools > 注册并登录（dev/dev）` —— 真的走一遍注册 + 登录 + 心跳，用来确认链路通
- `Dev Tools > 注册新账号并登录（自动命名）` —— 用 `tester-xxxxxx` 走一遍真实注册流程

编辑器内起服务器时会顺带把资源站也拉起来（同样默认 8000 端口，根目录取工程根的 `ServerData`），
停服务器时一起停；找不到目录或端口被占用只会警告，不影响逻辑服。

注意：**Unity 重编译脚本（domain reload）会杀掉编辑器进程里的服务器**，Start 过会自动重新拉起；
长期跑后端请用独立进程。

### 前端（Unity）

1. 场景里建一个空物体，挂 `GameClientBehaviour`，填 `host` / `port` / `account` / `password`。
2. 在合适的时机（点"进入游戏"）调用 `Login()`；没有账号就先 `Register()` 或直接 `RegisterAndLogin()`。
   框架不会自动连接。
3. 登录成功后注册业务协议：

```csharp
net.Router.RegisterOrReplace(ProtocolId.PlayerMove, (msg, peer) =>
{
    var move = msg.Deserialize<MoveRequest>(peer.Serializer);
    // 已经在主线程，可以随便用 Unity API
});
net.Send(ProtocolId.PlayerMove, new MoveRequest { X = 1 });
```

## 6. 线程模型

- 每条连接一个后台接收线程：`Receive → FrameParser 拆包 → 事件 → 路由派发`。
- 发送用锁串行化，多个线程同时发消息也不会把两条帧的字节交错。
- 后端默认在接收线程直接执行处理函数（`ImmediateDispatcher`）；Unity 侧换成 `QueuedDispatcher`，
  回调在 `Update` 里执行，因此处理函数里可以安全调用 Unity API。
- 请求-响应：`client.SendRequest<请求类型, 响应类型>(...)` 会阻塞等待回包（登录就是这么实现的）。
  这是简化实现（同一时刻同一响应协议只支持一个等待者），高并发场景应在内容里带请求 id 做关联。

## 7. 依赖

- Unity 侧：`com.unity.nuget.newtonsoft-json`（3.2.2），Unity 官方分发的 Newtonsoft.Json，
  是 Unity 生态里用得最多的 JSON 库，前后端共用同一套序列化行为。
- 后端：.NET 8 + `Newtonsoft.Json` 13.0.3。
- 想再省带宽：把 `IMessageSerializer` 换成 MessagePack / Protobuf 实现，前后端同时替换即可，报文层不用动。

## 8. 已验证（`start-server.cmd --selftest`）

```
===== 1/5 协议名总表 =====
  LoginRequest               login.request            13      OK
  LoginResponse              login.response           14      OK
  SystemPing / SystemPong / SystemHeartbeat / SystemHeartbeatAck / SystemBye ...  OK
  协议表校验：通过（枚举与名字一一对应、无重名、长度合法）

===== 2/5 报文层 =====
  整帧 100 字节 = 包头 21 字节 + 内容 79 字节
  包头第 1 段 nameLen = 13（协议名称的字节长度）
  包头第 3 段 bodyLen = 79（消息内容的字节长度）
  半包测试（逐字节喂 100 次）：拆出 1 条 -> OK
  粘包测试（两条拼一起）：拆出 2 条 -> OK
  非法报文测试（nameLen = -5）：已拒绝 OK

===== 3/5 账号存储（增删改查 + 持久化 + 密码哈希）=====
  注册账号成功：selftest-user（存储=sqlite）-> OK
  密码以哈希保存（不存明文）：pbkdf2$sha256$10000$zIjj2uNM… -> OK
  道具：potion_hp=8（期望 8），gold_key=3（期望 3），sword_iron=0（期望 0）
  道具增删改查 -> OK
  数量不足时扣道具被拒绝：True -> OK
  重新打开存储后读取：等级=12 经验=3456 金币=999 力量=18 道具格数=2
  持久化（重启后数据还在）-> OK
  改密码后：旧密码被拒=True，新密码可用=True -> OK
  删除账号：deleted=True，再查已不存在=True -> OK

===== 4/5 登录 =====
  账号过短被拒：reason='invalid_account'，message='账号长度需在 3~20 个字符之间' -> OK
  弱密码被拒：reason='weak_password'，message='密码至少 6 位' -> OK
  注册成功：account='reg-net-test'，playerId='注册测试号' -> OK
  重复注册被拒：reason='account_exists' -> OK
  注册后的账号可以登录：playerId='注册测试号' -> OK
  注册并登录（账号已存在时直接登录）：playerId='注册测试号' -> OK
  全角数字 '９９８８７７６６' 注册 -> playerId='99887766'（应为 99887766） -> OK
  带尾随空格的 '99887766 ' 被识别成同一个账号：reason='account_exists' -> OK
  用半角数字 '99887766' 能登录上面注册的账号 -> OK
  账号中间带空格：message='账号的第 3 个字符不合法：空格（U+0020）…' -> OK
  密码错误被拒：reason='invalid_credentials' -> OK
  登录失败后服务器已断开该连接 -> OK
  未登录发送其它协议被服务器拦截并告警 -> OK
  登录成功：playerId='player-login-test'，sessionId='02070c4433...' -> OK
  服务器侧已登录客户端数 = 1 -> OK
  登录后心跳正常：1.2 秒收到 3 次回包 -> OK
  账号不存在与密码错误返回同一提示（不泄漏账号是否存在）-> OK

===== 5/5 心跳 =====
  已让服务器停止回心跳，等待客户端判定超时并断开…
  连接超时：546ms 没有收到心跳回包（心跳间隔 500ms，超时阈值 500ms）
  超时回调=True，客户端已断开=True -> OK

全部自检通过
```

Unity 编辑器内实测（`Dev Tools > 登录并连接`）：

```
[SERVER] 账号存储（json）新建：D:\unity\game\MCP\GameData\accounts.json
[SERVER] 服务器已启动：127.0.0.1:7777（内容格式=json，最大连接=128）
[框架] 服务器运行中：127.0.0.1:7777，在线连接 0，已登录 0，账号库=json（0 个账号）
[SERVER] 账号已注册：account='dev'，playerId='player-dev'
[SERVER] [client-1] 收到 'login.request'，内容 114 字节：{"Account":"dev","Password":"dev",...}
[SERVER] [client-1] 登录成功：account='dev'，playerId='player-dev'，sessionId='ba5812ac...'
[DEV] 登录成功：账号='dev'，playerId='player-dev'，服务器='editor-localserver'
[框架] 心跳正常，往返 2ms（第 1 次）
```

账号文件长这样（`GameData/accounts.json`，密码只存哈希）：

```json
{
  "Version": 1,
  "Accounts": {
    "dev": {
      "Account": "dev",
      "PasswordHash": "pbkdf2$sha256$100000$Q/96XQ/xh0Tz7b5p22e5ug==$Nxfg4Ld0J5KXt5zQpUA7sf0osqPHRajDoWjzcOTnOSg=",
      "PlayerId": "player-dev",
      "CreatedAtMs": 1790066658000,
      "LastLoginMs": 1790066660372,
      "Banned": false,
      "Player": { "Level": 1, "Exp": 0, "Gold": 0, "Attributes": {}, "Items": [] }
    }
  }
}
```

后端控制台 CRUD 实测（管道喂命令）：

```
register demo-user 123456   → 账号已创建：demo-user
setlevel demo-user 30       → 已修改
setattr demo-user strength 25 / agility 12
additem demo-user sword_fire 1 / potion_hp 10 / delitem potion_hp 3
account demo-user           → 等级=30 属性：strength=25，agility=12 道具：potion_hp x7、sword_fire x1
accounts                    → demo-user、dev 共 2 个
delete demo-user            → 账号已删除；再 account demo-user → 账号不存在
```

已知限制 / 踩坑记录：

- **别在 `Assets/Scripts/Framework/NetFramework/ServerHost/` 下执行 `dotnet run`** —— 那里只有 `Program.cs`，没有工程文件，
  会报"未指定项目"。用工程根目录的 `start-server.cmd`，或 `dotnet run --project ServerHost`。
- 改了框架文件夹的名字（比如 Framework → NetFramework）后，记得同步
  `ServerHost/GameServerHost.csproj` 里的 `<Compile Include="..\Assets\Scripts\...">` 路径。
- 后端进程正在跑的时候 `dotnet build` 会因为 exe/dll 被占用报 MSB3021/MSB3026；先停掉服务器，或加 `-p:UseAppHost=false`。
- Unity 后台不聚焦时脚本编译会被推迟，菜单/类型可能还是旧的：切一下 Unity 窗口让它编译完即可。

## 9. 协议推送（服务端主动下发）

前面 1~8 节都是「客户端发请求、服务端回包」。**推送**相反：服务端想发就发，客户端只收不回。

### 登记与转发

- 协议名统一登记在 `Shared/Protocol/Protocols.cs`，当前两条推送协议：
  - `player.info.push` —— 玩家全量快照（登录成功、等级/金币/属性变化时下发）
  - `player.bag.changed` —— 背包道具增量（某个道具数量变化时下发）
- payload 结构在 `Shared/Protocol/Messages/PlayerPushMessages.cs`
- 客户端收到的推送：`PeerConnection` -> `ProtocolRouter`（协议名 -> 单个回调）-> `ProtocolHub`（协议名 -> 多个订阅者）

`ProtocolHub`（`Client/ProtocolHub.cs`）的几个要点：

| 能力 | 说明 |
| --- | --- |
| 一对多 | 一条协议可以同时被多个系统订阅 |
| 强类型 | `Register<T>(ProtocolId, Action<T>, owner)` 直接拿 payload 对象，不用自己拆包 |
| 异常隔离 | 某个订阅者抛异常不会影响其它订阅者，也不会打断接收线程 |
| 热替换 | 重新登录会重建 `GameClient`，`Bind(router)` 把订阅挂到新路由表上 |
| 可诊断 | `Dump()` / `DescribeRegistered()` 查「这条协议到底谁在处理」 |

### 谁处理推送

**界面不订阅协议。** 处理写在 `Scripts/Business/Systems/`：
协议 -> System 校验/算数/写 Model -> `Publish(逻辑事件)` -> 界面 `Listen`。
详见 `Scripts/Business/README.md`。

### 后端本地调试

服务端控制台（`start-server.cmd` 起的那个窗口）支持：

```
push <账号>                  推一份全量玩家信息
gold <账号> <增减量>          改金币并自动推送
setlevel <账号> <等级>        改等级并自动推送
setattr <账号> <属性> <值>    改属性并自动推送
additem <账号> <道具> <数量>  加道具并自动推送
delitem <账号> <道具> <数量>  减道具并自动推送
```

自动化自检：`start-server.cmd --selftest`（基础收发）、`start-server.cmd --pushtest`（推送链路）。
