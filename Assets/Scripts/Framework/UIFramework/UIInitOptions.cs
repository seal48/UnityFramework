using System;
using UnityEngine;

namespace GameFramework.UI
{
    /// <summary>
    /// UI 框架初始化参数。由 GameController 填好后传给 UIManager.Init。
    /// </summary>
    [Serializable]
    public sealed class UIInitOptions
    {
        /// <summary>UI 总 Root 预制体的默认地址。</summary>
        public const string DefaultRootPrefabLocation = "Assets/Prefabs/UIRoot.prefab";

        /// <summary>面板预制体的默认目录，[UIPanel] 没写 Location 时用它拼地址。</summary>
        public const string DefaultPanelPrefabFolder = "Assets/Prefabs/";

        [Tooltip("UI 总 Root 预制体地址（YooAsset 可寻址地址）。预制体里是 Canvas + EventSystem + 四个层级。")]
        public string RootPrefabLocation = DefaultRootPrefabLocation;

        [Tooltip("切换场景时是否保留 UI Root，勾上后所有 UI 跨场景常驻")]
        public bool DontDestroyOnLoad = true;

        [Tooltip("Root 预制体加载失败时，用代码临时创建一个 Root（方便工程起步阶段，正式版建议关掉）")]
        public bool CreateRootIfMissing = true;

        [Tooltip("初始化时自动注册所有带 [UIPanel] 特性的面板")]
        public bool RegisterAllPanels = true;

        [Tooltip("面板预制体默认目录，[UIPanel] 没写 Location 时用它 + 面板名 + .prefab")]
        public string PanelPrefabFolder = DefaultPanelPrefabFolder;

        [Tooltip("UI 动画参数（DOTween 包装层）")]
        public UIAnimOptions Anim = new UIAnimOptions();

        [Tooltip("启动时预加载（只创建不显示）的面板名，可以减少第一次打开界面时的卡顿")]
        public string[] PreloadPanels = new string[0];
    }
}
