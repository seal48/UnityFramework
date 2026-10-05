# 业务层（Business）

把「服务端推送」翻译成「界面能听懂的本地事件」的一层。
界面不认识协议名，只认识 `PlayerInfoChangedEvent` 这类逻辑事件。

## 整条链路

```
后端 PlayerPushService
   |  推 player.info.push / player.bag.changed
   v
PeerConnection 收包
   v
ProtocolRouter（协议名 -> 回调，1 对 1）
   v
ProtocolHub（协议名 -> 多个订阅者，1 对多，订阅者异常互相隔离）
   v
PlayerSystem / BagSystem（校验、算数、写 PlayerModel）
   v
事件总线 Publish(PlayerInfoChangedEvent / BagChangedEvent)
   v
MainPanel 等界面 Listen -> 刷新显示
```

一句话规矩：**只有 System 碰协议，界面只碰逻辑事件。**

## 目录

| 路径 | 作用 |
| --- | --- |
| `BusinessManager.cs` | 业务层总入口，由 GameController 创建 / 销毁 |
| `Models/PlayerModel.cs` | 玩家数据（只读），System 写，界面读 |
| `Systems/GameSystem.cs` | 业务系统基类（Bind / Subscribe / Publish） |
| `Systems/PlayerSystem.cs` | 处理 `player.info.push` -> `PlayerInfoChangedEvent` |
| `Systems/BagSystem.cs` | 处理 `player.bag.changed` -> `BagChangedEvent` |
| `Events/PlayerEvents.cs` | 逻辑事件定义（struct） |

## 怎么用

初始化（业务层依赖事件总线，所以放在事件总线之后）：

```csharp
business = new BusinessManager(clientBehaviour, events, new UnityNetLogger("HUB"));
business.Hub.LogPush = logPushMessages;
```

拿数据：

```csharp
var model = GameController.Instance.Business.Player;
Debug.Log(model.PlayerName + " Lv." + model.Level);
```

界面订阅：

```csharp
Listen<PlayerInfoChangedEvent>(OnPlayerInfoChanged);
Listen<BagChangedEvent>(OnBagChanged);
```

## 新增一条推送要改哪里

1. `NetFramework/Shared/Protocol/Protocols.cs` 登记协议名（唯一登记处）
2. `Shared/Protocol/Messages/` 加 payload 结构
3. 后端 `Server/Builtin/PlayerPushService.cs`（或自己的 Service）推送
4. `Business/Events/` 加逻辑事件
5. `Business/Systems/` 加（或改）一个 System：`Subscribe<T>(协议, ...)` -> 写 Model -> `Publish(事件)`
6. 界面 `Listen<事件>` 刷新 —— **界面永远不改协议名**

## 排查

```csharp
Debug.Log(GameController.Instance.Business.Dump());
```

输出：每条协议有几个订阅者、累计收到几条、被谁处理了；以及每个 System 处理了多少条。
