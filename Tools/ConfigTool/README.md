# ConfigTool —— 独立的配置表导出器

不打开 Unity 就能导出 / 校验配置表。产出和 Unity 菜单 `Tools/配置表/导出全部表` **完全一样**
（不是复制一份实现，而是直接 link `Assets/Scripts/Framework/Config/Editor` 里的同一份代码）。

## 用法

```
ConfigTool.exe                     自动往上找工程根（含 Assets\Config\Excel 的那一层）
ConfigTool.exe D:\path\to\project  显式指定工程根
ConfigTool.exe --check             只校验 + 报数据大小，一个文件都不写
ConfigTool.exe --no-pause          跑完不等按键（批处理 / CI 用）
```

- 双击运行：自动找工程根，跑完停在「按回车键退出」
- 退出码：`0` 成功，`1` 导出/校验有错，`2` 参数或工程根不对
- **有错就一个文件都不写**，`--check` 更是全程只读，适合发版前先跑一遍当保险

## 发布

```
cd Tools\ConfigTool
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

产出 `publish\ConfigTool.exe`（单文件，约 190 KB），需要目标机器装了 .NET 8 运行时
（开发机装了 SDK 就有）。

要发给不装 .NET 的策划用，就改成自包含（体积约 15~70 MB，需要能联网下 runtime pack）：

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -o publish-standalone
```

## 注意

- 这个 exe **不会**自动跑，也没有任何 AssetPostprocessor 钩子。改完表以后由人手动执行一次，
  和 Unity 菜单一样，是发版前的一道闸门。
- 源码改动要重新 publish 才会生效（`Assets/Scripts/Framework/Config/Editor` 下的文件是 link 进来的）。