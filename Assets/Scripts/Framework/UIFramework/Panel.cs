using GameFramework.Log;
using System;
using System.Collections.Generic;
using System.Text;
using DG.Tweening;
using GameFramework.Event;
using GameFramework.Timer;
using UnityEngine;

namespace GameFramework.UI
{
    /// <summary>
    /// 所有界面的基类。命名约定：XXXXPanel。
    ///
    /// 职责：
    /// 1. 面板第一次使用时遍历整棵 UI 树，把「节点名」和「相对路径」映射到 GameObject 并缓存；
    /// 2. 提供与组件类型无关的取节点接口，调用方不用关心节点上挂的是 Text 还是 Button；
    /// 3. 提供可重载的生命周期：OnInit / OnShow / OnHide / OnUpdate / OnClose。
    ///
    /// 注意：绑定是懒执行的（第一次取值时触发），所以子类不要重载 Awake。
    /// 同理子类不要重载 OnDestroy，需要收尾请用 OnClose。
    /// </summary>
    public class Panel : MonoBehaviour
    {
        /// <summary>组件缓存的 Key：节点实例 ID + 组件类型。</summary>
        private struct ComponentKey : IEquatable<ComponentKey>
        {
            private readonly int instanceId;
            private readonly Type type;

            public ComponentKey(GameObject go, Type type)
            {
                instanceId = go.GetInstanceID();
                this.type = type;
            }

            public bool Equals(ComponentKey other)
            {
                return instanceId == other.instanceId && type == other.type;
            }

            public override bool Equals(object obj)
            {
                return obj is ComponentKey && Equals((ComponentKey)obj);
            }

            public override int GetHashCode()
            {
                return (instanceId * 397) ^ (type != null ? type.GetHashCode() : 0);
            }
        }

        private readonly Dictionary<string, GameObject> nodesByName = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly Dictionary<string, GameObject> nodesByPath = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly Dictionary<ComponentKey, Component> componentCache = new Dictionary<ComponentKey, Component>();
        private readonly List<string> duplicatedNames = new List<string>();
        private readonly List<Component> queryBuffer = new List<Component>();

        private bool bound;
        private bool closed;
        private UIManager manager;
        private UIAnim anim;
        private List<IDisposable> eventSubscriptions;

        [Header("开关动画（在预制体上直接改）")]
        [Tooltip("入场预设")]
        public UIPanelAnimPreset ShowPreset = UIPanelAnimPreset.FadeScale;

        [Tooltip("退场预设")]
        public UIPanelAnimPreset HidePreset = UIPanelAnimPreset.Fade;

        [Tooltip("入场时长，<=0 用全局默认")]
        public float ShowDuration = 0f;

        [Tooltip("退场时长，<=0 用全局默认")]
        public float HideDuration = 0f;

        /// <summary>面板名，等于 UIManager 里注册的名字。</summary>
        public string PanelName { get; private set; }

        /// <summary>面板所在层级。</summary>
        public UILayer Layer { get; private set; }

        /// <summary>是否正在显示。</summary>
        public bool IsShown { get; private set; }

        /// <summary>是否已经完成节点绑定。</summary>
        public bool IsBound { get { return bound; } }

        /// <summary>所属的 UIManager。单独拖到场景里调试时为 null。</summary>
        public UIManager Manager { get { return manager; } }

        /// <summary>事件总线，由 UIManager 在创建面板时注入。没有 GameController 的调试场景里为 null。</summary>
        public IEventBus Events { get; internal set; }

        /// <summary>
        /// 计时器，由 UIManager 在创建面板时注入。面板关闭时会自动取消「归属本面板」的计时器，
        /// 所以注册时用 Delay(this, ...) / Repeat(this, ...) 这种带归属的重载就不会在关闭后打到已销毁的界面上。
        /// 没有 GameController 的调试场景里为 null。
        /// </summary>
        public TimerManager Timers { get; internal set; }

        /// <summary>面板动画器：按节点名直接对组件做动画。懒创建。</summary>
        public UIAnim Anim
        {
            get
            {
                if (anim == null)
                    anim = new UIAnim(this, manager != null ? manager.AnimOptions : null);
                return anim;
            }
        }

        /// <summary>当前是否有动画在播。</summary>
        public bool IsAnimating { get { return anim != null && anim.IsPlayingAny; } }

        /// <summary>立即结束本面板所有动画（不触发完成回调）。</summary>
        public void KillAnimations()
        {
            if (anim != null)
                anim.KillAll();
        }

        /// <summary>
        /// 订阅事件，并在面板销毁时自动退订（不用自己管 IDisposable）。
        /// 注意：订阅从调用开始生效，与面板显示 / 隐藏无关——面板隐藏期间照样会收到事件。
        /// 只想在显示期间收事件的话，别用这个方法：在 OnShow 里自己 Subscribe，OnHide 里 Dispose。
        /// </summary>
        protected IDisposable Listen<T>(Action<T> handler, int priority = 0) where T : IGameEvent
        {
            IEventBus bus = Events;
            if (bus == null)
            {
                GameLog.ErrorFormat(LogTag.Panel, "[{0}] 事件总线还没注入，Listen<{1}> 失败。", name, typeof(T).Name);
                return null;
            }

            IDisposable subscription = bus.Subscribe(handler, priority);

            if (eventSubscriptions == null)
                eventSubscriptions = new List<IDisposable>();

            eventSubscriptions.Add(subscription);
            return subscription;
        }

        /// <summary>发布事件（等价于 Events.Publish，只是省得每次判空）。</summary>
        protected void Send<T>(T evt) where T : IGameEvent
        {
            if (Events != null)
                Events.Publish(evt);
        }

        #region 生命周期：子类按需重载

        /// <summary>
        /// 把节点一次性绑到字段上。默认什么都不做 ——
        /// 由生成代码（<c>XxxPanel.Bindings.g.cs</c>，见菜单 Tools/UI/绑定/生成绑定代码）重写，
        /// 把 <c>Get&lt;T&gt;("名字")</c> 的结果赋给同名字段。
        ///
        /// 调用时机：节点收集完成之后、<see cref="OnInit"/> 之前，
        /// 所以 OnInit 里可以直接用这些字段，不用再写字符串。
        /// </summary>
        protected virtual void BindNodes() { }

        /// <summary>面板第一次创建、节点绑定完成后调用一次（做初始化、注册事件）。</summary>
        protected virtual void OnInit() { }

        /// <summary>每次显示都会调用。userData 是 UIManager.Open 传进来的参数。</summary>
        protected virtual void OnShow(object userData) { }

        /// <summary>每次隐藏都会调用（关闭时也会先走一次）。</summary>
        protected virtual void OnHide() { }

        /// <summary>显示期间每帧调用，由 UIManager.Tick（GameController.Update）驱动。</summary>
        protected virtual void OnUpdate() { }

        /// <summary>面板被销毁前调用一次（反注册事件、释放资源）。</summary>
        protected virtual void OnClose() { }

        #region 动画生命周期：想自定义表现就重载这几个

        /// <summary>
        /// 入场前的准备：此时面板还没被激活，在这里摆好起始状态（默认按 ShowPreset 摆）。
        /// </summary>
        protected virtual void OnPrepareShowAnimation()
        {
            Anim.PreparePreset(ShowPreset);
        }

        /// <summary>入场动画，返回 null 表示不需要等。默认按 ShowPreset 播放。</summary>
        protected virtual Tween OnShowAnimation()
        {
            return Anim.PlayPreset(ShowPreset, true, ShowDuration);
        }

        /// <summary>退场动画，返回 null 表示立刻隐藏。默认按 HidePreset 播放。</summary>
        protected virtual Tween OnHideAnimation()
        {
            return Anim.PlayPreset(HidePreset, false, HideDuration);
        }

        #endregion

        #endregion

        #region 节点绑定

        /// <summary>手动触发一次节点绑定。一般不用调，取值时会自动触发。</summary>
        public void Bind()
        {
            if (bound)
                return;

            bound = true;
            nodesByName.Clear();
            nodesByPath.Clear();
            componentCache.Clear();
            duplicatedNames.Clear();

            CollectNodes(transform, string.Empty);

            if (duplicatedNames.Count > 0)
            {
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < duplicatedNames.Count; i++)
                {
                    if (i > 0)
                        builder.Append(", ");
                    builder.Append(duplicatedNames[i]);
                }

                GameLog.WarnFormat(LogTag.Panel, "[{0}] 存在重名节点：{1}。按短名取会命中第一个，重名节点请用相对路径取，例如 \"A/B/Text\"。", name, builder.ToString());
            }
        }

        private void CollectNodes(Transform parent, string parentPath)
        {
            int count = parent.childCount;
            for (int i = 0; i < count; i++)
            {
                Transform child = parent.GetChild(i);
                string path = parentPath.Length == 0 ? child.name : parentPath + "/" + child.name;

                nodesByPath[path] = child.gameObject;

                GameObject exist;
                if (!nodesByName.TryGetValue(child.name, out exist))
                    nodesByName[child.name] = child.gameObject;
                else if (exist != child.gameObject && !duplicatedNames.Contains(child.name))
                    duplicatedNames.Add(child.name);

                CollectNodes(child, path);
            }
        }

        #endregion

        #region 取节点与组件

        /// <summary>按短名或相对路径取节点，取不到返回 null。</summary>
        public GameObject GetNode(string key)
        {
            GameObject node;
            return TryGetNode(key, out node) ? node : null;
        }

        /// <summary>按短名或相对路径取节点。短名有重名时返回最先找到的那个。</summary>
        public bool TryGetNode(string key, out GameObject node)
        {
            EnsureBound();
            node = null;

            if (string.IsNullOrEmpty(key))
                return false;

            if (nodesByPath.TryGetValue(key, out node))
                return true;

            return nodesByName.TryGetValue(key, out node);
        }

        /// <summary>节点是否存在。</summary>
        public bool Has(string key)
        {
            GameObject node;
            return TryGetNode(key, out node);
        }

        /// <summary>
        /// 取节点上指定类型的组件，类型由调用方决定，框架不关心里面挂的是什么。
        /// 例如：Get&lt;Button&gt;("LoginButton")、Get&lt;Text&gt;("LoginButton/Text")。
        /// </summary>
        public T Get<T>(string key) where T : Component
        {
            T component;
            if (TryGet(key, out component))
                return component;

            GameLog.ErrorFormat(LogTag.Panel, "[{0}] 取不到 {1}：节点 \"{2}\" 不存在，或节点上没挂该组件。", name, typeof(T).Name, key);
            return null;
        }

        /// <summary>取节点上指定类型的组件，取不到返回 false（不报错）。</summary>
        public bool TryGet<T>(string key, out T component) where T : Component
        {
            EnsureBound();
            component = null;

            GameObject node;
            if (!TryGetNode(key, out node) || node == null)
                return false;

            ComponentKey cacheKey = new ComponentKey(node, typeof(T));
            Component cached;
            if (componentCache.TryGetValue(cacheKey, out cached))
            {
                component = cached as T;
                return component != null;
            }

            component = node.GetComponent<T>();
            if (component == null)
                return false;

            componentCache[cacheKey] = component;
            return true;
        }

        /// <summary>先取节点，再在它下面（含自身）递归找组件。适合 ScrollView 这类复合控件。</summary>
        public T GetInChildren<T>(string key) where T : Component
        {
            EnsureBound();

            GameObject node;
            if (!TryGetNode(key, out node) || node == null)
            {
                GameLog.ErrorFormat(LogTag.Panel, "[{0}] 取不到节点 \"{1}\"。", name, key);
                return null;
            }

            T component = node.GetComponentInChildren<T>(true);
            if (component == null)
                GameLog.ErrorFormat(LogTag.Panel, "[{0}] 节点 \"{1}\" 下面没有 {2} 组件。", name, key, typeof(T).Name);

            return component;
        }

        /// <summary>把面板范围内所有指定类型的组件收集到 results（结果会先清空）。</summary>
        public void GetAll<T>(List<T> results) where T : Component
        {
            EnsureBound();
            if (results == null)
                return;

            results.Clear();

            foreach (KeyValuePair<string, GameObject> pair in nodesByPath)
            {
                queryBuffer.Clear();
                pair.Value.GetComponents(queryBuffer);

                for (int i = 0; i < queryBuffer.Count; i++)
                {
                    T component = queryBuffer[i] as T;
                    if (component != null)
                        results.Add(component);
                }
            }
        }

        private void EnsureBound()
        {
            if (!bound)
                Bind();
        }

        #endregion

        #region 显隐控制

        /// <summary>请求关闭自己，等价于 UIManager.Close(PanelName)。</summary>
        public void Close()
        {
            if (manager != null)
                manager.Close(PanelName);
            else
                InternalHideImmediate();
        }

        /// <summary>请求隐藏自己（保留实例），等价于 UIManager.Hide(PanelName)。</summary>
        public void Hide()
        {
            if (manager != null)
                manager.Hide(PanelName);
            else
                InternalHideImmediate();
        }

        #endregion

        #region 框架内部调用：子类不要调

        internal void InternalInitialize(UIManager owner, string panelName, UILayer layer)
        {
            manager = owner;
            PanelName = panelName;
            Layer = layer;

            Bind();
            BindNodes();   // 生成代码在这里把节点赋给字段（默认空实现）
            OnInit();
        }

        /// <summary>显示前的准备：此时还没激活，先摆好动画起始状态，免得第一帧闪出完整界面。</summary>
        internal void InternalPrepareShow()
        {
            Bind();
            OnPrepareShowAnimation();
        }

        /// <summary>激活并播入场动画，返回需要等待的动画。</summary>
        internal Tween InternalPlayShow(object userData)
        {
            gameObject.SetActive(true);
            IsShown = true;
            OnShow(userData);
            return OnShowAnimation();
        }

        /// <summary>播退场动画，返回需要等待的动画；面板本来就没显示时返回 null。</summary>
        internal Tween InternalPlayHide()
        {
            if (!IsShown)
                return null;

            IsShown = false;
            OnHide();
            return OnHideAnimation();
        }

        /// <summary>真正隐藏：收掉所有动画、恢复初始状态、关掉物体。</summary>
        internal void InternalHideImmediate()
        {
            KillAnimations();
            if (anim != null)
                anim.ResetRoot();

            IsShown = false;
            gameObject.SetActive(false);
        }

        /// <summary>入场动画播完后恢复点击。</summary>
        internal void InternalFinishShow()
        {
            if (anim != null)
                anim.ResetRaycastBlock();
        }

        internal void InternalTick()
        {
            if (IsShown)
                OnUpdate();
        }

        internal void InternalClose()
        {
            if (closed)
                return;

            closed = true;
            IsShown = false;
            KillAnimations();

            // 先退订，再跑 OnClose：避免 OnClose 里又被事件回调进来
            if (eventSubscriptions != null)
            {
                for (int i = 0; i < eventSubscriptions.Count; i++)
                {
                    if (eventSubscriptions[i] != null)
                        eventSubscriptions[i].Dispose();
                }

                eventSubscriptions.Clear();
                eventSubscriptions = null;
            }

            // 面板关了，它注册的计时器一起停掉：延迟回调不该在关闭之后再打到这个界面上
            if (Timers != null)
                Timers.CancelOwner(this);

            OnClose();

            // OnClose 里还可以正常收发事件，收尾完再断开总线
            Events = null;
            Timers = null;
        }

        private void OnDestroy()
        {
            // OnClose 保证只被触发一次：不管是 UIManager 主动销毁，还是场景卸载
            InternalClose();
        }

        #endregion
    }
}
