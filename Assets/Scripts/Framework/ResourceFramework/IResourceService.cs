using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameFramework.Resource
{
    /// <summary>
    /// 资源服务接口。业务代码只依赖这个接口，底层实现（YooAsset 等）可以整体替换。
    /// 统一由 GameController 在启动时 Init。
    /// </summary>
    public interface IResourceService
    {
        /// <summary>是否初始化完成（Host 模式下要等热更下载完成才算完成）。</summary>
        bool IsInitialized { get; }

        /// <summary>当前资源包版本号。Host 模式下是远端最新版本。</summary>
        string PackageVersion { get; }

        /// <summary>当前运行模式。</summary>
        ResourcePlayMode PlayMode { get; }

        /// <summary>热更下载进度（只在 Host 模式的热更阶段触发）。</summary>
        event Action<ResourceDownloadReport> DownloadProgressChanged;

        /// <summary>
        /// 内置资源就绪：首包清单已经激活，此时可以加载「随包内置」的资源（比如启动加载界面），
        /// 但热更差异下载还没开始。Host 模式降级为离线并成功初始化后会补发一次。
        /// </summary>
        event Action BuiltinReady;

        /// <summary>
        /// 初始化资源系统；Host 模式下会自动完成「请求版本 → 更新清单 → 下载差异 → 清理缓存」。
        /// onComplete(是否成功, 说明文本)，全部完成后才回调。
        /// </summary>
        void Init(ResourceInitOptions options, Action<bool, string> onComplete);

        /// <summary>异步加载资源，回调在主线程。</summary>
        void LoadAssetAsync<T>(string location, Action<ResourceAsset<T>> onLoaded) where T : UnityEngine.Object;

        /// <summary>异步加载 Prefab 并实例化，回调在主线程。</summary>
        void InstantiateAsync(string location, Transform parent, Action<ResourceInstance> onCreated);

        /// <summary>
        /// 异步加载场景。返回的句柄可以每帧查进度（配加载界面用），完成后触发 Completed。
        /// 场景资源必须进 YooAsset 资源收集器；编辑器模拟模式下走 EditorSceneManager，真机走打包资源。
        /// 失败（地址不对、场景没收集）时返回 null。
        /// </summary>
        ISceneLoadOperation LoadSceneAsync(string location, LoadSceneMode mode = LoadSceneMode.Single, bool suspendLoad = false);

        /// <summary>按地址释放一次引用（LoadAssetAsync / InstantiateAsync 各记一次）。</summary>
        void ReleaseAsset(string location);

        /// <summary>释放当前所有引用。</summary>
        void ReleaseAll();

        /// <summary>关闭资源系统。</summary>
        void Shutdown();
    }
}
