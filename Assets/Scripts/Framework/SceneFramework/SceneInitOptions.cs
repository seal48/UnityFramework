using System;
using UnityEngine;

namespace GameFramework.Scenes
{
    /// <summary>场景框架的初始化参数，由 GameController 填好后传给 SceneLoader.Init。</summary>
    [Serializable]
    public sealed class SceneInitOptions
    {
        [Tooltip("切场景时自动开 / 关加载界面（总开关，单个请求还能再关）")]
        public bool ShowLoadingPanel = true;

        [Tooltip("加载界面面板名，要和 [UIPanel] 注册的名字一致")]
        public string LoadingPanelName = "LoadingPanel";

        [Tooltip("加载界面至少停留多久（秒），避免进度一闪而过。0 = 不等")]
        public float MinLoadingTime = 0.6f;

        [Tooltip("加载界面默认提示文本")]
        public string DefaultTip = "正在加载…";

        [Tooltip("切主场景（Single）前关掉所有面板，免得上个界面的界面跟着过来")]
        public bool CloseAllPanelsOnSingle = true;

        [Tooltip("场景目录。传短名（不含 /）时按「目录 + 短名 + .unity」拼地址")]
        public string SceneFolder = "Assets/Scenes/";

        [Tooltip("初始化完成后打一条日志")]
        public bool LogOnInit = true;
    }

    /// <summary>单个场景的加载参数，不传就用默认（Single + 显示加载界面）。</summary>
    [Serializable]
    public sealed class SceneLoadOptions
    {
        [Tooltip("先加载不激活，加载完自己调 Activate 切过去（做无缝切场景用）")]
        public bool SuspendLoad;

        [Tooltip("加载完自动激活场景")]
        public bool ActivateAfterLoad = true;

        [Tooltip("这次加载要不要显示加载界面（还要 SceneInitOptions.ShowLoadingPanel 也开着才显示）")]
        public bool ShowLoadingPanel = true;

        [Tooltip("切主场景前关掉所有面板")]
        public bool CloseAllPanels = true;

        [Tooltip("加载界面的提示文本，留空用默认")]
        public string Tip;

        [Tooltip("最短加载时间（秒），< 0 表示用 SceneInitOptions 里的默认值")]
        public float MinLoadingTime = -1f;
    }
}