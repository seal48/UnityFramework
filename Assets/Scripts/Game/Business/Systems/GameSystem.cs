using System;
using System.Collections.Generic;
using GameFramework.Event;
using GameFramework.Net.Client;
using GameFramework.Net.Protocol;

namespace GameFramework.Business
{
    /// <summary>
    /// 业务系统基类：一个系统 = 一类数据 + 它负责处理的几条协议。
    ///
    /// 它在整条链路里的位置：
    ///   协议推送 → ProtocolHub → 某个 System 的 OnXxx(payload)
    ///   → 校验 / 算数 / 写 Model → Publish(逻辑事件) → 界面 Listen
    ///
    /// 规矩：System 是唯一碰"协议"的地方，界面永远不订阅协议名 ——
    /// 服务端改协议时只需要动对应的 System，界面一行都不用改。
    /// </summary>
    public abstract class GameSystem : IDisposable
    {
        private readonly List<IDisposable> subscriptions = new List<IDisposable>();
        private bool disposed;

        /// <summary>系统名（默认类名），也是注册到协议中心时留的"处理者"名字。</summary>
        public string Name { get { return GetType().Name; } }

        protected ProtocolHub Hub { get; private set; }

        protected IEventBus Events { get; private set; }

        /// <summary>累计处理了多少条推送（自检 / 调试面板用）。</summary>
        public int HandledCount { get; protected set; }

        /// <summary>框架内部调用：把系统和协议中心、事件总线接起来。</summary>
        internal void Bind(ProtocolHub hub, IEventBus events)
        {
            if (hub == null) throw new ArgumentNullException(nameof(hub));

            Hub = hub;
            Events = events;
            OnBind();
        }

        /// <summary>在这里用 Subscribe 登记自己关心的协议。</summary>
        protected abstract void OnBind();

        /// <summary>订阅一条协议（强类型）：内容会自动反序列化成 T。</summary>
        protected void Subscribe<T>(ProtocolId protocol, Action<T> handler) where T : class
        {
            RequireHub();
            subscriptions.Add(Hub.Register(protocol, handler, Name));
        }

        /// <summary>订阅一条协议（原始消息）：需要自己看字节 / 自己选类型时用。</summary>
        protected void Subscribe(ProtocolId protocol, ProtocolHub.PushHandler handler)
        {
            RequireHub();
            subscriptions.Add(Hub.Register(protocol, handler, Name));
        }

        /// <summary>
        /// 发一条逻辑事件。用 Post（下一帧派发）而不是 Publish：
        /// 一帧里可能收到好几条推送，入队可以避免一层套一层的递归派发。
        /// </summary>
        protected void Publish<T>(T evt) where T : IGameEvent
        {
            if (Events != null) Events.Post(evt);
        }

        private void RequireHub()
        {
            if (Hub == null)
                throw new InvalidOperationException($"{Name} 还没绑定协议中心（Bind 由 BusinessManager 调用）");
        }

        public virtual void Dispose()
        {
            if (disposed) return;
            disposed = true;

            for (int i = 0; i < subscriptions.Count; i++)
                subscriptions[i]?.Dispose();

            subscriptions.Clear();
            Hub = null;
            Events = null;
        }
    }
}
