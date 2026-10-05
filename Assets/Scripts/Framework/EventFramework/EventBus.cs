using GameFramework.Core;
using GameFramework.Log;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace GameFramework.Event
{
    /// <summary>
    /// 事件总线实现。业务只依赖 IEventBus，底层换成别的实现不影响业务代码。
    ///
    /// 设计要点：
    /// - 一个事件类型一条「通道」，同一条通道内按优先级从高到低执行（相等则按订阅先后）；
    /// - 派发过程中订阅 / 退订是安全的：本轮已经开始派发的事件不会因为增删而乱序或漏派，
    ///   派发中新增的订阅从下一次派发开始生效；
    /// - 某个 handler 抛异常只打日志，不影响同一条事件里的其它 handler；
    /// - 没人订阅时 Publish 是零开销（连通道都不会创建）；
    /// - 事件类型不会无限堆积：没有订阅也没有待派发事件的通道会在 Tick 时回收。
    /// </summary>
    public sealed class EventBus : IEventBus, IGameModule, ITickable
    {
        /// <summary>一条事件的内部接口，让总线能用「类型」当 Key 存不同的事件通道。</summary>
        private interface IChannel
        {
            Type EventType { get; }
            int SubscriberCount { get; }
            bool IsEmpty { get; }
            void Flush(int maxPerFlush, bool logPublish);
            void Discard();
            void AppendDump(StringBuilder sb);
        }

        /// <summary>订阅句柄：Dispose 就是退订。</summary>
        private sealed class Subscription<T> : IDisposable where T : IGameEvent
        {
            private EventBus bus;
            private Action<T> handler;

            public Subscription(EventBus bus, Action<T> handler)
            {
                this.bus = bus;
                this.handler = handler;
            }

            public void Dispose()
            {
                if (bus == null)
                    return;

                bus.Unsubscribe(handler);
                bus = null;
                handler = null;
            }
        }

        /// <summary>某个事件类型的订阅表 + 延迟队列。</summary>
        private sealed class Channel<T> : IChannel where T : IGameEvent
        {
            private sealed class Entry
            {
                public Action<T> Handler;
                public int Priority;
                public int Seq;
                public bool Once;
                public bool Removed;
            }

            private readonly List<Entry> entries = new List<Entry>();
            private Queue<T> posted;
            private int seq;
            private int dispatchDepth;
            private bool dirty;

            public Type EventType { get { return typeof(T); } }

            public int SubscriberCount
            {
                get
                {
                    if (!dirty)
                        return entries.Count;

                    int count = 0;
                    for (int i = 0; i < entries.Count; i++)
                    {
                        if (!entries[i].Removed)
                            count++;
                    }
                    return count;
                }
            }

            public bool IsEmpty
            {
                get
                {
                    if (SubscriberCount > 0)
                        return false;

                    return posted == null || posted.Count == 0;
                }
            }

            public void Subscribe(Action<T> handler, int priority, bool once)
            {
                Entry entry = new Entry();
                entry.Handler = handler;
                entry.Priority = priority;
                entry.Once = once;
                entry.Seq = seq++;

                // 派发中新增的订阅：先排到末尾，本轮不派发（结束时统一重排）
                if (dispatchDepth > 0)
                {
                    entries.Add(entry);
                    dirty = true;
                    return;
                }

                int index = entries.Count;
                while (index > 0 && entries[index - 1].Priority < priority)
                    index--;

                entries.Insert(index, entry);
            }

            public bool Unsubscribe(Action<T> handler)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    Entry entry = entries[i];
                    if (entry.Removed || !ReferenceEquals(entry.Handler, handler))
                        continue;

                    entry.Removed = true;
                    dirty = true;
                    return true;
                }

                return false;
            }

            public void UnsubscribeAll()
            {
                for (int i = 0; i < entries.Count; i++)
                    entries[i].Removed = true;

                if (posted != null)
                    posted.Clear();

                dirty = true;
            }

            public void Post(T evt)
            {
                if (posted == null)
                    posted = new Queue<T>();

                posted.Enqueue(evt);
            }

            public void Flush(int maxPerFlush, bool logPublish)
            {
                if (posted == null || posted.Count == 0)
                    return;

                int budget = maxPerFlush;
                while (posted.Count > 0)
                {
                    if (budget-- <= 0)
                    {
                        GameLog.ErrorFormat(LogTag.EventBus, "一次 Tick 里 {0} 的延迟事件超过 {1} 条，疑似「处理事件时又 Post 同类型事件」的死循环，本轮强制停止。", EventType.Name, maxPerFlush);
                        break;
                    }

                    Dispatch(posted.Dequeue(), logPublish);
                }
            }

            public void Dispatch(T evt, bool logPublish)
            {
                if (logPublish)
                    GameLog.InfoFormat(LogTag.EventBus, "{0} → {1}", EventType.Name, (object)evt == null ? "null" : evt.ToString());

                dispatchDepth++;
                try
                {
                    // 先记住本轮要派发的数量：派发过程中新增的订阅从下一次开始生效
                    int count = entries.Count;
                    for (int i = 0; i < count; i++)
                    {
                        Entry entry = entries[i];
                        if (entry.Removed)
                            continue;

                        if (entry.Once)
                        {
                            entry.Removed = true;
                            dirty = true;
                        }

                        try
                        {
                            entry.Handler(evt);
                        }
                        catch (Exception ex)
                        {
                            GameLog.ErrorFormat(LogTag.EventBus, "处理事件 {0} 的订阅 {1} 时异常：{2}", EventType.Name, Describe(entry.Handler), ex);
                        }
                    }
                }
                finally
                {
                    dispatchDepth--;
                    if (dispatchDepth == 0 && dirty)
                        Compact();
                }
            }

            public void Discard()
            {
                entries.Clear();

                if (posted != null)
                    posted.Clear();

                dirty = false;
            }

            public void AppendDump(StringBuilder sb)
            {
                sb.Append("  ").Append(EventType.Name).Append("   ").Append(SubscriberCount).Append(" 个订阅");

                if (posted != null && posted.Count > 0)
                    sb.Append("，待派发 ").Append(posted.Count).Append(" 条");

                sb.Append('\n');

                for (int i = 0; i < entries.Count; i++)
                {
                    Entry entry = entries[i];
                    if (entry.Removed)
                        continue;

                    sb.Append("      [").Append(entry.Priority).Append("] ").Append(Describe(entry.Handler));
                    if (entry.Once)
                        sb.Append("  (once)");
                    sb.Append('\n');
                }
            }

            /// <summary>把已退订的条目真正删掉，并重排一次（派发中新增的订阅优先级可能没插对）。</summary>
            private void Compact()
            {
                dirty = false;

                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    if (entries[i].Removed)
                        entries.RemoveAt(i);
                }

                entries.Sort(Compare);
            }

            private static int Compare(Entry a, Entry b)
            {
                if (a.Priority != b.Priority)
                    return b.Priority.CompareTo(a.Priority);   // 优先级高的在前

                return a.Seq.CompareTo(b.Seq);                 // 同优先级按订阅先后
            }

            private static string Describe(Action<T> handler)
            {
                if (handler == null)
                    return "(null)";

                object target = handler.Target;
                string owner = target != null ? target.GetType().Name : "static";
                return owner + "." + handler.Method.Name;
            }
        }

        private readonly Dictionary<Type, IChannel> channels = new Dictionary<Type, IChannel>(64);
        private readonly List<IChannel> ordered = new List<IChannel>();
        private EventBusOptions options = new EventBusOptions();
        private bool shutdown;

        /// <summary>全局快捷访问。GameController 创建总线时会指过来，没有 GameController 的地方也能用。</summary>
        /// <summary>
        /// 全局总线。给「拿不到构造注入、又需要发/收事件」的框架代码用（例如本地化组件）。
        /// 由**持有者**负责登记：GameController 创建后设置，销毁时置回 null。业务代码不要改它。
        /// （拆 asmdef 前这里是 internal set，因为整个工程一个程序集；现在 Game 是独立程序集，必须放开）
        /// </summary>
        public static EventBus Global { get; set; }

        /// <summary>总线参数（是否打派发日志、单次 Tick 最大派发量）。</summary>
        public EventBusOptions Options
        {
            get { return options; }
            set { options = value != null ? value : new EventBusOptions(); }
        }

        public int SubscriberCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < ordered.Count; i++)
                    count += ordered[i].SubscriberCount;

                return count;
            }
        }

        /// <summary>当前有订阅或待派发事件的事件类型数量。</summary>
        public int ChannelCount { get { return ordered.Count; } }

        public IDisposable Subscribe<T>(Action<T> handler, int priority = 0) where T : IGameEvent
        {
            return AddSubscription(handler, priority, false);
        }

        public IDisposable SubscribeOnce<T>(Action<T> handler, int priority = 0) where T : IGameEvent
        {
            return AddSubscription(handler, priority, true);
        }

        public bool Unsubscribe<T>(Action<T> handler) where T : IGameEvent
        {
            if (handler == null)
                return false;

            Channel<T> channel = GetChannel<T>(false);
            return channel != null && channel.Unsubscribe(handler);
        }

        public void UnsubscribeAll<T>() where T : IGameEvent
        {
            Channel<T> channel = GetChannel<T>(false);
            if (channel != null)
                channel.UnsubscribeAll();
        }

        public void Publish<T>(T evt) where T : IGameEvent
        {
            Channel<T> channel = GetChannel<T>(false);
            if (channel == null)
                return;      // 没人订阅：直接丢掉，零开销

            channel.Dispatch(evt, options.LogPublish);
        }

        public void Post<T>(T evt) where T : IGameEvent
        {
            GetChannel<T>(true).Post(evt);
        }

        public void Tick(float deltaTime, float unscaledDeltaTime)
        {
            // 派发过程中可能新增通道（handler 里订阅了新类型的事件），本轮不处理它们
            int count = ordered.Count;
            for (int i = 0; i < count; i++)
                ordered[i].Flush(options.MaxEventsPerFlush, options.LogPublish);

            PruneEmptyChannels();
        }

        /// <summary>总线创建即可用；Shutdown 之后为 false。</summary>
        public bool IsInitialized { get { return !shutdown; } }

        /// <summary>关闭总线：丢开所有订阅和待派发事件。幂等。</summary>
        public void Shutdown()
        {
            if (shutdown)
                return;

            shutdown = true;
            Clear();
        }

        public void Clear()
        {
            for (int i = 0; i < ordered.Count; i++)
                ordered[i].Discard();

            ordered.Clear();
            channels.Clear();
        }

        public string Dump()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[EventBus] 通道 ").Append(ordered.Count)
              .Append(" 个，订阅 ").Append(SubscriberCount).Append(" 个\n");

            for (int i = 0; i < ordered.Count; i++)
                ordered[i].AppendDump(sb);

            return sb.ToString();
        }

        private IDisposable AddSubscription<T>(Action<T> handler, int priority, bool once) where T : IGameEvent
        {
            if (handler == null)
            {
                GameLog.ErrorFormat(LogTag.EventBus, "订阅 {0} 失败：handler 为 null。", typeof(T).Name);
                return null;
            }

            GetChannel<T>(true).Subscribe(handler, priority, once);
            return new Subscription<T>(this, handler);
        }

        private Channel<T> GetChannel<T>(bool create) where T : IGameEvent
        {
            IChannel channel;
            if (channels.TryGetValue(typeof(T), out channel))
                return (Channel<T>)channel;

            if (!create)
                return null;

            Channel<T> created = new Channel<T>();
            channels.Add(typeof(T), created);
            ordered.Add(created);
            return created;
        }

        /// <summary>回收「没订阅也没待派发事件」的通道，避免事件类型越用越多。</summary>
        private void PruneEmptyChannels()
        {
            for (int i = ordered.Count - 1; i >= 0; i--)
            {
                IChannel channel = ordered[i];
                if (!channel.IsEmpty)
                    continue;

                ordered.RemoveAt(i);
                channels.Remove(channel.EventType);
            }
        }
    }
}
