# Boot — HybridCLR 代码热更引导

让**业务层代码可以热更新**（改 Bug / 加功能不用重新发版，只更新 DLL）。

## 核心思路

```
框架层 Framework/     = AOT（编进包里，稳定，几乎不改）
业务层 Game/ 等        = 热更（编成 DLL，从外部加载，随时换）
```

- **热更程序集**（HybridCLR 配置 `hotUpdateAssemblies`）：
  `Game`、`GameFramework.Business`、`GameFramework.ServerSelect`
- **AOT 引导**：`HybridCLRBoot`（场景挂载的 MonoBehaviour）+ `GameOptions`（启动配置资产）

## 启动链

```
场景加载
  └─ HybridCLRBoot.Awake (AOT)
       ├─ 真机 IL2CPP：RuntimeApi.LoadMetadataForAOTAssembly(global-metadata.dat)   ← AOT 元数据补充
       │              → Assembly.Load 三个热更 DLL（StreamingAssets/HotUpdate/）
       ├─ 编辑器：跳过加载（热更程序集已编译进编辑器，直接可用）
       ├─ 反射找 Game.HotEntry.Boot()
       └─ HotEntry.Boot() → GameController.CreateRoot()   ← 运行时创建（热更代码不能挂场景）
            → 既有启动流程（ProcedureLaunch → ... → ProcedureLogin）
```

**为什么 GameController 不在场景里**：场景 / 预制体只能序列化 AOT 脚本。
热更的 `GameController` 由 Boot 运行时 `AddComponent` 创建（`CreateRoot()`），
它的启动选项统一放在 AOT 的 `GameOptions` 资产（`Assets/Resources/GameOptions.asset`）。

## 文件

| 文件 | 作用 |
|---|---|
| `HybridCLRBoot.cs` | AOT 场景入口：加载元数据 + 热更 DLL + 反射进 HotEntry |
| `GameOptions.cs` | AOT ScriptableObject：GameController 的启动配置（原场景序列化值迁移到这里） |
| `Assets/Resources/GameOptions.asset` | 启动配置资产（菜单 GameFramework/创建启动配置 生成） |
| `Assets/StreamingAssets/HotUpdate/*.dll` | 真机加载的热更 DLL |
| `Assets/HybridCLRGenerate/` | AOT 元数据生成物（`HybridCLR/Generate/All` 生成，别手动改） |
| `Packages/com.code-philosophy.hybridclr` | HybridCLR 包（内嵌，来自 gitee） |

## 改业务代码后的出包流程

```
1) 菜单 HybridCLR/CompileDll/ActiveBuildTarget    编译热更 DLL
   → HybridCLRData/HotUpdateDlls/<平台>/ 下会生成全部程序集
2) 拷贝其中 3 个到设备目录：
   Game.dll / GameFramework.Business.dll / GameFramework.ServerSelect.dll
   → Assets/StreamingAssets/HotUpdate/
3) 重建资源包（菜单 Tools/YooAsset/2 + 6 + 清 yoo/ 缓存）—— 把新 DLL 打进首包
4) Tools/环境/构建/<环境> 出包
```

> 编辑器里改热更代码**不用**重编 DLL —— 编辑器直接用编译进编辑器的程序集，
> 只有真机（IL2CPP）才从 DLL 加载。所以日常联调零负担，只有出热更包时才走上面流程。

## 改 AOT 程序集（框架层）之后

AOT 程序集变了，热更代码可能引用到它的新泛型 / 反射信息，需要重新生成补充元数据：

```
菜单 HybridCLR/Generate/All
```

会重新生成 `Assets/HybridCLRGenerate/`（AOTGenericReferences.cs / link.xml 等）。别手动改。

## 首次接入（本仓库已完成）

1. 从 gitee 装包：`com.code-philosophy.hybridclr`（8.15.0，匹配 Unity 2021.3.26）
2. 菜单 `HybridCLR/Installer/Install`：克隆 hybridclr + il2cpp_plus，替换 Unity 编辑器 il2cpp（**改 Unity 安装**，装前备份过）
3. 配置 `HybridCLRSettings`：hotUpdateAssemblies 三个热更程序集 + patchAOTAssemblies 全部 AOT
4. `HybridCLR/Generate/All` + `HybridCLR/CompileDll/ActiveBuildTarget`
5. 场景：GameController 组件 → HybridCLRBoot 组件；选项迁移到 GameOptions 资产
6. 编辑器验证：Play 走通 Boot → HotEntry → 完整启动

## 注意

- **Unity 必须 IL2CPP**：HybridCLR 只在 IL2CPP 构建下有意义（Mono 不需要热更）。
  Android 出包记得切 IL2CPP + ARM64。
- **AOT 不能静态引用热更程序集**：框架层别 `using` 业务层的类型，入口走反射。
- 热更 DLL 目前放 StreamingAssets（首包携带）。后续可改成 YooAsset 远端下载，
  那才是真正的"线上热更" —— 需要时在 `HybridCLRBoot` 的加载处接 YooAsset。
