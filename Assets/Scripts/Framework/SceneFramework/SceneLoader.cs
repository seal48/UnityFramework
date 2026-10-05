using System;
using System.Collections.Generic;
using GameFramework.Core;
using GameFramework.Log;
using GameFramework.Resource;
using GameFramework.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameFramework.Scenes
{
    /// <summary>
    /// 场景管理：统一走资源框架（YooAsset）加载，所以场景能跟着热更走；
    /// 切主场景时自动关界面、开加载界面、等加载完再收尾。
    ///
    /// 由 GameController 在 Awake 创建、ProcedureInitScene 初始化，之后每帧 Tick。
    /// 业务用 GameController.Instance.Scene，或者短一点 GameFramework.Scene.SceneKit.Load("Lobby")。
    ///
    /// 一次只处理一个加载请求，同时来的排队等（不会出现两个加载界面打架）。
    /// </summary>
    public sealed class SceneLoader : IGameModule, ITickable
    {
        /// <summary>一个排队的加载请求。</summary>
        private sealed class Request
        {
            public string Location;
            public LoadSceneMode Mode;
            public SceneLoadOptions Options;
            public Action<bool, string> OnComplete;
        }

        private IResourceService resource;
        private UIManager ui;
        private SceneInitOptions options;

        private readonly Queue<Request> queue = new Queue<Request>();
        private readonly Dictionary<string, ISceneLoadOperation> additiveScenes =
            new Dictionary<string, ISceneLoadOperation>(StringComparer.Ordinal);

        private bool initialized;
        private Request current;
        private ISceneLoadOperation operation;
        private ISceneLoadOperation singleScene;
        private ISceneLoadOperation pendingScene;
        private LoadSceneMode pendingSceneMode;
        private float elapsed;
        private float minTime;
        private IProgressPanel loadingPanel;
        private string currentSceneName = string.Empty;

        /// <summary>开始加载一个场景（切下去之前）。</summary>
        public event Action<string> LoadStarted;

        /// <summary>一次加载结束（成功与否都发）。参数：地址、是否成功、失败原因。</summary>
        public event Action<string, bool, string> LoadFinished;

        /// <summary>是否已经初始化。</summary>
        public bool IsInitialized { get { return initialized; } }

        /// <summary>是否正在加载（排队中不算）。</summary>
        public bool IsLoading { get { return operation != null; } }

        /// <summary>当前加载进度 0~1。</summary>
        public float Progress { get { return operation != null ? operation.Progress : 0f; } }

        /// <summary>当前场景名。</summary>
        public string CurrentSceneName
        {
            get
            {
                UnityEngine.SceneManagement.Scene active = SceneManager.GetActiveScene();
                if (active.IsValid() && !string.IsNullOrEmpty(active.name))
                    return active.name;

                return currentSceneName;
            }
        }

        /// <summary>正在加载的场景地址，没在加载时为 null。</summary>
        public string LoadingLocation { get { return current != null ? current.Location : null; } }

        /// <summary>排队等待的请求数。</summary>
        public int PendingCount { get { return queue.Count + (operation != null ? 1 : 0); } }

        #region 生命周期

        /// <summary>初始化。没有异步步骤，GameController 在流程里直接调。</summary>
        public void Init(IResourceService resourceService, SceneInitOptions initOptions, UIManager uiManager,
            Action<bool, string> onComplete)
        {
            if (initialized)
            {
                if (onComplete != null) onComplete(true, "场景管理已经初始化过了");
                return;
            }

            if (resourceService == null)
            {
                if (onComplete != null) onComplete(false, "资源服务为空，无法初始化场景管理");
                return;
            }

            resource = resourceService;
            options = initOptions != null ? initOptions : new SceneInitOptions();
            ui = uiManager;

            UnityEngine.SceneManagement.Scene active = SceneManager.GetActiveScene();
            currentSceneName = active.IsValid() ? active.name : string.Empty;

            initialized = true;
            SceneKit.Registered = this;

            if (options.LogOnInit)
                GameLog.Info(LogTag.Scene, "场景管理就绪：当前场景 " + currentSceneName);

            if (onComplete != null) onComplete(true, "场景管理就绪");
        }

        /// <summary>关闭：释放句柄、隐藏加载界面。</summary>
        public void Shutdown()
        {
            queue.Clear();
            current = null;
            operation = null;

            if (singleScene != null)
            {
                singleScene.Release();
                singleScene = null;
            }

            if (pendingScene != null)
            {
                pendingScene.Release();
                pendingScene = null;
            }

            foreach (KeyValuePair<string, ISceneLoadOperation> pair in additiveScenes)
            {
                if (pair.Value != null)
                    pair.Value.Release();
            }

            additiveScenes.Clear();

            if (ui != null && loadingPanel != null)
                ui.Hide(options.LoadingPanelName);

            loadingPanel = null;
            resource = null;
            options = null;
            ui = null;
            initialized = false;
            if (SceneKit.Registered == this) SceneKit.Registered = null;
        }

        /// <summary>每帧调用，由 GameController 转发。用 unscaledDeltaTime，暂停时加载界面照转。</summary>
        public void Tick(float deltaTime, float unscaledDeltaTime)
        {
            if (!initialized)
                return;

            if (operation == null)
            {
                StartNext();
                return;
            }

            elapsed += unscaledDeltaTime;

            if (!operation.IsDone)
            {
                // 留一点进度给收尾（激活场景 / 关界面），别让进度条提前满格卡住
                SetPanelProgress(Mathf.Min(operation.Progress, 0.95f));
                return;
            }

            if (minTime > 0f && elapsed < minTime)
            {
                SetPanelProgress(0.99f);
                return;
            }

            CompleteCurrent();
        }

        #endregion

        #region 加载 / 卸载

        /// <summary>切主场景（Single：旧场景会被卸载）。</summary>
        public void LoadAsync(string location, Action<bool, string> onComplete = null)
        {
            Enqueue(location, LoadSceneMode.Single, null, onComplete);
        }

        /// <summary>切主场景，带参数。</summary>
        public void LoadAsync(string location, SceneLoadOptions loadOptions, Action<bool, string> onComplete = null)
        {
            Enqueue(location, LoadSceneMode.Single, loadOptions, onComplete);
        }

        /// <summary>叠加场景（Additive：不卸载当前场景，常用于 UI / 战斗子场景）。</summary>
        public void LoadAdditiveAsync(string location, Action<bool, string> onComplete = null)
        {
            Enqueue(location, LoadSceneMode.Additive, null, onComplete);
        }

        /// <summary>叠加场景，带参数。</summary>
        public void LoadAdditiveAsync(string location, SceneLoadOptions loadOptions,
            Action<bool, string> onComplete = null)
        {
            Enqueue(location, LoadSceneMode.Additive, loadOptions, onComplete);
        }

        /// <summary>卸载一个用 LoadAdditiveAsync 加载的场景。</summary>
        public void UnloadAsync(string location, Action<bool, string> onComplete = null)
        {
            if (!initialized)
            {
                if (onComplete != null) onComplete(false, "场景管理还没初始化");
                return;
            }

            string key = Resolve(location);

            ISceneLoadOperation scene;
            if (!additiveScenes.TryGetValue(key, out scene) || scene == null)
            {
                if (onComplete != null) onComplete(false, "没有加载过这个叠加场景：" + key);
                return;
            }

            additiveScenes.Remove(key);

            GameLog.Info(LogTag.Scene, "卸载叠加场景：" + key);
            scene.UnloadAsync(delegate
            {
                if (onComplete != null) onComplete(true, null);
            });
        }

        private void Enqueue(string location, LoadSceneMode mode, SceneLoadOptions loadOptions,
            Action<bool, string> onComplete)
        {
            if (!initialized)
            {
                GameLog.Warn(LogTag.Scene, "场景管理还没初始化就调了加载（是不是在 ProcedureInitScene 之前？）。");
                if (onComplete != null) onComplete(false, "场景管理还没初始化");
                return;
            }

            if (string.IsNullOrEmpty(location))
            {
                if (onComplete != null) onComplete(false, "场景地址为空");
                return;
            }

            queue.Enqueue(new Request
            {
                Location = Resolve(location),
                Mode = mode,
                Options = loadOptions != null ? loadOptions : new SceneLoadOptions(),
                OnComplete = onComplete,
            });
        }

        /// <summary>把短名补成工程里的真实地址；带 / 的地址原样使用。</summary>
        public string Resolve(string location)
        {
            if (string.IsNullOrEmpty(location))
                return location;

            if (location.IndexOf('/') >= 0 || location.IndexOf('\\') >= 0)
                return location;

            string folder = options != null ? options.SceneFolder : null;
            if (string.IsNullOrEmpty(folder))
                return location;

            if (folder[folder.Length - 1] != '/')
                folder = folder + "/";

            return location.EndsWith(".unity") ? folder + location : folder + location + ".unity";
        }

        #endregion

        #region 内部

        private void StartNext()
        {
            if (queue.Count == 0)
                return;

            current = queue.Dequeue();
            elapsed = 0f;
            minTime = current.Options.MinLoadingTime >= 0f
                ? current.Options.MinLoadingTime
                : options.MinLoadingTime;

            GameLog.Info(LogTag.Scene, "开始加载场景：" + current.Location +
                                      (current.Mode == LoadSceneMode.Additive ? "（叠加）" : "（单场景）"));

            ISceneLoadOperation op = resource.LoadSceneAsync(current.Location, current.Mode, current.Options.SuspendLoad);
            if (op == null)
            {
                FinishCurrent(false, "加载场景失败：" + current.Location + "（资源收集器里配了吗？）");
                return;
            }

            operation = op;

            // 先关旧界面，再开加载界面，否则会被一起关掉
            if (current.Mode == LoadSceneMode.Single && current.Options.CloseAllPanels && options.CloseAllPanelsOnSingle && ui != null)
                ui.CloseAll();

            if (options.ShowLoadingPanel && current.Options.ShowLoadingPanel)
                ShowLoadingPanel(current.Options.Tip);

            Action<string> started = LoadStarted;
            if (started != null)
                started(current.Location);
        }

        private void CompleteCurrent()
        {
            ISceneLoadOperation op = operation;
            Request request = current;

            string error = op.LastError;
            if (!string.IsNullOrEmpty(error))
            {
                op.Release();
                operation = null;
                FinishCurrent(false, error);
                return;
            }

            operation = null;

            if (request.Options.SuspendLoad && !request.Options.ActivateAfterLoad)
            {
                // 先加载不激活：等业务自己挑时机调 ActivateLoadedScene()
                pendingScene = op;
                pendingSceneMode = request.Mode;
            }
            else
            {
                if (request.Options.SuspendLoad)
                    op.Activate();

                Register(op, request.Mode);
            }

            UnityEngine.SceneManagement.Scene scene = op.Scene;
            if (scene.IsValid())
                currentSceneName = scene.name;

            GameLog.Info(LogTag.Scene, "场景加载完成：" + op.Location);
            FinishCurrent(true, null);
        }

        /// <summary>登记一个加载好的场景（主场景 / 叠加场景分开管）。</summary>
        private void Register(ISceneLoadOperation op, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Single)
            {
                if (singleScene != null && singleScene != op)
                    singleScene.Release();

                singleScene = op;
                return;
            }

            ISceneLoadOperation old;
            if (additiveScenes.TryGetValue(op.Location, out old) && old != null && old != op)
                old.Release();

            additiveScenes[op.Location] = op;
        }

        /// <summary>把之前挂起加载（SuspendLoad）的场景激活，做无缝切场景用。</summary>
        public bool ActivateLoadedScene()
        {
            if (pendingScene == null)
                return false;

            ISceneLoadOperation op = pendingScene;
            LoadSceneMode mode = pendingSceneMode;

            pendingScene = null;

            bool ok = op.Activate();
            Register(op, mode);

            UnityEngine.SceneManagement.Scene scene = op.Scene;
            if (scene.IsValid())
                currentSceneName = scene.name;

            GameLog.Info(LogTag.Scene, (ok ? "已激活挂起的场景：" : "激活场景失败：") + op.Location);
            return ok;
        }

        private void FinishCurrent(bool success, string error)
        {
            Request request = current;

            operation = null;
            current = null;

            HideLoadingPanel();

            if (!success)
                GameLog.Error(LogTag.Scene, error ?? "场景加载失败");

            Action<string, bool, string> finished = LoadFinished;
            if (finished != null && request != null)
                finished(request.Location, success, error);

            if (request != null && request.OnComplete != null)
                request.OnComplete(success, error);
        }

        private void ShowLoadingPanel(string tip)
        {
            if (ui == null || !ui.IsRegistered(options.LoadingPanelName))
            {
                if (ui != null)
                    GameLog.Warn(LogTag.Scene, "加载界面 \"" + options.LoadingPanelName + "\" 没注册，这次不显示进度。");

                return;
            }

            string text = string.IsNullOrEmpty(tip) ? options.DefaultTip : tip;

            Action<Panel> onOpened = delegate(Panel panel)
            {
                loadingPanel = panel as IProgressPanel;
                if (loadingPanel == null)
                    return;

                loadingPanel.SetImmediate(0f, text);
            };

            Panel existing = ui.Get(options.LoadingPanelName);
            if (existing != null)
            {
                loadingPanel = existing as IProgressPanel;
                if (loadingPanel != null)
                    loadingPanel.SetImmediate(0f, text);
                return;
            }

            ui.Open(options.LoadingPanelName, null, onOpened);
        }

        private void SetPanelProgress(float progress)
        {
            if (loadingPanel != null)
                loadingPanel.SetProgress(progress);
        }

        private void HideLoadingPanel()
        {
            if (loadingPanel != null)
                loadingPanel.SetImmediate(1f, null);

            if (ui != null && ui.IsOpen(options.LoadingPanelName))
                ui.Hide(options.LoadingPanelName);

            loadingPanel = null;
        }

        #endregion
    }
}