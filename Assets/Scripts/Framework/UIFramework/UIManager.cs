using GameFramework.Core;
using GameFramework.Log;
using System;
using System.Collections.Generic;
using System.Reflection;
using DG.Tweening;
using GameFramework.Event;
using GameFramework.Resource;
using GameFramework.Timer;
using UnityEngine;

namespace GameFramework.UI
{
    /// <summary>
    /// UI 总管理器：负责 UI Root 的加载、面板注册、打开 / 关闭、每帧驱动。
    ///
    /// 由 GameController 统一初始化（因为 Root 预制体也是资源，所以要在资源系统就绪之后 Init），
    /// 业务代码通过 GameController.Instance.UI 使用。
    /// </summary>
    public sealed class UIManager : IGameModule, ITickable
    {
        /// <summary>一个面板的注册信息。</summary>
        private sealed class PanelEntry
        {
            public string Name;
            public string Location;
            public UILayer Layer;
            public Type Type;
            public bool Cache;
        }

        private readonly Dictionary<string, PanelEntry> entries = new Dictionary<string, PanelEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, Panel> panels = new Dictionary<string, Panel>(StringComparer.Ordinal);
        private readonly Dictionary<string, ResourceInstance> instances = new Dictionary<string, ResourceInstance>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Action<Panel>>> waiting = new Dictionary<string, List<Action<Panel>>>(StringComparer.Ordinal);
        private readonly HashSet<string> loading = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> openOrder = new List<string>();
        private readonly List<Panel> tickBuffer = new List<Panel>();

        private IResourceService resource;
        private IEventBus eventBus;
        private TimerManager timers;
        private UIInitOptions options;
        private UIRoot root;
        private ResourceInstance rootInstance;
        private GameObject runtimeRoot;
        private Action<bool, string> initCallback;
        private readonly List<Action<bool, string>> initWaiting = new List<Action<bool, string>>();
        private bool initialized;
        private bool initializing;

        /// <summary>某个面板被打开时触发，参数是面板名。</summary>
        public event Action<string> PanelOpened;

        /// <summary>某个面板被关闭 / 隐藏时触发，参数是面板名。</summary>
        public event Action<string> PanelClosed;

        /// <summary>是否初始化完成。</summary>
        public bool IsInitialized { get { return initialized; } }

        /// <summary>
        /// 全局登记的实例（由 Init / Shutdown 自己维护）。
        /// 给框架内的调试工具用 —— 这样它们不必去引用业务层的 GameController。
        /// </summary>
        public static UIManager Current { get; private set; }

        /// <summary>UI 总 Root。</summary>
        public UIRoot Root { get { return root; } }

        /// <summary>UI 根 Canvas。</summary>
        public Canvas Canvas { get { return root != null ? root.Canvas : null; } }

        /// <summary>UI 动画参数，面板里的 UIAnim 会读它。</summary>
        internal UIAnimOptions AnimOptions { get { return options != null ? options.Anim : null; } }

        #region 初始化与关闭

        /// <summary>
        /// 初始化：加载 UI Root 预制体 → 补齐四个层级 → 自动注册面板 → 预加载。
        /// 由 GameController 在资源系统初始化完成后调用。
        /// </summary>
        public void Init(IResourceService resourceService, UIInitOptions initOptions, IEventBus bus,
            TimerManager timerManager, Action<bool, string> onComplete)
        {
            if (initialized)
            {
                if (onComplete != null)
                    onComplete(true, "UI 已经初始化过了");
                return;
            }

            // 初始化是异步的（要加载 UIRoot 预制体）。这期间再有人调 Init 就排队等结果，
            // 否则会重复加载一次 Root，场景里就会出现两个 Canvas / EventSystem。
            if (initializing)
            {
                if (onComplete != null)
                    initWaiting.Add(onComplete);
                return;
            }

            initializing = true;
            initCallback = onComplete;
            options = initOptions != null ? initOptions : new UIInitOptions();

            if (resourceService == null)
            {
                FailInit("资源服务为空，无法加载 UI Root");
                return;
            }

            resource = resourceService;
            eventBus = bus;
            timers = timerManager;

            // UI 动画池按需一次性申请，避免运行中扩容
            if (options.Anim != null && options.Anim.MaxTweens > 0)
                DOTween.SetTweensCapacity(options.Anim.MaxTweens, Mathf.Max(1, options.Anim.MaxSequences));

            if (options.RegisterAllPanels)
                RegisterAll();

            string location = string.IsNullOrEmpty(options.RootPrefabLocation)
                ? UIInitOptions.DefaultRootPrefabLocation
                : options.RootPrefabLocation;

            resource.InstantiateAsync(location, null, OnRootInstantiated);
        }

        /// <summary>关闭整个 UI 框架：销毁所有面板和 UI Root。</summary>
        public void Shutdown()
        {
            DestroyAll();

            waiting.Clear();
            loading.Clear();
            entries.Clear();
            tickBuffer.Clear();
            openOrder.Clear();

            initialized = false;
            initializing = false;
            if (Current == this) Current = null;
            resource = null;
            eventBus = null;
            timers = null;
            options = null;
            initCallback = null;
            initWaiting.Clear();

            if (rootInstance != null)
            {
                rootInstance.Dispose();
                rootInstance = null;
            }

            if (runtimeRoot != null)
            {
                UnityEngine.Object.Destroy(runtimeRoot);
                runtimeRoot = null;
            }

            root = null;
        }

        private void OnRootInstantiated(ResourceInstance instance)
        {
            if (instance == null || instance.GameObject == null)
            {
                if (options.CreateRootIfMissing)
                {
                    GameLog.Warn(LogTag.UIManager, "UI Root 预制体加载失败，改用运行时临时创建的 Root。" +
                                     "正式版请把 Root 预制体配进资源收集器。");
                    CreateRuntimeRoot();
                    CompleteInit();
                    return;
                }

                FailInit("加载 UI Root 预制体失败：" + options.RootPrefabLocation);
                return;
            }

            rootInstance = instance;
            PrepareRoot(instance.GameObject);
            CompleteInit();
        }

        private void CreateRuntimeRoot()
        {
            runtimeRoot = new GameObject("UIRoot", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler),
                typeof(UnityEngine.UI.GraphicRaycaster), typeof(UIRoot));
            PrepareRoot(runtimeRoot);
        }

        private void PrepareRoot(GameObject go)
        {
            if (options.DontDestroyOnLoad && go.transform.parent == null)
                UnityEngine.Object.DontDestroyOnLoad(go);

            root = go.GetComponent<UIRoot>();
            if (root == null)
                root = go.AddComponent<UIRoot>();

            root.DontDestroy = options.DontDestroyOnLoad;
            root.EnsureReady();
        }

        private void CompleteInit()
        {
            initialized = true;
            initializing = false;
            Current = this;

            PreloadPanels();

            GameLog.InfoFormat(LogTag.UIManager, "UI 初始化完成，已注册 {0} 个面板。", entries.Count);
            NotifyInit(true, "UI 初始化完成");
        }

        private void FailInit(string message)
        {
            GameLog.Error(LogTag.UIManager, message);

            initializing = false;
            NotifyInit(false, message);
        }

        /// <summary>把初始化结果发给发起者，以及初始化期间排队等待的那些调用方。</summary>
        private void NotifyInit(bool success, string message)
        {
            Action<bool, string> callback = initCallback;
            initCallback = null;

            if (callback != null)
                callback(success, message);

            if (initWaiting.Count == 0)
                return;

            // 回调里可能又调 Init（少见但合法），先拷一份再清空
            List<Action<bool, string>> callbacks = new List<Action<bool, string>>(initWaiting);
            initWaiting.Clear();

            for (int i = 0; i < callbacks.Count; i++)
            {
                if (callbacks[i] != null)
                    callbacks[i](success, message);
            }
        }

        #endregion

        #region 注册

        /// <summary>注册一个面板。name 是打开 / 关闭时用的 Key，location 是预制体地址。</summary>
        public void Register<T>(string name, string location, UILayer layer = UILayer.Normal, bool cache = true) where T : Panel
        {
            Register(typeof(T), name, location, layer, cache);
        }

        /// <summary>注册一个面板。</summary>
        public void Register(Type type, string name, string location, UILayer layer, bool cache)
        {
            if (type == null || !typeof(Panel).IsAssignableFrom(type))
            {
                GameLog.ErrorFormat(LogTag.UIManager, "注册失败，{0} 不是 Panel 的子类。", type != null ? type.Name : "null");
                return;
            }

            if (string.IsNullOrEmpty(name))
            {
                GameLog.Error(LogTag.UIManager, "注册失败，面板名不能为空。");
                return;
            }

            if (entries.ContainsKey(name))
            {
                GameLog.WarnFormat(LogTag.UIManager, "面板 \"{0}\" 已经注册过了，本次注册被忽略。", name);
                return;
            }

            PanelEntry entry = new PanelEntry();
            entry.Name = name;
            entry.Location = location;
            entry.Layer = layer;
            entry.Type = type;
            entry.Cache = cache;

            entries[name] = entry;
        }

        /// <summary>反射扫描所有带 [UIPanel] 特性的 Panel 子类并注册。Init 时会自动调用。</summary>
        public void RegisterAll()
        {
            string folder = options != null && !string.IsNullOrEmpty(options.PanelPrefabFolder)
                ? options.PanelPrefabFolder
                : UIInitOptions.DefaultPanelPrefabFolder;

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type[] types;
                try
                {
                    types = assemblies[i].GetTypes();
                }
                catch (Exception)
                {
                    continue;
                }

                for (int j = 0; j < types.Length; j++)
                {
                    Type type = types[j];
                    if (type == null || type.IsAbstract || !typeof(Panel).IsAssignableFrom(type))
                        continue;

                    object[] attributes = type.GetCustomAttributes(typeof(UIPanelAttribute), false);
                    if (attributes.Length == 0)
                        continue;

                    UIPanelAttribute attribute = (UIPanelAttribute)attributes[0];
                    string location = string.IsNullOrEmpty(attribute.Location)
                        ? folder + attribute.Name + ".prefab"
                        : attribute.Location;

                    Register(type, attribute.Name, location, attribute.Layer, attribute.Cache);
                }
            }
        }

        /// <summary>面板是否已经注册。</summary>
        public bool IsRegistered(string name)
        {
            return entries.ContainsKey(name);
        }

        #endregion

        #region 打开 / 关闭

        /// <summary>按类型打开面板，userData 会原样传给 Panel.OnShow。</summary>
        public void Open<T>(object userData = null, Action<Panel> onOpened = null) where T : Panel
        {
            string name = FindNameByType(typeof(T));
            if (name == null)
            {
                GameLog.ErrorFormat(LogTag.UIManager, "面板 {0} 还没注册，先 Register 或给它加 [UIPanel] 特性。", typeof(T).Name);
                return;
            }

            Open(name, userData, onOpened);
        }

        /// <summary>按面板名打开。第一次打开会异步加载预制体，加载期间重复调用会排队。</summary>
        public void Open(string name, object userData = null, Action<Panel> onOpened = null)
        {
            if (!CheckReady())
                return;

            PanelEntry entry;
            if (!entries.TryGetValue(name, out entry))
            {
                GameLog.ErrorFormat(LogTag.UIManager, "面板 \"{0}\" 未注册。", name);
                return;
            }

            Panel opened = Get(name);
            if (opened != null)
            {
                Show(opened, userData, onOpened);
                return;
            }

            AddWaiting(name, delegate(Panel panel)
            {
                if (panel == null)
                    return;

                Show(panel, userData, onOpened);
            });

            if (loading.Contains(name))
                return;

            loading.Add(name);
            resource.InstantiateAsync(entry.Location, root.GetLayer(entry.Layer),
                delegate(ResourceInstance instance) { OnPanelInstantiated(entry, instance); });
        }

        /// <summary>关闭面板：播完退场动画后隐藏（cache = true）或销毁。</summary>
        public void Close(string name)
        {
            Panel panel = Get(name);
            if (panel == null)
                return;

            StartHide(panel, !IsCached(name));
        }

        /// <summary>按类型关闭面板。</summary>
        public void Close<T>() where T : Panel
        {
            string name = FindNameByType(typeof(T));
            if (name != null)
                Close(name);
        }

        /// <summary>只隐藏，保留实例（不管注册时的缓存设置是什么）。</summary>
        public void Hide(string name)
        {
            Panel panel = Get(name);
            if (panel == null)
                return;

            StartHide(panel, false);
        }

        /// <summary>强制销毁面板实例（不管它是显示中还是隐藏缓存）。</summary>
        public void DestroyPanel(string name)
        {
            Panel panel = Get(name);
            if (panel != null)
                panel.InternalClose();

            ResourceInstance instance;
            if (instances.TryGetValue(name, out instance))
            {
                instances.Remove(name);
                if (instance != null)
                    instance.Dispose();
            }

            panels.Remove(name);
            openOrder.Remove(name);
        }

        /// <summary>关闭当前所有显示中的面板（立即收，不播退场动画，切场景 / 退出时用）。</summary>
        public void CloseAll()
        {
            List<string> names = new List<string>(openOrder);
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                FinishHide(Get(name), !IsCached(name));

                if (PanelClosed != null)
                    PanelClosed(name);
            }

            openOrder.Clear();
        }

        /// <summary>销毁所有面板实例，回到「一个面板都没创建」的状态。</summary>
        public void DestroyAll()
        {
            List<string> names = new List<string>(panels.Keys);
            for (int i = 0; i < names.Count; i++)
                DestroyPanel(names[i]);

            panels.Clear();
            instances.Clear();
            openOrder.Clear();
        }

        private void OnPanelInstantiated(PanelEntry entry, ResourceInstance instance)
        {
            loading.Remove(entry.Name);

            Panel panel = null;

            if (instance != null && instance.GameObject != null)
            {
                panel = instance.GameObject.GetComponent<Panel>();
                if (panel == null)
                {
                    GameLog.ErrorFormat(LogTag.UIManager, "预制体 {0} 的根节点上没有 Panel 组件，打开失败。", entry.Location);
                    instance.Dispose();
                }
                else
                {
                    // 新建出来的面板先隐藏，等 Show 的时候再显示
                    instance.GameObject.SetActive(false);

                    panel.Events = eventBus;
                    panel.Timers = timers;
                    panel.InternalInitialize(this, entry.Name, entry.Layer);
                    panels[entry.Name] = panel;
                    instances[entry.Name] = instance;
                }
            }
            else
            {
                GameLog.ErrorFormat(LogTag.UIManager, "加载面板预制体失败：{0}", entry.Location);
            }

            NotifyWaiting(entry.Name, panel);
        }

        /// <summary>
        /// 显示面板：先摆起始状态 → 激活 → 播入场动画 → 动画播完再回调 onOpened。
        /// 没有动画（预设为 None / 动画总开关关掉）时立刻回调。
        /// </summary>
        private void Show(Panel panel, object userData, Action<Panel> onOpened)
        {
            if (root != null)
            {
                Transform layer = root.GetLayer(panel.Layer);
                if (layer != null && panel.transform.parent != layer)
                    panel.transform.SetParent(layer, false);
            }

            panel.transform.SetAsLastSibling();

            // 打断上一次没播完的动画（比如退场播到一半又被打开），再摆起始状态
            panel.KillAnimations();
            panel.InternalPrepareShow();

            Tween tween = panel.InternalPlayShow(userData);

            if (!openOrder.Contains(panel.PanelName))
                openOrder.Add(panel.PanelName);

            if (PanelOpened != null)
                PanelOpened(panel.PanelName);

            if (tween == null || !tween.IsActive())
            {
                panel.InternalFinishShow();
                if (onOpened != null)
                    onOpened(panel);
                return;
            }

            // 动画被别的操作打断（例如刚打开就被关闭）时，这个回调不会再触发
            tween.OnComplete(delegate
            {
                panel.InternalFinishShow();
                if (onOpened != null)
                    onOpened(panel);
            });
        }

        /// <summary>播退场动画，动画结束后再把面板收起来（destroyAfter = true 时销毁）。</summary>
        private void StartHide(Panel panel, bool destroyAfter)
        {
            string name = panel.PanelName;
            openOrder.Remove(name);

            if (PanelClosed != null)
                PanelClosed(name);

            Tween tween = panel.InternalPlayHide();
            if (tween == null || !tween.IsActive())
            {
                FinishHide(panel, destroyAfter);
                return;
            }

            tween.OnComplete(delegate { FinishHide(panel, destroyAfter); });
        }

        /// <summary>退场动画播完（或不需要动画）后真正把面板收起来。</summary>
        private void FinishHide(Panel panel, bool destroyAfter)
        {
            if (panel == null)
                return;

            if (destroyAfter)
                DestroyPanel(panel.PanelName);
            else
                panel.InternalHideImmediate();
        }

        /// <summary>注册时是否勾了「缓存面板」。</summary>
        private bool IsCached(string name)
        {
            PanelEntry entry;
            return entries.TryGetValue(name, out entry) && entry.Cache;
        }

        /// <summary>每帧驱动，由 GameController.Update 调用。</summary>
        public void Tick(float deltaTime, float unscaledDeltaTime)
        {
            if (!initialized || openOrder.Count == 0)
                return;

            tickBuffer.Clear();
            for (int i = 0; i < openOrder.Count; i++)
            {
                Panel panel = Get(openOrder[i]);
                if (panel != null && panel.IsShown)
                    tickBuffer.Add(panel);
            }

            for (int i = 0; i < tickBuffer.Count; i++)
                tickBuffer[i].InternalTick();
        }

        /// <summary>预加载：只创建不显示，减少第一次打开的卡顿。</summary>
        private void PreloadPanels()
        {
            if (options == null || options.PreloadPanels == null)
                return;

            for (int i = 0; i < options.PreloadPanels.Length; i++)
            {
                string name = options.PreloadPanels[i];
                if (string.IsNullOrEmpty(name))
                    continue;

                PanelEntry entry;
                if (!entries.TryGetValue(name, out entry))
                {
                    GameLog.WarnFormat(LogTag.UIManager, "预加载失败，面板 \"{0}\" 未注册。", name);
                    continue;
                }

                if (panels.ContainsKey(name) || loading.Contains(name))
                    continue;

                loading.Add(name);
                resource.InstantiateAsync(entry.Location, root.GetLayer(entry.Layer),
                    delegate(ResourceInstance instance) { OnPanelInstantiated(entry, instance); });
            }
        }

        #endregion

        #region 查询

        /// <summary>取已经创建出来的面板实例（含隐藏缓存的），没有则返回 null。</summary>
        public Panel Get(string name)
        {
            Panel panel;
            if (panels.TryGetValue(name, out panel))
                return panel;

            return null;
        }

        /// <summary>按类型取面板实例。</summary>
        public T Get<T>() where T : Panel
        {
            string name = FindNameByType(typeof(T));
            if (name == null)
                return null;

            return Get(name) as T;
        }

        /// <summary>面板是否存在并且正在显示。</summary>
        public bool IsOpen(string name)
        {
            Panel panel = Get(name);
            return panel != null && panel.IsShown;
        }

        /// <summary>取所有已经创建出来的面板，结果写进 results（会先清空）。</summary>
        public void GetAll(List<Panel> results)
        {
            if (results == null)
                return;

            results.Clear();
            results.AddRange(panels.Values);
        }

        private string FindNameByType(Type type)
        {
            foreach (KeyValuePair<string, PanelEntry> pair in entries)
            {
                if (pair.Value.Type == type)
                    return pair.Key;
            }

            return null;
        }

        private bool CheckReady()
        {
            if (initialized)
                return true;

            GameLog.Error(LogTag.UIManager, "UI 还没初始化完成，Open / Close 请放在 UIManager.Init 的回调之后调用。");
            return false;
        }

        private void AddWaiting(string name, Action<Panel> callback)
        {
            if (callback == null)
                return;

            List<Action<Panel>> callbacks;
            if (!waiting.TryGetValue(name, out callbacks))
            {
                callbacks = new List<Action<Panel>>();
                waiting[name] = callbacks;
            }

            callbacks.Add(callback);
        }

        private void NotifyWaiting(string name, Panel panel)
        {
            List<Action<Panel>> callbacks;
            if (!waiting.TryGetValue(name, out callbacks))
                return;

            waiting.Remove(name);

            for (int i = 0; i < callbacks.Count; i++)
            {
                if (callbacks[i] != null)
                    callbacks[i](panel);
            }
        }

        #endregion
    }
}
