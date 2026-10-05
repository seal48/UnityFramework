using GameFramework.Log;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GameFramework.UI
{
    /// <summary>
    /// UI 总 Root。挂在 UI Root 预制体上，是所有 UI 操作的基础：
    /// Canvas + EventSystem + 四个层级容器，切换场景时不销毁。
    ///
    /// 层级容器按 UILayer 顺序排列在 Canvas 下面，靠 hierarchy 顺序决定显示前后关系。
    /// 所有补齐逻辑都是幂等的，预制体没配全也能自动补出来。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UIRoot : MonoBehaviour
    {
        /// <summary>四个层级的节点名，下标和 UILayer 一一对应。</summary>
        public static readonly string[] LayerNames = { "Background", "Normal", "Popup", "Top" };

        /// <summary>兜底创建 Canvas 时用的设计分辨率。</summary>
        public static readonly Vector2 DefaultReferenceResolution = new Vector2(1920f, 1080f);

        private static UIRoot instance;

        [SerializeField]
        [Tooltip("根 Canvas，留空会自动查找或创建")]
        private Canvas canvas;

        [SerializeField]
        [Tooltip("切换场景时是否保留（由 UIManager 按 UIInitOptions 设置）")]
        private bool dontDestroyOnLoad = true;

        private readonly RectTransform[] layers = new RectTransform[4];
        private bool ready;

        /// <summary>当前场景里的 UI Root。</summary>
        public static UIRoot Instance { get { return instance; } }

        /// <summary>UI 的根 Canvas。</summary>
        public Canvas Canvas
        {
            get
            {
                EnsureReady();
                return canvas;
            }
        }

        /// <summary>切换场景时是否保留。</summary>
        public bool DontDestroy
        {
            get { return dontDestroyOnLoad; }
            set { dontDestroyOnLoad = value; }
        }

        private void Awake()
        {
            instance = this;

            // 只在运行时调用：编辑期调用会把对象挪到 DontDestroyOnLoad 特殊场景里，影响预制体制作
            if (dontDestroyOnLoad && transform.parent == null && Application.isPlaying)
                DontDestroyOnLoad(gameObject);

            EnsureReady();
        }

        private void OnDestroy()
        {
            if (instance == this)
                instance = null;
        }

        /// <summary>取某个层级的容器，面板打开时都会挂到对应层级下面。</summary>
        public RectTransform GetLayer(UILayer layer)
        {
            EnsureReady();

            int index = (int)layer;
            if (index < 0 || index >= layers.Length)
                index = (int)UILayer.Normal;

            return layers[index];
        }

        /// <summary>补齐 Canvas、四个层级、EventSystem。幂等，重复调用没有副作用。</summary>
        public void EnsureReady()
        {
            if (ready)
                return;

            EnsureCanvas();
            EnsureLayers();
            EnsureEventSystem();

            ready = true;
        }

        private void EnsureCanvas()
        {
            if (canvas == null)
                canvas = GetComponent<Canvas>();

            if (canvas == null)
                canvas = GetComponentInParent<Canvas>();

            if (canvas == null)
            {
                canvas = gameObject.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.pixelPerfect = false;
            }
            else if (canvas.renderMode == RenderMode.WorldSpace)
            {
                // UI 总根用 WorldSpace 基本一定是配错了（没有相机就什么都看不见），这里纠正一下
                GameLog.Warn(LogTag.UIRoot, "根 Canvas 是 WorldSpace，已自动改回 ScreenSpaceOverlay。", this);
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            }

            if (canvas.GetComponent<CanvasScaler>() == null)
            {
                CanvasScaler scaler = canvas.gameObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = DefaultReferenceResolution;
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f;
            }

            if (canvas.GetComponent<GraphicRaycaster>() == null)
                canvas.gameObject.AddComponent<GraphicRaycaster>();
        }

        private void EnsureLayers()
        {
            Transform parent = canvas != null ? canvas.transform : transform;

            for (int i = 0; i < LayerNames.Length; i++)
            {
                RectTransform layer = parent.Find(LayerNames[i]) as RectTransform;
                if (layer == null)
                {
                    GameObject go = new GameObject(LayerNames[i], typeof(RectTransform));
                    layer = go.GetComponent<RectTransform>();
                    layer.SetParent(parent, false);
                }

                Stretch(layer);

                if (layer.GetSiblingIndex() != i)
                    layer.SetSiblingIndex(i);

                layers[i] = layer;
            }
        }

        private void EnsureEventSystem()
        {
            if (EventSystem.current != null)
                return;

            if (FindObjectOfType<EventSystem>() != null)
                return;

            GameObject go = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            go.transform.SetParent(transform, false);
            GameLog.Info(LogTag.UIRoot, "场景里没有 EventSystem，已在 UI Root 下自动创建。", this);
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            rect.localScale = Vector3.one;
        }
    }
}
