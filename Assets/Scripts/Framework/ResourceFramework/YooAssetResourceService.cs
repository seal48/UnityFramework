using GameFramework.Core;
using GameFramework.Log;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using YooAsset;

namespace GameFramework.Resource
{
    /// <summary>
    /// IResourceService 的 YooAsset 实现（对应 YooAsset 2.3.x）。
    ///
    /// 设计要点：
    /// - 句柄按 location 做引用计数：同一个地址重复加载只真正加载一次，ReleaseAsset 递减，减到 0 才释放。
    /// - 解决「并发加载同一个地址」：第二次请求会挂到等待队列里，复用第一次的结果。
    /// - 热更流程全部在内部完成，业务只等一个 onComplete。
    /// </summary>
    public sealed class YooAssetResourceService : IResourceService, IGameModule
    {
        private sealed class AssetEntry
        {
            public AssetHandle Handle;
            public int RefCount;
        }

        private readonly Dictionary<string, AssetEntry> _entries =
            new Dictionary<string, AssetEntry>(StringComparer.Ordinal);

        private readonly Dictionary<string, List<Action<AssetHandle>>> _pending =
            new Dictionary<string, List<Action<AssetHandle>>>(StringComparer.Ordinal);

        private ResourcePackage _package;
        private ResourceInitOptions _options;
        private bool _initialized;
        private bool _initializing;
        private bool _builtinReadyNotified;

        public bool IsInitialized { get { return _initialized; } }

        public string PackageVersion { get; private set; }

        public ResourcePlayMode PlayMode
        {
            get { return _options != null ? _options.PlayMode : ResourcePlayMode.Offline; }
        }

        public event Action<ResourceDownloadReport> DownloadProgressChanged;

        // ============================== 初始化 ==============================

        public event Action BuiltinReady;

        public void Init(ResourceInitOptions options, Action<bool, string> onComplete)
        {
            if (_initialized)
            {
                Notify(onComplete, true, "资源系统已经初始化过了");
                return;
            }
            if (_initializing)
            {
                Notify(onComplete, false, "资源系统正在初始化中，不要重复调用 Init");
                return;
            }

            _options = options != null ? options : new ResourceInitOptions();
            _initializing = true;


            if (YooAssets.Initialized == false)
                YooAssets.Initialize();


            YooAssets.SetOperationSystemMaxTimeSlice(_options.MaxTimeSliceMs);


            InitializePackage(onComplete);
        }

        /// <summary>通知「内置资源就绪」（清单已激活，首包资源可加载），只通知一次（降级重来时不会重复触发）。</summary>
        private void NotifyBuiltinReady()
        {
            if (_builtinReadyNotified) return;
            _builtinReadyNotified = true;

            var handler = BuiltinReady;
            if (handler != null) handler();
        }

        /// <summary>Host 模式下远端不可用时，降级成离线模式重来一次。返回 true 表示已经接管回调。</summary>
        private bool TryFallbackToOffline(Action<bool, string> onComplete, string reason)
        {
            if (_options.PlayMode != ResourcePlayMode.Host || !_options.FallbackToOfflineOnHostFailure)
                return false;

            LogWarning("远端资源站不可用，已降级为离线模式（只用首包内置资源）。原因：" + reason);

            // Host 模式的包已经初始化过了，必须先销毁，才能换成离线模式重建
            ResourcePackage hostPackage = _package;
            _package = null;
            _options.PlayMode = ResourcePlayMode.Offline;
            _initialized = false;
            _initializing = true;

            DestroyOperation destroyOperation = hostPackage.DestroyAsync();
            destroyOperation.Completed += op =>
            {
                if (destroyOperation.Status != EOperationStatus.Succeed)
                {
                    _initializing = false;
                    Notify(onComplete, false, "降级为离线模式失败：" + destroyOperation.Error);
                    return;
                }

                YooAssets.RemovePackage(hostPackage);
                InitializePackage(onComplete);
            };
            return true;
        }

        /// <summary>按当前 PlayMode 初始化资源包；同一个包允许重新初始化（降级时用）。</summary>
        private void InitializePackage(Action<bool, string> onComplete)
        {
            _package = YooAssets.TryGetPackage(_options.PackageName);
            if (_package == null)
                _package = YooAssets.CreatePackage(_options.PackageName);

            InitializeParameters parameters = BuildInitializeParameters();
            if (parameters == null)
            {
                _initializing = false;
                Notify(onComplete, false, "创建初始化参数失败，请检查 PlayMode 与项目配置");
                return;
            }

            parameters.AutoUnloadBundleWhenUnused = _options.AutoUnloadBundleWhenUnused;

            InitializationOperation operation = _package.InitializeAsync(parameters);
            operation.Completed += op1 =>
            {
                if (operation.Status != EOperationStatus.Succeed)
                {
                    _initializing = false;
                    Notify(onComplete, false, "资源包初始化失败：" + operation.Error);
                    return;
                }

                _initializing = false;
                _initialized = true;

                StartUpdateFlow(onComplete);
            };
        }

        private InitializeParameters BuildInitializeParameters()
        {
            switch (_options.PlayMode)
            {
                case ResourcePlayMode.EditorSimulate:
                {
#if UNITY_EDITOR
                    PackageInvokeBuildResult buildResult = EditorSimulateModeHelper.SimulateBuild(_options.PackageName);
                    if (buildResult == null)
                        return null;

                    FileSystemParameters editorFileSystem =
                        FileSystemParameters.CreateDefaultEditorFileSystemParameters(buildResult.PackageRootDirectory);
                    return new EditorSimulateModeParameters { EditorFileSystemParameters = editorFileSystem };
#else
                    LogError("编辑器模拟模式只能在编辑器里使用，真机上请改用 Offline 或 Host");
                    return null;
#endif
                }

                case ResourcePlayMode.Offline:
                {
                    FileSystemParameters buildinFileSystem =
                        FileSystemParameters.CreateDefaultBuildinFileSystemParameters();
                    buildinFileSystem.AddParameter(FileSystemParametersDefine.FILE_VERIFY_LEVEL, (EFileVerifyLevel)_options.FileVerifyLevel);
                    return new OfflinePlayModeParameters { BuildinFileSystemParameters = buildinFileSystem };
                }

                case ResourcePlayMode.Host:
                {
                    IRemoteServices remoteServices =
                        new HttpRemoteServices(_options.HostServerURL, _options.FallbackHostServerURL);

                    FileSystemParameters buildinFileSystem =
                        FileSystemParameters.CreateDefaultBuildinFileSystemParameters();
                    buildinFileSystem.AddParameter(FileSystemParametersDefine.FILE_VERIFY_LEVEL, (EFileVerifyLevel)_options.FileVerifyLevel);

                    FileSystemParameters cacheFileSystem =
                        FileSystemParameters.CreateDefaultCacheFileSystemParameters(remoteServices);
                    cacheFileSystem.AddParameter(FileSystemParametersDefine.FILE_VERIFY_LEVEL, (EFileVerifyLevel)_options.FileVerifyLevel);

                    return new HostPlayModeParameters
                    {
                        BuildinFileSystemParameters = buildinFileSystem,
                        CacheFileSystemParameters = cacheFileSystem,
                    };
                }

                default:
                    return null;
            }
        }

        // ============================== 热更 ==============================

        /// <summary>请求版本号 → 更新清单 → （只有 Host 模式才下载差异）→ 完成。</summary>
        private void StartUpdateFlow(Action<bool, string> onComplete)
        {
            RequestPackageVersionOperation versionOperation = _package.RequestPackageVersionAsync();
            versionOperation.Completed += op1 =>
            {
                if (versionOperation.Status != EOperationStatus.Succeed)
                {
                    if (TryFallbackToOffline(onComplete, "请求远端版本号失败：" + versionOperation.Error))
                        return;

                    Notify(onComplete, false, "请求远端版本号失败：" + versionOperation.Error);
                    return;
                }

                PackageVersion = versionOperation.PackageVersion;

                UpdatePackageManifestOperation manifestOperation =
                    _package.UpdatePackageManifestAsync(PackageVersion);
                manifestOperation.Completed += op2 =>
                {
                    if (manifestOperation.Status != EOperationStatus.Succeed)
                    {
                        if (TryFallbackToOffline(onComplete, "更新资源清单失败：" + manifestOperation.Error))
                            return;

                        Notify(onComplete, false, "更新资源清单失败：" + manifestOperation.Error);
                        return;
                    }

                    // 清单激活之后，「随包内置」的资源才真正可加载（CheckLocationValid 依赖激活的清单）。
                    // 在这里通知一次：启动流程可以先把加载界面显示出来，热更下载在它后面才开始。
                    NotifyBuiltinReady();

                    // 离线 / 编辑器模拟模式：清单和资源都在本地，没有下载环节
                    if (_options.PlayMode != ResourcePlayMode.Host)
                    {
                        Notify(onComplete, true, "资源系统就绪（" + _options.PlayMode + "），版本 " + PackageVersion);
                        return;
                    }

                    ResourceDownloaderOperation downloader =
                        _package.CreateResourceDownloader(_options.DownloadingMaxNumber, _options.FailedTryAgain);

                    int needCount = downloader.TotalDownloadCount;
                    long needBytes = downloader.TotalDownloadBytes;

                    if (needCount == 0)
                    {
                        _package.ClearCacheFilesAsync(EFileClearMode.ClearUnusedBundleFiles);
                        Notify(onComplete, true, "已是最新版本（" + PackageVersion + "），无需下载");
                        return;
                    }

                    ResourceDownloadWatcher.Begin(downloader, DownloadProgressChanged);

                    downloader.Completed += op3 =>
                    {
                        if (downloader.Status != EOperationStatus.Succeed)
                        {
                            if (TryFallbackToOffline(onComplete, "资源下载失败：" + downloader.Error))
                                return;

                            Notify(onComplete, false, "资源下载失败：" + downloader.Error);
                            return;
                        }

                        _package.ClearCacheFilesAsync(EFileClearMode.ClearUnusedBundleFiles);
                        Notify(onComplete, true, string.Format(
                            "热更完成：更新 {0} 个文件 / {1}，版本 {2}",
                            needCount, FormatBytes(needBytes), PackageVersion));
                    };

                    downloader.BeginDownload();
                };
            };
        }

        // ============================== 加载 ==============================

        public void LoadAssetAsync<T>(string location, Action<ResourceAsset<T>> onLoaded) where T : UnityEngine.Object
        {
            if (!EnsureReady(location))
            {
                if (onLoaded != null)
                    onLoaded(null);
                return;
            }

            AcquireHandle(location, typeof(T), handle =>
            {
                ResourceAsset<T> result = null;

                if (handle != null)
                {
                    try
                    {
                        T asset = handle.GetAssetObject<T>();
                        if (asset != null)
                            result = new ResourceAsset<T>(asset, location, this);
                        else
                            LogError("地址 '" + location + "' 上没有 " + typeof(T).Name + " 类型的资源");
                    }
                    catch (Exception ex)
                    {
                        LogError("资源类型不匹配：" + location + " -> " + ex.Message);
                    }

                    if (result == null)
                        ReleaseAsset(location);
                }

                if (onLoaded != null)
                    onLoaded(result);
            });
        }

        public ISceneLoadOperation LoadSceneAsync(string location, LoadSceneMode mode = LoadSceneMode.Single, bool suspendLoad = false)
        {
            if (!EnsureReady(location))
                return null;

            try
            {
                SceneHandle handle = _package.LoadSceneAsync(location, mode, LocalPhysicsMode.None, suspendLoad);
                return handle != null ? new YooSceneLoadOperation(location, handle) : null;
            }
            catch (Exception ex)
            {
                LogError("加载场景失败：" + location + " -> " + ex.Message);
                return null;
            }
        }

        public void InstantiateAsync(string location, Transform parent, Action<ResourceInstance> onCreated)
        {
            if (!EnsureReady(location))
            {
                if (onCreated != null)
                    onCreated(null);
                return;
            }

            AcquireHandle(location, typeof(GameObject), handle =>
            {
                if (handle == null)
                {
                    if (onCreated != null)
                        onCreated(null);
                    return;
                }

                InstantiateOperation operation = handle.InstantiateAsync(parent, true);
                operation.Completed += op1 =>
                {
                    if (operation.Status != EOperationStatus.Succeed)
                    {
                        LogError("实例化失败：" + location + " -> " + operation.Error);
                        ReleaseAsset(location);
                        if (onCreated != null)
                            onCreated(null);
                        return;
                    }

                    if (onCreated != null)
                        onCreated(new ResourceInstance(operation.Result, location, this));
                };
            });
        }

        /// <summary>
        /// 取句柄并让引用计数 +1。
        /// 同一个地址并发请求时只发起一次真实加载，其余请求排队等结果。
        /// </summary>
        private void AcquireHandle(string location, Type assetType, Action<AssetHandle> onReady)
        {
            AssetEntry entry;
            if (_entries.TryGetValue(location, out entry))
            {
                entry.RefCount++;
                if (onReady != null)
                    onReady(entry.Handle);
                return;
            }

            List<Action<AssetHandle>> waiters;
            if (_pending.TryGetValue(location, out waiters))
            {
                waiters.Add(onReady);
                return;
            }

            waiters = new List<Action<AssetHandle>>();
            waiters.Add(onReady);
            _pending[location] = waiters;

            AssetHandle handle = _package.LoadAssetAsync(location, assetType, 0);
            handle.Completed += h =>
            {
                List<Action<AssetHandle>> callbacks = null;
                if (_pending.TryGetValue(location, out callbacks))
                    _pending.Remove(location);

                int waiterCount = callbacks != null ? callbacks.Count : 0;

                if (h.Status != EOperationStatus.Succeed)
                {
                    LogError("资源加载失败：" + location + " -> " + h.LastError);
                    if (h.IsValid)
                        h.Release();
                    InvokeWaiters(callbacks, null);
                    return;
                }

                AssetEntry exist;
                if (_entries.TryGetValue(location, out exist))
                {
                    // 期间已经被别人登记过了：复用已有句柄，把这份多余的释放掉
                    h.Release();
                    exist.RefCount++;
                    InvokeWaiters(callbacks, exist.Handle);
                    return;
                }

                entry = new AssetEntry();
                entry.Handle = h;
                entry.RefCount = waiterCount > 0 ? waiterCount : 1;
                _entries[location] = entry;
                InvokeWaiters(callbacks, entry.Handle);
            };
        }

        private static void InvokeWaiters(List<Action<AssetHandle>> waiters, AssetHandle handle)
        {
            if (waiters == null)
                return;

            for (int i = 0; i < waiters.Count; i++)
            {
                Action<AssetHandle> callback = waiters[i];
                if (callback == null)
                    continue;

                try
                {
                    callback(handle);
                }
                catch (Exception ex)
                {
                    LogError("加载回调异常：" + ex);
                }
            }

            waiters.Clear();
        }

        // ============================== 释放 ==============================

        public void ReleaseAsset(string location)
        {
            if (string.IsNullOrEmpty(location))
                return;

            AssetEntry entry;
            if (!_entries.TryGetValue(location, out entry))
                return;

            entry.RefCount--;
            if (entry.RefCount > 0)
                return;

            _entries.Remove(location);
            if (entry.Handle != null)
                entry.Handle.Release();
        }

        public void ReleaseAll()
        {
            foreach (KeyValuePair<string, AssetEntry> pair in _entries)
            {
                AssetEntry entry = pair.Value;
                if (entry != null && entry.Handle != null)
                    entry.Handle.Release();
            }

            _entries.Clear();
            _pending.Clear();
        }

        public void Shutdown()
        {
            ReleaseAll();
            _initialized = false;
            _initializing = false;
            PackageVersion = null;
            _package = null;
        }

        // ============================== 工具 ==============================

        private bool EnsureReady(string location)
        {
            if (string.IsNullOrEmpty(location))
            {
                LogError("location 为空");
                return false;
            }

            if (!_initialized || _package == null)
            {
                LogError("资源系统还没初始化，请先通过 GameController 初始化资源框架");
                return false;
            }

            try
            {
                if (!_package.CheckLocationValid(location))
                {
                    LogError("资源地址不存在（检查 YooAsset 资源收集器与可寻址规则）：" + location);
                    return false;
                }
            }
            catch (Exception ex)
            {
                // 清单还没激活时 CheckLocationValid 会直接抛异常，这里兜住，避免打断调用方流程
                LogError("资源系统尚未准备就绪（清单可能还没激活）：" + ex.Message);
                return false;
            }

            return true;
        }

        private static void Notify(Action<bool, string> onComplete, bool success, string message)
        {
            if (onComplete == null)
                return;

            try
            {
                onComplete(success, message);
            }
            catch (Exception ex)
            {
                LogError("初始化回调异常：" + ex);
            }
        }

        private static void LogError(string message)
        {
            GameLog.Error(LogTag.Resource, message);
        }

        private static void LogWarning(string message)
        {
            GameLog.Warn(LogTag.Resource, message);
        }

        /// <summary>把字节数格式化成人看的字符串。</summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024L)
                return bytes + "B";
            if (bytes < 1024L * 1024L)
                return (bytes / 1024f).ToString("0.0") + "KB";
            return (bytes / (1024f * 1024f)).ToString("0.0") + "MB";
        }
    }
}
