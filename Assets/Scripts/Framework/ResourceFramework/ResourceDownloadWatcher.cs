using GameFramework.Log;
using System;
using UnityEngine;
using YooAsset;

namespace GameFramework.Resource
{
    /// <summary>
    /// 热更下载进度上报器。
    /// YooAsset 的下载推进由它自己的驱动器负责，这里只负责每帧把进度快照转给业务，不参与下载逻辑。
    /// 下载结束（成功或失败）后自动销毁。
    /// </summary>
    internal sealed class ResourceDownloadWatcher : MonoBehaviour
    {
        private DownloaderOperation _operation;
        private Action<ResourceDownloadReport> _callback;

        public static void Begin(DownloaderOperation operation, Action<ResourceDownloadReport> callback)
        {
            if (operation == null)
                return;

            GameObject go = new GameObject("[ResourceDownloadWatcher]");
            DontDestroyOnLoad(go);

            ResourceDownloadWatcher watcher = go.AddComponent<ResourceDownloadWatcher>();
            watcher._operation = operation;
            watcher._callback = callback;
        }

        private void Update()
        {
            if (_operation == null)
            {
                Destroy(gameObject);
                return;
            }

            Action<ResourceDownloadReport> callback = _callback;
            if (callback != null)
            {
                ResourceDownloadReport report = new ResourceDownloadReport
                {
                    TotalCount = _operation.TotalDownloadCount,
                    TotalBytes = _operation.TotalDownloadBytes,
                    CurrentCount = _operation.CurrentDownloadCount,
                    CurrentBytes = _operation.CurrentDownloadBytes,
                };

                try
                {
                    callback(report);
                }
                catch (Exception ex)
                {
                    GameLog.Error(LogTag.Resource, ex.Message, ex);
                }
            }

            if (_operation.IsDone)
            {
                _operation = null;
                _callback = null;
                Destroy(gameObject);
            }
        }
    }
}
