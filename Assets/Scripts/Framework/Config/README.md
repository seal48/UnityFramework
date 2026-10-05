# 配置表框架（Excel -> C# 类 + 二进制）

Excel 是唯一编辑源；导出工具生成「C# 类 + 二进制数据 + JSON 快照」。
客户端和服务端编译**同一份生成代码**，各自读自己那一侧的 `.bytes`（靠 `#if` 分端），
所以不存在两边字段对不上、或者客户端能看到服务端字段的问题。

## 一眼看懂

```
Assets/Config/Excel/*.xlsx                 <- 你改这里（一个文件 = 一张表）
        |  Tools/配置表/导出全部表
        v
Assets/Scripts/Framework/Config/Generated/*.g.cs     生成的强类型代码（不要手改，两端共用）
Assets/ConfigData/*.bytes                  客户端运行时数据（进包、走热更）
Assets/ConfigDataServer/*.bytes            服务端运行时数据（不进包，部署时拷到服务器）
Assets/Config/Json/*.json                  文本快照（进 git 用来比对差异，不打进包）
Assets/Config/Json/used_chars.txt          文本字符集（字体子集化用，进 git，不打进包）
```

运行时访问：

```csharp
var item = GameController.Instance.Config.Database.Item.Get(1001);
Debug.Log(item.Name + " " + item.Quality + " 掉落=" + string.Join(",", item.Droplist));
```

服务端 / 任何不依赖 Unity 的地方：

```csharp
var db = ConfigFileLoader.Load("Assets/ConfigDataServer");
var item = db.Item.Get(1001);
```

## 目录

| 路径 | 说明 |
| --- | --- |
| `Assets/Config/Excel/` | Excel 源表，文件名 = 表名 |
| `Assets/Config/Json/` | 导出时产生的文本快照，只进 git，不进包 |
| `Assets/ConfigData/` | 客户端 `.bytes`，由 YooAsset 收集器的 Config 组收集 |
| `Assets/ConfigDataServer/` | 服务端 `.bytes`，不参与打资源包，部署时直接拷到服务器 |
| `Assets/Scripts/Framework/Config/Runtime/` | 运行时共享代码（纯 C#、不依赖 Unity，服务端也编译它） |
| `Assets/Scripts/Framework/Config/Generated/` | 导出工具生成的代码，不要手改 |
| `Assets/Scripts/Framework/Config/Editor/` | 导出工具：xlsx 读取 / 校验 / 生成（零第三方依赖） |
| `Assets/Scripts/Framework/Config/ConfigManager.cs` | 客户端加载器（经 IResourceService 读资源） |

## 表头规范

A 列是标记列，字段从 B 列开始（和 Luban 一致，以后想换 Luban 不用改表）。

| 行 | A | B | C | D | E | F | G | H | I | J | K |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | `##var` | `Id` | `Name` | `Quality` | `Price` | `Droplist` | `Tags` | `IconPath` | | | |
| 2 | `##type` | `int` | `string` | `enum:ItemQuality` | `float` | `int[]` | `string[]` | `string` | | | |
| 3 | `##group` | `B` | `B` | `B` | `B` | `B` | `B` | `C` | | | |
| 4 | `##` | 道具ID | 名字 | 品质 | 价格 | 掉落列表 | 标签 | 图标 | | | |
| 5 | | 1 | 小刀 | Common | 10.5 | `1001&1002` | `新手&近战` | icon_knife | | | |

规则：

- A 列以 `##` 开头的行是元数据行，按标记解析；其它 `##xxx` 行忽略（留给以后扩展）
- 第一个不是 `##` 开头的行开始才是数据
- **第一个字段是主键**，类型只能是 `int` / `long` / `string`，且必须是双端字段（`##group=B`）
- 字段名必须是合法的 C# 标识符（生成同名的 public 字段）
- 整行全空的行会被跳过，方便你用空行分组
- 一个 Excel = 一张表 = 文件名就是表名；只读第一个 sheet

支持的类型：`int`、`long`、`float`、`bool`、`string`、`enum:枚举名`。
`bool` 可以写 `true/false/是/否/1/0/y/n`。

### 数组

类型末尾加 `[]` 就是数组：`int[]`、`long[]`、`float[]`、`bool[]`、`string[]`、`enum:ItemQuality[]`。
单元格里用 `&` 分隔元素（和 Luban 一致）：

```
Droplist（int[]）   2001&2002&2003
Tags（string[]）    剑&装备
```

- 空单元格 = **空数组**（长度 0），不是 null，用之前不用判空
- 元素多写一个 `&`（比如 `1&&2`）会报错，错误信息会指出是"第几项"
- 数组字段不能当主键

### 分端字段 `##group`

`##group` 行决定这一列发给哪一端，留空按双端处理：

| 值 | 含义 |
| --- | --- |
| `B`（或 `Both`） | 双端都有（默认） |
| `C`（或 `Client`） | 只有客户端有（比如图标路径、特效名、UI 文案） |
| `S`（或 `Server`） | 只有服务端有（比如后台权重、掉落概率、反作弊参数） |

分端之后：

- 客户端编译出的行类里**没有** `S` 字段，服务端编译出的行类里**没有** `C` 字段（写错直接编译报错，不会悄悄读到脏数据）
- 客户端数据文件里不写 `S` 字段，服务端数据文件里不写 `C` 字段，各自身上的结构哈希也不一样

## 枚举表 `__enums__.xlsx`

`Assets/Config/Excel/__enums__.xlsx` 里**一个 sheet = 一个枚举**，sheet 名就是枚举名：

| 行 | A | B | C |
| --- | --- | --- | --- |
| 1 | `##enum` | Value | Comment |
| 2 | Common | 1 | 普通 |
| 3 | Fine | 2 | 精良 |
| 4 | Rare | 3 | 稀有 |
| 5 | Epic | 4 | 史诗 |

导出时生成 `public enum ItemQuality { Common = 1, ... }`；表里写 `enum:ItemQuality` 就有类型安全，
写错成员名导出会直接报错。

## 导出

菜单 `Tools/配置表/导出全部表`。导出失败会弹框，并给出「文件 / sheet / 第几行 / 哪一列 / 第几项」的原因。

校验项（任意一条不过就**一个文件都不写**，避免写出半成品）：

- 缺 `##var` / `##type`，字段名重复或不是合法标识符
- 类型不认识，`##group` 写了 B/C/S 之外的值
- 值转换失败（比如 int 列填了中文、数组里某一项不是整数）
- 主键为空 / 重复，主键是数组，或主键不是 int / long / string
- 枚举值不在 `__enums__.xlsx` 里

导出还会清理不再需要的旧产物（被删掉的表对应的 `.bytes` 和 `.g.cs`，客户端和服务端两个目录都会清）。

写盘之前还会把自己刚生成的二进制**读回来验一遍**（字段个数 / 顺序 / 数组长度对不上就报错），
所以生成器本身出问题会在导出阶段就暴露，而不是等运行时炸。

### 不开 Unity 导出（命令行）

`Tools\ConfigTool\ConfigTool.exe` 是同一份导出代码的独立 exe（直接 link `Editor/` 下的源码，
不存在两套实现）：

```
ConfigTool.exe                     自动往上找工程根（含 Assets\Config\Excel 的那一层）
ConfigTool.exe D:\path\to\project  显式指定工程根
ConfigTool.exe --check             只校验 + 报数据大小，一个文件都不写
ConfigTool.exe --no-pause          跑完不等按键（批处理 / CI 用）
```

双击运行就行，退出码 `0` 成功 / `1` 有错 / `2` 参数或工程根不对。
`--check` 建议在发版前先跑一遍。重新发布的方法见 `Tools\ConfigTool\README.md`。

## 数据格式

```
int32 magic         0x47464331
int32 formatVersion 当前 2
int32 schemaHash    该侧字段名 + 类型的 FNV-1a 哈希（客户端 / 服务端各一份）
int32 rowCount
rowCount 行数据，按该侧字段顺序

标量：int -> int32，long -> int64，float -> float32，bool -> byte(0/1)，
      string -> 7bit 长度 + UTF8，enum -> int32
数组：int32 元素个数，然后按上面的方式依次写元素
```

`ConfigReader` 读之前会校验这三项，不匹配直接抛 `ConfigFormatException` —— 专门防
「表改了但没重新导出 / 没重新编译」以及「拿错端的文件」。数组长度还会校验上限
（不能超过剩余字节数），防止读坏文件时把内存撑爆。

**改字段结构（增删列 / 改类型 / 改 `##group`）要重新导出并重新编译；只改数值不用编译。**

## 字符集（字体子集化）

导出时会顺带扫所有表里**客户端可见的 string 字段**（双端 + 客户端字段，含数组元素），
加上固定 ASCII 段（`0x20~0x7E`）和常用全角标点，生成 `Assets/Config/Json/used_chars.txt`
（进 git、不打进包）。

用途：中文字体全字库 10-20MB，子集化后压到几百 KB。用 fontTools 裁剪：

```bash
pyftsubset NotoSansSC-Regular.ttf --text-file=Assets/Config/Json/used_chars.txt \
    --output-file=NotoSansSC-subset.ttf
```

注意：

- **宁多勿少**：字符集覆盖不足 = 界面某个字渲染成方块。所以固定段 + 全表客户端 string 都扫了；
- **ASCII 段固定保留**：英文 / 数字 / 标点 / 聊天混合文本都要用，别在子集化时裁掉；
- **只覆盖配置表文本**：玩家输入（聊天 / 改名）、服务端动态下发的公告等不在表里的字装不下，
  需要时用动态 OS 字体兜底，或只给那些场景用全字库；
- 导出工具的字符集扫描代码在 `Editor/ConfigCharSet.cs`；**改了 `Editor/` 下的源码，
  ConfigTool.exe 要重新 publish 才生效**（Unity 菜单路径自动编译，不用管）。

## 热更

配置表和别的资源走同一条通道：

1. `Tools/配置表/导出全部表`（顺带产出 `Assets/ConfigDataServer/`）
2. `Tools/YooAsset/2. 构建资源（Android + 拷进首包）`
3. `Tools/YooAsset/6. 同步 Android 产物到本地服务器目录`
4. 上传服务器目录

服务端的 `Assets/ConfigDataServer/` 不进资源包，改完表把它一起拷到服务器即可（和热更清单同一次发布）。

## 前后端共用

`ServerHost/GameServerHost.csproj` 链接了这两份源码：

```xml
<Compile Include="..\Assets/Scripts/Framework/Config\Runtime\**\*.cs" />
<Compile Include="..\Assets/Scripts/Framework/Config\Generated\**\*.cs" />
```

生成代码里分端字段用编译宏隔开：

```csharp
#if UNITY_5_3_OR_NEWER          // Unity：只留 双端 + 客户端 字段
public string IconPath;
#endif
#if !UNITY_5_3_OR_NEWER         // ServerHost：只留 双端 + 服务端 字段
public int ServerOnly;
#endif
```

`SchemaHash` 同理（两端各一个常量），所以一端的数据文件被另一端的代码读会直接报错。

服务端自检（不用开 Unity，默认读 `Assets/ConfigDataServer`，没有就退回 `Assets/ConfigData`）：

```bash
dotnet run --project ServerHost\GameServerHost.csproj -- --cfgtest
dotnet run --project ServerHost\GameServerHost.csproj -- --cfgtest --config-dir "D:\path\to\dir"
```

自检覆盖：主键索引、枚举、浮点、bool、数组（含空数组）、分端字段、结构哈希防篡改、
以及"用服务端结构读客户端数据会被拦下"。

## 新增一张表

1. `Assets/Config/Excel/` 下新建 `Xxx.xlsx`，按上面的表头规范填
2. `Tools/配置表/导出全部表`
3. 代码里 `GameController.Instance.Config.Database.Xxx.Get(id)`

## 目前没做（按需再加）

- 嵌套结构 / 子表（现在是扁平表 + 数组）
- 改完 Excel 自动导出 —— **故意不做**。导出只有两个手动入口：Unity 菜单
  `Tools/配置表/导出全部表`，或者不开 Unity 的 `Tools\ConfigTool\ConfigTool.exe`。
  原因：`AssetPostprocessor.OnPostprocessAllAssets` 是"导入事件"级而不是"内容变更"级，
  打开工程、切分支、右键 Reimport、删掉 Library 后重建都会触发，它分不清你有没有真的改表。
  一旦挂上去，就会在没改表的时候重写 `Generated/*.g.cs` 和 `ConfigData/*.bytes`，
  进而把资源包版本弄脏（清单 hash 变了就得重新构建 + 重新上传热更），还会有 Excel 被占用读失败的风险。
  真要做自动化的话，必须**按输入指纹去重**（把所有 xlsx + 枚举表的内容哈希记下来，
  指纹没变就直接跳过、不写任何文件），而不是按导入事件触发。
- 非主键字段的二级索引
- 导出时顺带产 CSV 快照（现在是 JSON 快照）