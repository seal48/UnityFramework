# 对象池框架

一份预制体 = 一个池。父子节点、激活状态、生命周期回调都由框架管，
业务只做两件事：`Spawn` 拿一个、用完 `Despawn` 还回去。

## 一眼看懂

```
GameController.Awake   pool = new ObjectPoolManager()            只创建，不初始化
ProcedureInitPool      GameController.Instance.Pool.Init(...)    建池根节点 + 按配置预热
业务                   Pool.Spawn(prefab, parent); Pool.Despawn(go);
切场景                 GameController 是 DontDestroyOnLoad，池跟着活着
退出                   GameController.OnDestroy -> pool.Shutdown()
```

## 用法

取 / 还：

```csharp
GameObject go  = GameController.Instance.Pool.Spawn(prefab);
GameObject go2 = GameController.Instance.Pool.Spawn(prefab, parent, position, rotation);
GameController.Instance.Pool.Despawn(go);

// 顺手取组件：预制体根节点上没有这个组件时会还回去并告警
var view = GameController.Instance.Pool.Spawn<ItemView>(prefab, parent, position, rotation);

// 延迟归还：特效、飘字这种播完就没人管的
GameController.Instance.Pool.Despawn(go, 2f);
```

按地址取（预制体第一次用到时才异步加载，加载完回调）：

```csharp
GameController.Instance.Pool.SpawnAsync("Assets/Prefabs/Effects/Hit.prefab", parent,
    go => { if (go != null) go.transform.localScale = Vector3.one; });
```

预制体上的脚本要收生命周期回调，就继承 `PoolBehaviour`：

```csharp
public sealed class Bullet : PoolBehaviour
{
    public override void OnPoolSpawn()   { /* 取出、激活之后：恢复初始状态 */ }
    public override void OnPoolDespawn() { /* 归还、隐藏之前：停协程 / 清数据 / 断事件 */ }
    public override void OnPoolDestroy() { /* 真销毁：池满、池释放、切场景 */ }
}
```

不想继承也行，直接实现 `IPoolableObject`，只写关心的那一个。

纯 C# 对象（`List`、消息体、树节点，跟 GameObject 无关）用 `Pool<T>`，省掉反复 new + GC：

```csharp
private readonly Pool<List<int>> listPool =
    new Pool<List<int>>(() => new List<int>(), onRelease: list => list.Clear(), maxSize: 32);

List<int> list = listPool.Get();
listPool.Release(list);
```

## 配置

`GameController` Inspector 的「对象池」就是 `PoolInitOptions`：

| 字段 | 作用 |
| --- | --- |
| `RootName` | 池根节点名字，默认 `[ObjectPool]` |
| `DontDestroyOnLoad` | 切场景保留池根节点，默认开 |
| `Default` | 每个池的默认 `PoolOptions` |
| `Prewarm` | 启动预热列表（资源地址 + 数量） |
| `ClearPoolsOnSceneChange` | 切场景时自动清空所有池，默认关 |
| `LogOnInit` | 初始化完打一条统计日志 |

单个池的 `PoolOptions`：

| 字段 | 作用 |
| --- | --- |
| `MaxSize` | 池里最多留多少个空闲实例，超出的归还时直接销毁（默认 64，0 = 不限） |
| `MaxAlive` | 同时存活上限，超过时 `Spawn` 返回 null 并报错（默认 0 = 不限），防止一种资源吃满内存 |
| `IdleShrinkDelay` | 池空闲多久开始分批回收，<= 0 表示不回收（默认 60 秒） |
| `CollectionCheck` | 重复归还 / 归还错池时打错误日志，建议开发期打开（默认开） |
| `PrewarmCount` | 池第一次创建时顺手预热多少个（默认 0） |

## 目录

| 文件 | 说明 |
| --- | --- |
| `ObjectPoolManager.cs` | 总入口：池注册表、根节点、延迟归还、统计数据。`GameController` 持有它 |
| `GameObjectPool.cs` | 单个池；同文件的 `PooledInstance` 是挂在实例上的标记，负责把漏归还回报给池 |
| `Pool.cs` | 纯 C# 的 `Pool<T>`，完全不碰 GameObject |
| `IPoolableObject.cs` | `IPoolableObject` / `PoolBehaviour` / `IPoolableItem` 三个生命周期约定 |
| `PoolOptions.cs` | `PoolOptions` / `PoolInitOptions` / `PoolStatistics` |
| `ReferenceComparer.cs` | 内部用的引用比较器，兼容 Unity 的「假 null」 |

## 注意

- **Spawn 出来的对象必须 Despawn 回去。** 直接 `Destroy` 会被 `PooledInstance` 抓到并报「漏归还」，
  池里可用实例会越用越少（不是内存泄漏，但会反复新建）。
- 框架不重置对象状态，状态复位写在 `OnPoolSpawn` 里。回调时机：`OnPoolSpawn` 在对象**已激活**之后，
  `OnPoolDespawn` 在对象**还没隐藏**之前。
- 归还只做 `SetParent(池节点)` + `SetActive(false)`，**不改位置**；需要归位就在 `OnPoolSpawn` 里自己设。
- 池化实例的名字会被改回预制体原名（去掉 Unity 自动加的 `(Clone)`），层级面板里好认。
- 释放：`ReleasePool(prefab)` 释放单个池、`ClearAll()` 清空全部池但保留根节点、`Shutdown()` 整体关闭
 （`GameController.OnDestroy` 自动调）。
- 同一个预制体永远只对应 0 或 1 个池；用地址 `SpawnAsync` 和用引用 `Spawn` 会命中同一个池。
- `MaxAlive` 触发时是**报错 + 返回 null**，不是排队等待；看到这条日志基本就是有地方忘了归还。