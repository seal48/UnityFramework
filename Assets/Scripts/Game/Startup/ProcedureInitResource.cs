using GameFramework.Log;
using GameFramework.Procedure;
using GameFramework.Resource;
using GameFramework.UI;
using UnityEngine;

namespace GameFramework.Startup
{
    /// <summary>
    /// 资源初始化 + 加载界面。
    ///
    /// 时序（Host 模式）：
    ///   1. InitializeAsync 加载首包清单 → 请求版本 → 更新清单 → 清单激活
    ///   2. BuiltinReady：这一步开始「随包内置」的资源才能加载，于是初始化 UI 并显示 LoadingPanel
    ///   3. 下载差异资源，进度实时刷到 LoadingPanel
    ///   4. 资源全部就绪 + 加载界面显示够时间 → 关掉 LoadingPanel，进入下一步
    ///
    /// 失败时停在本流程，按 R 可以重试（方便开发期调试）。
    /// </summary>
    public sealed class ProcedureInitResource : ProcedureBase
    {
        /// <summary>加载界面最短显示时间，避免热更太快时闪一下就没了。</summary>
        private const float MinLoadingTime = 1f;

        private const string TipChecking = "正在检查更新…";
        private const string TipDone = "加载完成";

        private IResourceService resource;
        private LoadingPanel loading;

        private bool uiRequested;      // 是否已经请求过初始化 UI
        private bool uiUnavailable;    // UI 起不来（初始化失败 / 面板没打开），就不要再等加载界面
        private bool gotProgress;      // 是否收到过下载进度
        private bool failed;
        private bool ready;
        private float shownTime = -1f;

        private ResourceDownloadReport lastReport;
        private bool loggedDownloadStart;

        public override void OnEnter(ProcedureBase from)
        {
            var controller = GameController.Instance;
            if (controller == null)
            {
                failed = true;
                return;
            }

            resource = controller.Resource;
            if (resource != null)
            {
                resource.BuiltinReady += OnBuiltinReady;
                resource.DownloadProgressChanged += OnDownloadProgress;
            }

            GameLog.Info(LogTag.Startup, "开始初始化资源…");
            controller.InitResource(OnResourceReady);
        }

        public override void OnLeave(ProcedureBase to)
        {
            if (resource != null)
            {
                resource.BuiltinReady -= OnBuiltinReady;
                resource.DownloadProgressChanged -= OnDownloadProgress;
                resource = null;
            }
        }

        public override void OnUpdate(float deltaTime, float unscaledDeltaTime)
        {
            if (failed)
            {
                if (Input.GetKeyDown(KeyCode.R))
                    Retry();

                return;
            }

            if (!ready)
                return;

            // 加载界面还在加载中：先等它出来，不然会出现「流程走完了界面才冒出来」的鬼畜情况
            if (uiRequested && !uiUnavailable && loading == null)
                return;

            // 等够最短显示时间再走，免得玩家什么都看不清
            if (shownTime >= 0f && Time.unscaledTime - shownTime < MinLoadingTime)
                return;

            if (loading != null)
            {
                loading.Close();
                loading = null;
            }

            ChangeState<ProcedureInitConfig>();
        }

        private void Retry()
        {
            failed = false;
            ready = false;
            gotProgress = false;
            loggedDownloadStart = false;

            if (loading != null)
                loading.SetImmediate(0f, "正在重试…");

            GameLog.Info(LogTag.Startup, "重试初始化资源…");
            GameController.Instance.InitResource(OnResourceReady);
        }

        /// <summary>清单已激活：这时候就能加载「随包内置」的界面了，先把加载界面显示出来。</summary>
        private void OnBuiltinReady()
        {
            if (uiRequested)
                return;

            uiRequested = true;

            var controller = GameController.Instance;
            if (controller == null)
                return;

            GameLog.Info(LogTag.Startup, "首包资源就绪，显示加载界面…");
            controller.InitUI(OnLoadingUIReady);
        }

        private void OnLoadingUIReady(bool success, string message)
        {
            if (!success)
            {
                uiUnavailable = true;
                GameLog.Warn(LogTag.Startup, "加载界面初始化失败：" + message + "，直接继续热更。");
                return;
            }

            GameController.Instance.UI.Open(LoadingPanel.Name, null, OnLoadingPanelOpened);
        }

        private void OnLoadingPanelOpened(Panel panel)
        {
            loading = panel as LoadingPanel;
            if (loading == null)
            {
                // 面板没打开成功（预制体缺失之类），不要让流程卡死在这里
                uiUnavailable = true;
                GameLog.Warn(LogTag.Startup, "加载界面没有打开成功，跳过等待。");
                return;
            }

            shownTime = Time.unscaledTime;

            // 面板是异步打开的，这期间可能已经收到进度 / 已经下载完了，先补一次当前状态
            if (gotProgress)
                loading.SetHotUpdateProgress(lastReport);
            else if (ready)
                loading.SetImmediate(1f, TipDone);
            else
                loading.SetImmediate(0f, TipChecking);
        }

        private void OnDownloadProgress(ResourceDownloadReport report)
        {
            // 下载结束那一帧 watcher 还会补报一次，别把已经显示的「加载完成」又盖回下载文本
            if (ready)
                return;

            gotProgress = true;
            lastReport = report;

            // 只报一次「开始下载」，后面每帧的进度走界面，不刷日志
            if (!loggedDownloadStart)
            {
                loggedDownloadStart = true;
                GameLog.InfoFormat(LogTag.Startup, "开始下载资源：{0} 个文件 / {1}", report.TotalCount, YooAssetResourceService.FormatBytes(report.TotalBytes));
            }

            if (loading != null)
                loading.SetHotUpdateProgress(report);
        }

        private void OnResourceReady(bool success, string message)
        {
            if (!success)
            {
                failed = true;
                GameLog.Error(LogTag.Startup, "资源初始化失败：" + message + "（按 R 重试）");

                if (loading != null)
                    loading.SetImmediate(0f, "资源初始化失败，按 R 重试");

                return;
            }

            GameLog.Info(LogTag.Startup, "资源就绪：" + message);

            ready = true;
            if (loading != null)
                loading.SetImmediate(1f, TipDone);
        }
    }
}
