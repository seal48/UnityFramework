# 本地化框架（LocalizationFramework）

文本 + 图片 + 字体一套搞定，**数据全在配置表（Excel）里**，复用 Config 框架的导出 / 热更管线。
核心思路：**每语言一张表，同一个 key 按当前语言取到不同的文本和图片**。

## 一眼看懂

```
Assets/Config/Excel/LocZh.xlsx / LocEn.xlsx   文本 + 图片地址（同 key）
Assets/Config/Excel/LocFont.xlsx              语言 → 字体地址
        |  Tools/配置表/导出全部表（或 ConfigTool.exe）
        v
生成 TbLocZh / TbLocEn / TbLocFont + ConfigDatabase 自动带上并预加载

LocalizationManager         当前语言 / GetText / GetImageAddress / 字体 / 换语言
    └─ 换语言 → LanguageChangedEvent → 界面上的 LocalizedElement 自动刷新
LocalizedElement            挂到带 Text / Image 的节点上，填 key 就完事
```

## 表格设计

`LocZh.xlsx`（每语言一张表，结构相同）：

```
##var    Key          Text     Image
##type   string       string   string
##group  B            C        C
##       键          中文文本  图片资源地址（可空）
         main_title  主城      UI/Main/title_zh
```

`LocEn.xlsx` 同样的 key：`main_title | Main City | UI/Main/title_en`。

**主键（第一列）必须 `##group=B`**（导出工具强制：两端都要能查表），值列用 `C`（纯客户端，服务端不需要）。

`LocFont.xlsx`：

```
##var   Lang     FontAddress
##group B        C
        zh-CN    Assets/Fonts/NotoSansSC-Regular
```

## 用法

```csharp
// 取文本 / 图片地址（按当前语言）
string text  = GameController.Instance.Localization.GetText("main_title");
string image = GameController.Instance.Localization.GetImageAddress("btn_confirm");

// 换语言（改存档 + 发事件，界面自动刷新）
GameController.Instance.Localization.SetLanguage("en-US");
```

界面：给带 `Text` / `Image` 的节点挂 `LocalizedElement`，填 `key` 即可：

```
┌─ LoginPanel (GameObject)
│  ├─ LoginButton   (+ LocalizedElement, key = "login_button")   → 文本+图片一起换
│  ├─ Title         (+ LocalizedElement, key = "main_title")     → 只换文本（没 Image 组件）
│  └─ LoadingTip    (+ LocalizedElement, key = "loading_tip")     → 纯文本
```

`LocalizedElement` 约定：

- 文本和图片默认共用 `key`；
- 个别元素图片要单独换 → 填 `imageKey`（文本用 `key`，图片用 `imageKey`）；
- 语言切换时自动刷文本 + 图片 + 字体，不用重开面板；
- 图片是异步加载的，加载期间换语言会自动丢弃过期结果。

## 字体

- 每个语言在 `LocFont` 表里配一个字体资源地址；
- `LocalizationManager` 按当前语言异步加载并缓存，`LocalizedElement` 自动套到 Text 上；
- 地址为空 / 加载失败 → 用默认字体（打一次警告，不崩）。

字体资产怎么做（要你自己定）：

| 方案 | 说明 |
|---|---|
| 每语言一个字体资产（推荐） | 中文放一个子集字体，英文放一个拉丁字体；配合「字符子集化」控制包体 |
| 动态 OS 字体 | `Font.CreateDynamicFontFromOSFont(...)`，零资产但渲染不可控 |
| TMP + 回退链 | manifest 里已有 `com.unity.textmeshpro`；TMP_FontAsset 支持 fallback chain，但要把 UI 从 `Text` 迁到 TMP |

**字符子集化建议**：中文字库 10-20MB 太肥。可以让导出工具顺手扫所有 Loc 表的中文文本，
生成用到的字符集，再对字体裁剪 —— 字体重量 = 配置表里实际出现的字。

## 加一种语言

1. `Assets/Config/Excel/` 加 `LocXx.xlsx`（同 key，文本/图片/字体换成对应语言）；
2. `Tools/配置表/导出全部表`；
3. `LocalizationOptions.SupportedLanguages` 加一项；
4. `LocalizationManager.RegisterTables()` 加一行；
5. 走热更流程（`构建资源` → 上传），**不需要改界面代码**。

## 接入 GameController

- `Awake`：创建 `LocalizationManager`（`Localization` 属性可访问）；
- 启动链新增 `ProcedureInitLocalization`（在 `ProcedureInitConfig` 之后、`ProcedureInitPool` 之前，
  因为本地化依赖配置表）；
- `OnDestroy`：`localization.Shutdown()`（释放缓存的字体资源）。

## 目录

| 文件 | 作用 |
| --- | --- |
| `LocalizationManager.cs` | 语言状态 / 查询 / 换语言 / 字体缓存 |
| `LocalizedElement.cs` | UI 组件：一个 key 自动刷文本 + 图片 + 字体 |
| `LocalizationOptions.cs` | 默认语言 / 回退语言 / 支持语言列表 |
| `LocalizationEvents.cs` | `LanguageChangedEvent` |

## 已知限制 / 取舍

- **缺 key 回退策略**：当前语言没有 → 回退语言（默认中文）→ 再没有 → 原样输出 key（方便抓漏）；
- **文本长度差异**（英文更长）：UI 要留余量或用 ContentSizeFitter，别写死宽高；
- **格式化 / 复数**：`GetText` 目前返回原始文本；需要 `{0}` 参数 / 英文复数的场景，
  在 `GetText` 上再加个带参数的重载（复数按 key 分条 `item.count.one` / `item.count.many`）；
- **业务字段**（Item 的 Name / Desc 等）目前还是字面量，没迁到 key —— 想跟着语言走，
  把表里改成 `NameKey` / `DescKey` 指向 Loc 表，取的时候用 `GetText(item.NameKey)`；
- **服务端文案**：loc 表是 `##group=C` 不进服务端；服务端继续发「原因码」，客户端翻译
  （LoginReasons 已经是码了）。
