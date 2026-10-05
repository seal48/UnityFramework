# 事件框架（事件总线）

模块之间只认「事件类型」，互相不认识。UI 不用引用战斗，战斗不用引用任务，
谁想听谁自己订阅——拆模块和复用都靠它。

内核就一个类：`EventBus`（实现 `IEventBus`）。由 `GameController` 统一创建和驱动。

## 定义事件

只需要实现 `IGameEvent` 标记接口。**优先用 struct**：派发时零 GC，而且事件天生是快照，
订阅方改不到原件。只有需要继承 / 多态、或者字段很多的事件才用 class。

```csharp
using GameFramework.Event;

public struct PlayerLevelUpEvent : IGameEvent
{
    public int PlayerId;
    public int Level;
}
```

事件放业务自己的目录下（框架不关心事件长什么样），命名一律 `XxxEvent`。

## 订阅 / 退订

```csharp
// 拿到总线：业务里用 GameController.Instance.Events，
// 没有 GameController 的地方（编辑器工具等）可以用 EventBus.Global
IEventBus bus = GameController.Instance.Events;

// 订阅：返回的句柄 Dispose 就退订
IDisposable sub = bus.Subscribe<PlayerLevelUpEvent>(OnLevelUp);

// priority 越大越先收到（同一个事件类型内排序，相等则按订阅先后）
bus.Subscribe<PlayerLevelUpEvent>(OnLevelUpFirst, priority: 10);

// 只收一次，触发后自动退订
bus.SubscribeOnce<PlayerLevelUpEvent>(OnLevelUpOnce);

// 退订
sub.Dispose();
bus.Unsubscribe<PlayerLevelUpEvent>(OnLevelUp);   // 等价写法
bus.UnsubscribeAll<PlayerLevelUpEvent>();         // 退掉这个类型的全部订阅

private void OnLevelUp(PlayerLevelUpEvent e)
{
    Debug.Log(e.Level);
}
```

## 在界面里订阅（推荐）

`Panel` 上包了一层：`Listen` 订阅，**面板销毁时自动退订**，不用自己管句柄。

```csharp
public class BagPanel : Panel
{
    protected override void OnInit()
    {
        // 面板销毁时自动退订
        Listen<PlayerLevelUpEvent>(OnLevelUp);
    }

    protected override void OnClose()
    {
        // 这里不用再关心退订
    }

    private void OnLevelUp(PlayerLevelUpEvent e)
    {
        Refresh();
    }
}
```

注意：`Listen` 的生命周期是**面板实例**，不是「显示期间」。
`UIManager.Close` 对缓存面板只是隐藏，隐藏期间照样会收到事件。
只想在显示期间收事件的话，别用 `Listen`，自己按显示的开关来：

```csharp
private IDisposable sub;

protected override void OnShow(object userData)
{
    sub = Events.Subscribe<PlayerLevelUpEvent>(OnLevelUp);
}

protected override void OnHide()
{
    if (sub != null) { sub.Dispose(); sub = null; }
}
```

## 发布

```csharp
// 立即派发（同步）：订阅方按优先级依次执行，全部执行完才返回
bus.Publish(new PlayerLevelUpEvent { PlayerId = 1, Level = 5 });

// 入队，等下一次 Tick 派发
bus.Post(new PlayerLevelUpEvent { PlayerId = 1, Level = 5 });
```

怎么选：

- **在事件处理函数里又要发事件** → 用 `Post`，否则就是层层递归，容易爆栈、也难排查顺序。
- **一帧里要发很多条**（比如伤害飘字） → 用 `Post`，让它们攒到下一帧统一走。
- **就是想让「发完 = 处理完」**（比如发完事件立刻读结果） → 用 `Publish`。

## 行为约定

- 一个事件类型一条「通道」，同一条通道内按优先级从高到低执行（相等则按订阅先后），顺序是确定的；
- **派发中订阅 / 退订是安全的**：本轮已经开始派发的事件不会因为增删而乱序或漏派；
  派发中新增的订阅从下一次派发开始生效，派发中退订的订阅本轮就不会再被调到；
- 某个 handler 抛异常只打日志，不影响同一条事件里的其它 handler；
- 没人订阅时 `Publish` 是零开销（连通道都不会创建）；
- 没有订阅、也没有待派发事件的通道会在 `Tick` 时回收，事件类型不会越用越多；
- `Post` 有死循环保护：一次 Tick 里单个类型最多派发 `EventBusOptions.MaxEventsPerFlush` 条，超过就报错中断。

## 只能主线程

总线不做线程安全。网络消息请先经 `IDispatcher` 回到主线程再发事件
（`GameClientBehaviour` 已经这么做了，业务侧直接写在回调里即可）。

## 调试

```csharp
Debug.Log(GameController.Instance.Events.Dump());   // 当前谁在听哪个事件
```

`GameController` 的 Inspector 里有 `EventBusOptions.LogPublish`，打开后每次派发都会打 Console，
用来追事件流（正式版记得关掉）。

## 参数

| 参数 | 说明 |
| --- | --- |
| `LogPublish` | 每次派发打 Console，开发期看事件流用 |
| `MaxEventsPerFlush` | 一次 Tick 里单个事件类型最多派发多少条延迟事件，防死循环 |
