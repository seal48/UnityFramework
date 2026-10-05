using System;
using UnityEngine;

namespace GameFramework.Resource
{
    /// <summary>
    /// 资源运行模式。业务代码只认这三个，底层换成 YooAsset / Addressables 都不影响业务。
    /// </summary>
    public enum ResourcePlayMode
    {
        /// <summary>编辑器模拟：直接读 AssetDatabase，不走网络，改完资源立刻生效（只能编辑器内用）。</summary>
        EditorSimulate = 0,

        /// <summary>离线：只用安装包里内置的资源，不联网。</summary>
        Offline = 1,

        /// <summary>联机：内置资源 + 远端热更，正式上线用这个。</summary>
        Host = 2,
    }

    /// <summary>
    /// 资源系统初始化参数。由 GameController 填好后传给 IResourceService.Init。
    /// </summary>
    [Serializable]
    public sealed class ResourceInitOptions
    {
        [Tooltip("运行模式")]
        public ResourcePlayMode PlayMode = ResourcePlayMode.EditorSimulate;

        [Tooltip("资源包名，必须和 YooAsset 资源收集器里配置的包名一致")]
        public string PackageName = "DefaultPackage";

        [Tooltip("主资源站地址。本地开发时后端进程（start-server.cmd）内置了资源站，默认 http://127.0.0.1:8000/Android/DefaultPackage")]
        public string HostServerURL = string.Empty;

        [Tooltip("备用资源站地址，可留空")]
        public string FallbackHostServerURL = string.Empty;

        [Tooltip("同时下载的最大并发数")]
        public int DownloadingMaxNumber = 10;

        [Tooltip("单个文件下载失败后的重试次数")]
        public int FailedTryAgain = 3;

        [Tooltip("异步操作单帧最大耗时（毫秒），越小越不卡帧")]
        public long MaxTimeSliceMs = 30;

        [Tooltip("引用计数归零时自动卸载 Bundle")]
        public bool AutoUnloadBundleWhenUnused = false;

        [Tooltip("Host 模式连不上远端资源站时自动降级为离线模式（只用首包内置资源），避免开发期忘开服务器就进不去游戏")]
        public bool FallbackToOfflineOnHostFailure = true;

        [Tooltip("Bundle 完整性校验级别：Low=1 只查大小，Middle=2 大小+存在性，High=3 CRC 校验（防改包，推荐）。默认 High")]
        public int FileVerifyLevel = 3;   // 3 = EFileVerifyLevel.High
    }

    /// <summary>热更下载进度快照。</summary>
    public struct ResourceDownloadReport
    {
        /// <summary>本次需要下载的文件总数。</summary>
        public int TotalCount;

        /// <summary>本次需要下载的总字节数。</summary>
        public long TotalBytes;

        /// <summary>已经下载的文件数。</summary>
        public int CurrentCount;

        /// <summary>已经下载的字节数。</summary>
        public long CurrentBytes;

        /// <summary>0~1 的下载进度。</summary>
        public float Progress
        {
            get { return TotalBytes <= 0 ? 1f : (float)CurrentBytes / (float)TotalBytes; }
        }

        /// <summary>是否已经下完。</summary>
        public bool IsDone
        {
            get { return TotalCount <= 0 || CurrentCount >= TotalCount; }
        }
    }
}
