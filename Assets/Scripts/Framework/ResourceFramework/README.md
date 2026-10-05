# 资源框架（YooAsset）

一层很薄的资源加载抽象：业务代码只依赖 `IResourceService`，底层用 YooAsset 2.3.x 实现。
换实现（Addressables / 自研 AB）时业务代码不用动。

## 1. 目录结构

```
Assets/Scripts/Framework/ResourceFramework/
├── ResourceTypes.cs            运行模式枚举 / 初始化参数 / 下载进度快照
├── IResourceService.cs         ★ 业务唯一依赖的接口
├── ResourceHandles.cs          ResourceAsset<T> / ResourceInstance（引用计数句柄）
├── YooAssetResourceService.cs  ★ YooAsset 实现
├── HttpRemoteServices.cs       远端地址拼接
└── ResourceDownloadWatcher.cs  热更进度上报（内部用，自动销毁）
```

YooAsset 本体是嵌入式本地包，位于 `Packages/com.tuyoogame.yooasset`（2.3.19），
不是从网络装的，升级时直接替换该目录即可。

## 2. 三个运行模式

| 模式 | 用途 | 走网络 | 说明 |
|---|---|---|---|
| `EditorSimulate` | 编辑器内日常开发 | 否 | 直接读 AssetDatabase，改完资源立刻生效，**不用构建** |
| `Offline` | 单机版 / 首包自测 | 否 | 只用安装包内置资源 |
| `Host` | 正式上线 | 是 | 内置资源 + 远端热更，自动完成版本检查与差异下载 |

模式只影响初始化参数，业务代码完全一样。

## 3. 怎么用

### 初始化（由 GameController 统一负责）

```csharp
// GameController.Awake
resource = new YooAssetResourceService();

// GameController.Start
resource.Init(resourceOptions, (success, message) => {
    // Host 模式下，下载全部完成才会回调
});
```

`resourceOptions` 在 Inspector 的 "资源框架" 分组里配。Host 模式要填 `HostServerURL`。

### 加载资源

```csharp
var res = GameController.Instance.Resource;

// 1) 加载普通资源（Sprite / AudioClip / ScriptableObject ...）
res.LoadAssetAsync<Sprite>("UI/Login/bg", asset => {
    if (asset == null) return;          // 失败已经在控制台打出来了
    image.sprite = asset.Asset;
    // 用完释放：asset.Dispose();
});

// 2) 加载 Prefab 并实例化
res.InstantiateAsync("UI/Login/LoginPanel", uiRoot, instance => {
    if (instance == null) return;
    instance.GameObject.transform.localScale = Vector3.one;
    // 关界面时：instance.Dispose();  会销毁 GameObject 并释放引用
});
```

**释放规则**：`ResourceAsset<T>.Dispose()` / `ResourceInstance.Dispose()` 各对应一次加载。
引用计数减到 0 才会真正释放 YooAsset 的句柄。

### 热更进度

```csharp
res.DownloadProgressChanged += report => {
    // report.Progress   0~1
    // report.CurrentCount / report.TotalCount
    // report.CurrentBytes / report.TotalBytes
};
```

## 4. 热更实际做了什么

`Init` 在 `Host` 模式下自动顺序执行：

1. `RequestPackageVersionAsync` 取远端最新版本号
2. `UpdatePackageManifestAsync` 拉清单，和本地清单比对出差异
3. `CreateResourceDownloader` 只下载变化的 bundle（按 bundle 粒度增量）
4. `ClearCacheFilesAsync(ClearUnusedBundleFiles)` 清理旧文件

远端目录结构（YooAsset 约定）：

```
{HostServerURL}/{PackageName}/{PackageVersion}/{FileName}
```

**版本号在路径里**，所以每次热更上传到新目录，天然绕过 CDN 缓存。

本地联调时资源站不用额外开服务：工程根目录的 `start-server.cmd`（后端进程）已经内置了静态文件服务，
默认监听 `http://127.0.0.1:8000/`，根目录是 `ServerData`；
Unity 编辑器里 `Game Framework > Start Local Server` 也会一起把它拉起来。

## 5. 已知限制

- `EditorSimulate` 必须在编辑器里；真机上用会直接报错并返回失败（已用 `#if UNITY_EDITOR` 挡住）。
- 引用计数按 `location` 记，同一个地址当成同一种资源。同一地址要按不同类型加载（比如图集同时当
  `Texture2D` 和 `Sprite`）目前不支持，需要扩展 key。
- `Shutdown()` 只释放自己的句柄，不销毁 YooAssets 全局实例（那是整个进程级别的东西）。

热更进度界面已经接上了（这里原先记着一条待办，其实早做完了）：`ProcedureInitResource` 订阅
`DownloadProgressChanged`，转调 `LoadingPanel.SetHotUpdateProgress`，见 `Assets/Scripts/Game/Startup/README.md`。
本层只负责抛进度事件，进度条长什么样是上层的事。
