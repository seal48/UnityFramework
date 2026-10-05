# UIFramework（基于 UGUI）

一套轻量的 UGUI 界面框架，只做三件事：管理 UI 总根、管理界面注册与开关、给界面提供节点查找。

三个核心：**UIRoot（总根）**、**Panel（界面基类）**、**UIManager（总管理器）**。

## 目录结构

| 文件 | 作用 |
| --- | --- |
| `UILayer.cs` | 四个层级的枚举：Background / Normal / Popup / Top |
| `UIRoot.cs` | 挂在 UI Root 预制体上，管理 Canvas、四个层级、EventSystem |
| `UIInitOptions.cs` | 初始化参数，由 GameController 填写 |
| `UIPanelAttribute.cs` | `[UIPanel]` 注册特性，UIManager 自动扫描 |
| `Panel.cs` | 所有界面的基类（含 `BindNodes` 绑定钩子） |
| `UIManager.cs` | 总管理器：Root 加载、面板注册、打开 / 关闭、每帧驱动 |
| `Editor/UIBindGenerator.cs` | 编辑器工具：扫预制体生成 `XxxPanel.Bindings.g.cs`（见 5.5 节） |

UI Root 预制体：`Assets/Prefabs/UIRoot.prefab`。
**具体游戏界面**（LoginPanel / MainPanel / …）不在这个框架里，它们在 `Assets/Scripts/Game/UI/`。

## 1. UIRoot：UI 总根

预制体结构：

```
UIRoot            RectTransform + Canvas + CanvasScaler + GraphicRaycaster + UIRoot
├─ Background     背景层，永远压在最下面
├─ Normal         普通层，主界面 / 功能界面
├─ Popup          弹窗层，确认框 / 设置 / 背包
├─ Top           顶层，飘字 / Toast / Loading 遮罩 / 新手引导
└─ EventSystem
```

- 四个层级是 Canvas 的子节点，**靠 hierarchy 顺序决定显示前后关系**，越靠后的越显示在前面。
- Canvas 是 `ScreenSpaceOverlay`，CanvasScaler 是 1920x1080、MatchWidthOrHeight = 0.5。
- 由 UIManager 在初始化时实例化，并 `DontDestroyOnLoad`，**切换场景不销毁**。
- `EnsureReady()` 是幂等的：Canvas / CanvasScaler / GraphicRaycaster / 四个层级 / EventSystem 缺哪个补哪个，
  所以预制体没配全也能跑。

```csharp
RectTransform layer = UIRoot.Instance.GetLayer(UILayer.Popup);
```

## 2. Panel：界面基类

命名约定 `XXXXPanel`，继承 `Panel`。

### 节点映射

面板第一次取值时会遍历整棵 UI 树，建两张表：

- **短名表**：节点名 → GameObject。重名时命中最先找到的那个（会打一条 warning）。
- **路径表**：相对路径 `A/B/C` → GameObject。路径一定唯一，重名节点用这个取。

### 取组件：不用关心节点上挂的是什么类型

```csharp
Button  start   = Get<Button>("StartButton");            // 短名
Text    title   = Get<Text>("StartButton/Text (Legacy)"); // 相对路径，用于重名节点
Slider  volume  = GetInChildren<Slider>("VolumeGroup");   // 在节点下面递归找
bool    has     = Has("StartButton");
GameObject node = GetNode("StartButton");

List<Button> allButtons = new List<Button>();
GetAll(allButtons);                                      // 面板范围内所有 Button
```

- `Get<T>` 取不到会打 error 并返回 null；不想报错用 `TryGet<T>(name, out comp)`。
- 组件结果会按「节点 + 类型」缓存，重复取没有 GetComponent 开销。

### 生命周期（都可重载）

```csharp
public class ShopPanel : Panel
{
    protected override void OnInit()                  { } // 第一次创建 + 绑定完成后，只调一次
    protected override void OnShow(object userData)   { } // 每次显示（userData 来自 UIManager.Open）
    protected override void OnHide()                  { } // 每次隐藏
    protected override void OnUpdate()                { } // 显示期间每帧，由 UIManager.Tick 驱动
    protected override void OnClose()                 { } // 被销毁前，只调一次
}
```

两个约定：

- **不要重载 `Awake`**：绑定是懒执行的（第一次取值时触发），重载 Awake 反而容易漏掉基类逻辑。
- **不要重载 `OnDestroy`**：需要收尾请写 `OnClose`，框架保证它只执行一次。

面板内部还可以直接调 `Close()` / `Hide()` 关闭或隐藏自己。

## 3. UIManager：总管理器

```csharp
UIManager ui = GameController.Instance.UI;

ui.Open<ShopPanel>();                                 // 按类型打开
ui.Open("ShopPanel", userData, p => { });              // 按名字打开，userData 会传给 OnShow
ui.Close<ShopPanel>();  ui.Close("ShopPanel");         // 关闭（cache 的面板只隐藏）
ui.Hide("ShopPanel");                                  // 只隐藏，保留实例
ui.DestroyPanel("ShopPanel");                          // 强制销毁实例
ui.CloseAll();  ui.DestroyAll();                       // 批量

ui.IsOpen("ShopPanel");  ui.IsRegistered("ShopPanel");
ui.Get<ShopPanel>();     ui.Get("ShopPanel");          // 取实例（含隐藏缓存的）

ui.PanelOpened += name => { };
ui.PanelClosed += name => { };
```

细节：

- **异步加载 + 排队**：第一次 `Open` 是异步加载预制体的，加载期间重复 `Open` 会排队，
  加载完成后依次回调，不会重复实例化。
- **cache 策略**：`[UIPanel(..., cache: true)]` 关闭时只隐藏，下次打开更快；
  `cache: false` 关闭时直接销毁实例。
- **同层叠放**：每次显示都会 `SetAsLastSibling()`，所以同层级里后开的盖在先前开的上面。
- **预加载**：`UIInitOptions.PreloadPanels` 里的面板会在初始化时创建好并隐藏，减少第一次打开的卡顿。

## 4. 接入 GameController

所有框架都由 GameController 统一初始化。UI 依赖资源系统（Root 预制体也是资源），
所以初始化顺序是：**资源系统 → UI → 业务**。

```csharp
private void Awake()
{
    resource = new YooAssetResourceService();
    ui = new UIManager();                 // 只创建实例
}

private void Start()
{
    InitResource();                       // 资源就绪后回调里再 InitUI()
    gameClient.Init();
}

private void Update()
{
    if (ui != null) ui.Tick();            // 驱动各面板的 OnUpdate
}
```

`UIInitOptions` 可在 GameController 的 Inspector 里配置（Root 地址、是否常驻、是否自动注册、预加载列表）。

## 5. 加一个新界面

1. 在 `Assets/Prefabs/` 下做预制体 `ShopPanel.prefab`，根节点加 `ShopPanel` 脚本。
2. 写脚本（**类要写 `partial`**，节点字段由生成代码提供，见下一节）：

```csharp
using GameFramework.UI;

[UIPanel("ShopPanel", UILayer.Popup)]        // 名字 + 层级；预制体地址自动拼成 Assets/Prefabs/ShopPanel.prefab
public partial class ShopPanel : Panel
{
    protected override void OnInit()
    {
        closeButton.onClick.AddListener(Close);   // 字段名 = 节点名首字母小写
    }

    protected override void OnShow(object userData)
    {
        title.text = "商店";
    }
}
```

3. 生成绑定代码：菜单 `Tools/UI/绑定/生成全部面板的绑定代码`（或选中预制体单独生成）。
4. 打开：`GameController.Instance.UI.Open<ShopPanel>();`

`[UIPanel]` 的预制体地址规则：不写 `location` 时 = `UIInitOptions.PanelPrefabFolder + 面板名 + ".prefab"`；
预制体放在别处就显式写 `[UIPanel("ShopPanel", UILayer.Popup, "Assets/Art/UI/ShopPanel.prefab")]`。

## 5.5 UI 绑定代码生成（不用再写字符串）

界面以前这么写：`Get<Button>("LoginButton")` —— 字符串拼错只有**运行时**才发现，节点改名重构编译器帮不上忙。
现在用生成器：扫预制体，把节点生成成 `XxxPanel.Bindings.g.cs` 里的强类型字段。

```csharp
// XxxPanel.Bindings.g.cs —— 自动生成，不要手改
public partial class ShopPanel
{
    /// <summary>节点：CloseButton</summary>
    public Button closeButton;
    /// <summary>节点：Title</summary>
    public Text title;

    protected override void BindNodes()          // 在 OnInit 之前调用
    {
        closeButton = Get<Button>("CloseButton");
        title = Get<Text>("Title");
    }
}
```

### 用法

| 菜单 | 作用 |
|---|---|
| `Tools/UI/绑定/生成全部面板的绑定代码` | 扫 `Assets/Prefabs/` 下所有带 Panel 的面板，逐个生成 |
| `Tools/UI/绑定/生成选中预制体的绑定代码` | 只生成选中的（可多选） |
| `Tools/UI/绑定/校验全部面板（只检查不生成）` | 报告哪些面板不是 `partial`、哪些预制体缺脚本 |

### 规则

- **字段名** = 节点名首字母小写（`LoginButton` → `loginButton`）；非法字符换成 `_`。
- **字段类型** = 节点上的组件，按优先级取第一个：`Button > Toggle > Slider > ScrollRect > InputField > Dropdown > Text > Image`。
  想拿同一节点上的别的组件（比如按钮底图），仍然可以直接 `Get<Image>("LoginButton")`。
- **重名节点**：短名有歧义时自动改用相对路径当 key（`A/B/Text`），字段名也由路径生成，保证唯一。
- **保留名**：字段名和继承来的成员撞车时自动加组件后缀 —— 节点 `Name`（`Text`）会生成 `nameText`，
  避免屏蔽掉 `UnityEngine.Object.name` 这类成员。
- **不想生成的节点**：名字以 `_` 开头即可跳过。

### 注意

- 面板类必须是 **`partial`**，否则生成文件合并不进去（编辑器会明确报错告诉你怎么改）。
- 生成的字段和手写字段**同名会编译报错** —— 这正是迁移的信号：把界面里手写的
  `public Button loginButton;`（以及 `Get<Button>("LoginButton")` 那几行）删掉即可，用法一行都不用改。
- 生成的 `.g.cs` 不要手改，重新生成会覆盖。

## 6. 注意事项

- **YooAsset 资源收集器要收集 `Assets/Prefabs`**，Root 和面板预制体的地址就是资源路径本身
  （例如 `Assets/Prefabs/UIRoot.prefab`）。在此之前 `rootOptions` 里的 Root 加载会失败，
  开发期可以靠 `UIInitOptions.CreateRootIfMissing` 用代码临时建一个 Root 顶一下。
- 面板预制体的**根节点必须挂 Panel 子类组件**，否则打开时报错。
- 场景里**不要再放一份 UIRoot / Canvas**，UI 全部由框架从预制体实例化，避免出现两个 EventSystem 或层级错乱。
- 层级顺序是 Canvas 子节点的顺序：要调整前后关系就调层级顺序，不要跨层级调面板。


## 7. 面板动画（UIAnim）

动画层是对 DOTween 的一层薄封装，目的只有一个：**在面板里按节点名直接对组件做动画**，
不用先拿到组件、也不用记它是什么类型。它是 `Panel` 的一部分，不单独初始化。

依赖：DOTween（已装好，工程无 asmdef，模块自动被引用，不需要执行 Setup）。

### 基本用法（写在 Panel 子类里）

```csharp
protected override void OnShow(object userData)
{
    Anim.Pop("Icon");                        // 弹一下
    Anim.FadeIn("Title");                    // 淡入
    Anim.Move("Award", new Vector2(0f, 0f)); // 按 anchoredPosition 位移
    Anim.CountTo("Gold", 1200, 0.6f);        // 数字滚动
    Anim.TextTo("Name", "Legend");           // 打字机
}
```

所有方法的第一个参数都是**节点名**（短名或相对路径，规则同 `Get<T>`），
时长传负数（默认 `-1`）表示用全局默认时长。返回值就是 DOTween 的 `Tween` / `Sequence`，
可以继续链式调用：`Anim.FadeIn("Title").SetDelay(0.1f).OnComplete(() => { });`

### 常用接口

- 透明度：`Fade` / `FadeIn` / `FadeOut`（走 CanvasGroup，缺了自动补）、`FadeGraphic`（只改 Graphic）
- 缩放：`Scale`、`ScaleFrom`、`Pop`（弹一下）
- 位移：`Move`、`MoveOffset`（相对移动）、`MoveX`、`MoveY`
- 其他：`Rotate`、`Tint`、`FillAmount`、`SliderValue`、`CountTo`、`TextTo`
- 立即设值（搭起始状态用）：`SetAlpha`、`SetScale`、`SetAnchoredPos`
- 控制：`Kill(node)`、`KillAll()`、`IsPlaying(node)`、`IsPlayingAny`、`NewSequence()`

### 面板开关动画

整屏的入场 / 退场动画，在预制体的 `Panel` 组件上选预设即可：

- `ShowPreset`（默认 `FadeScale`）、`HidePreset`（默认 `Fade`）
- 预设枚举：`None` / `Fade` / `Scale` / `FadeScale` / `SlideFromLeft` / `SlideFromRight` / `SlideFromTop` / `SlideFromBottom`
- 需要特别表现时，在面板里重载这三个生命周期，默认实现就是播 `ShowPreset` / `HidePreset`：

```csharp
protected override void OnPrepareShowAnimation() { Anim.PreparePreset(ShowPreset); } // 显示前搭起始状态
protected override void OnShowAnimation() { Anim.PlayPreset(ShowPreset, true, ShowDuration); }
protected override void OnHideAnimation() { Anim.PlayPreset(HidePreset, false, HideDuration); }
```

`UIManager.Open` 会等入场动画播完再触发 `onOpened` 回调；`Close` / `Hide` 也是先播退场再真正隐藏，
`CloseAll()` 例外（直接收，不播退场）。面板可通过 `IsAnimating` 查询是否正在动画。

### 全局参数

`UIInitOptions.Anim`（`UIAnimOptions`）里配置：总开关 `Enabled`、默认时长、入场 / 退场时长与缓动、
是否忽略 `Time.timeScale`、Slide 距离、起始缩放、动画期间是否屏蔽点击、DOTween 池容量。
总开关关掉后，所有时长变 0、瞬间完成，流程与回调不变（方便自动化测试 / 低端机降级）。

### 打断与安全

- 面板隐藏 / 销毁时 `UIManager` 会调用 `KillAnimations()`，不会留下半透明的“幽灵”面板；
- 每个动画都 `SetLink(面板)`，面板销毁时 DOTween 自动 Kill；
- 同一目标同一属性重复播放按 DOTween 默认规则覆盖，连点不会叠加；
- 取不到节点、或开关关掉时返回一个「空 Sequence」，它立即完成，链式写法与回调都不会断。