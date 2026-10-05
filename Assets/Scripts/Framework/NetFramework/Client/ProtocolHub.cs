using System;
using System.Collections.Generic;
using System.Text;
using GameFramework.Net.Protocol;
using GameFramework.Net.Router;

namespace GameFramework.Net.Client
{
    /// <summary>
    /// 协议分发中心：服务端推送进来的"中转站"。
    ///
    /// 分层位置：
    ///   网络层  PeerConnection → ProtocolRouter（按协议名查表）
    ///   → 本类 ProtocolHub（一个协议可以挂多个订阅者，负责拆给各个 System）
    ///   → 业务层 XxxSystem（校验 / 算数 / 写 Model）
    ///   → 事件总线 IGameEvent（界面只认逻辑事件，不认协议名）
    ///
    /// 为什么要有它（直接往 ProtocolRouter 上注册不行吗）：
    ///   1. ProtocolRouter 是单播：一条协议只能挂一个处理函数，后注册的会被拒。
    ///      而"一条推送要同时通知多个系统"是很常见的事，所以这里做多播。
    ///   2. ProtocolRouter 只给原始 GameMessage（byte[]），每个系统都要自己反序列化。
    ///      这里提供强类型订阅 Register&lt;T&gt;，业务代码拿到的就是对象。
    ///   3. GameClient 每次登录都会重建，Router 跟着换。
    ///      业务只需要在客户端重建时调一次 Bind(newRouter)，订阅关系不用重新登记。
    ///
    /// 线程：所有回调都在 IDispatcher 上执行（Unity 侧就是主线程），
    /// 因为 ProtocolRouter 已经负责把派发切回主线程了；本类不再额外线程切换。
    /// </summary>
    public sealed class ProtocolHub : IDisposable
    {
        /// <summary>原始推送处理函数：拿到还没反序列化的消息和来源连接。</summary>
        public delegate void PushHandler(GameMessage message, IMessagePeer peer);

        /// <summary>一条协议的订阅情况。</summary>
        private sealed class Entry
        {
            public readonly List<PushHandler> Handlers = new List<PushHandler>();
            public readonly List<string> Owners = new List<string>();
            public long ReceivedCount;
            public long LastReceivedAtMs;

            public bool IsEmpty
            {
                get
                {
                    for (int i = 0; i < Handlers.Count; i++)
                    {
                        if (Handlers[i] != null) return false;
                    }
                    return true;
                }
            }

            public int HandlerCount
            {
                get
                {
                    int count = 0;
                    for (int i = 0; i < Handlers.Count; i++)
                    {
                        if (Handlers[i] != null) count++;
                    }
                    return count;
                }
            }
        }

        /// <summary>退订句柄：Dispose 就是退订。</summary>
        private sealed class Binding : IDisposable
        {
            private ProtocolHub hub;
            private ProtocolId protocol;
            private PushHandler handler;

            public Binding(ProtocolHub hub, ProtocolId protocol, PushHandler handler)
            {
                this.hub = hub;
                this.protocol = protocol;
                this.handler = handler;
            }

            public void Dispose()
            {
                if (hub == null) return;

                hub.Remove(protocol, handler);
                hub = null;
                handler = null;
            }
        }

        private readonly Dictionary<ProtocolId, Entry> entries = new Dictionary<ProtocolId, Entry>();
        private readonly INetLogger logger;
        private ProtocolRouter router;
        private bool disposed;

        public ProtocolHub(INetLogger logger = null)
        {
            this.logger = logger ?? NullNetLogger.Instance;
        }

        /// <summary>把每条收到的推送打进日志（联调时打开，正式版关掉）。</summary>
        public bool LogPush { get; set; }

        /// <summary>载荷反序列化出来是 null 时，是否只是警告并跳过（false = 照样把 null 交给业务）。</summary>
        public bool SkipNullPayload { get; set; } = true;

        /// <summary>当前绑定的路由表；没绑定时为 null。</summary>
        public ProtocolRouter Router { get { return router; } }

        public bool IsBound { get { return router != null; } }

        /// <summary>累计收到多少条推送。</summary>
        public long ReceivedCount { get; private set; }

        /// <summary>有多少条协议被订阅了。</summary>
        public int ProtocolCount { get { return entries.Count; } }

        /// <summary>订阅总数。</summary>
        public int HandlerCount
        {
            get
            {
                int count = 0;
                foreach (var pair in entries) count += pair.Value.HandlerCount;
                return count;
            }
        }

        #region 绑定 / 解绑

        /// <summary>
        /// 绑定到某个协议的客户端路由表。GameClient 每次登录都会重建，
        /// 所以客户端重建后要重新 Bind 一次（订阅关系不用动，会自动挂到新路由上）。
        /// </summary>
        public void Bind(ProtocolRouter targetRouter)
        {
            if (disposed) throw new ObjectDisposedException(nameof(ProtocolHub));
            if (targetRouter == null) throw new ArgumentNullException(nameof(targetRouter));
            if (router == targetRouter) return;

            Unbind();
            router = targetRouter;

            int registered = 0;
            foreach (var pair in entries)
            {
                if (pair.Value.IsEmpty) continue;

                targetRouter.RegisterOrReplace(pair.Key, BuildRouterHandler(pair.Key));
                registered++;
            }

            if (registered > 0)
                logger.Info($"协议中心已绑定路由表，挂上 {registered} 条推送协议：{DescribeRegistered()}");
        }

        /// <summary>从当前路由表上摘掉所有订阅（换客户端 / 关闭时用）。</summary>
        public void Unbind()
        {
            if (router == null) return;

            foreach (var pair in entries)
                router.Unregister(pair.Key);

            router = null;
            logger.Info("协议中心已与路由表解绑");
        }

        #endregion

        #region 订阅

        /// <summary>
        /// 订阅一条协议（原始消息）。同一条协议可以订阅多次，都会收到。
        /// owner 只是写在日志/自检里的名字（一般传系统名），方便排查"这条协议谁在处理"。
        /// </summary>
        public IDisposable Register(ProtocolId protocol, PushHandler handler, string owner = null)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            Entry entry = GetOrCreate(protocol);
            entry.Handlers.Add(handler);
            entry.Owners.Add(string.IsNullOrEmpty(owner) ? "<anonymous>" : owner);

            if (router != null && !router.IsRegistered(protocol))
                router.RegisterOrReplace(protocol, BuildRouterHandler(protocol));

            return new Binding(this, protocol, handler);
        }

        /// <summary>
        /// 强类型订阅：协议内容会自动反序列化成 T 再交给 handler。
        /// 载荷为空（或反序列化失败）时按 SkipNullPayload 决定是跳过还是把 null 交给你。
        /// </summary>
        public IDisposable Register<T>(ProtocolId protocol, Action<T> handler, string owner = null)
            where T : class
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            return Register(protocol, delegate(GameMessage message, IMessagePeer peer)
            {
                T payload;
                try
                {
                    payload = message.Deserialize<T>(peer != null ? peer.Serializer : JsonMessageSerializer.Instance);
                }
                catch (Exception ex)
                {
                    logger.Error($"协议 '{message.Protocol}' 反序列化成 {typeof(T).Name} 失败", ex);
                    return;
                }

                if (payload == null && SkipNullPayload)
                {
                    logger.Warn($"协议 '{message.Protocol}' 的内容是空的，{typeof(T).Name} 没解出来，已跳过。");
                    return;
                }

                handler(payload);
            }, string.IsNullOrEmpty(owner) ? typeof(T).Name : owner);
        }

        /// <summary>退订一条协议上的某个处理函数。</summary>
        public bool Unregister(ProtocolId protocol, PushHandler handler)
        {
            if (handler == null) return false;
            return Remove(protocol, handler);
        }

        /// <summary>退掉一条协议上的全部订阅。</summary>
        public void UnregisterAll(ProtocolId protocol)
        {
            Entry entry;
            if (!entries.TryGetValue(protocol, out entry)) return;

            entry.Handlers.Clear();
            entry.Owners.Clear();
            if (router != null) router.Unregister(protocol);
        }

        public bool IsRegistered(ProtocolId protocol)
        {
            Entry entry;
            return entries.TryGetValue(protocol, out entry) && !entry.IsEmpty;
        }

        /// <summary>某条协议收到了多少条推送（调试用）。</summary>
        public long GetReceivedCount(ProtocolId protocol)
        {
            Entry entry;
            return entries.TryGetValue(protocol, out entry) ? entry.ReceivedCount : 0;
        }

        #endregion

        #region 派发

        private ProtocolHandler BuildRouterHandler(ProtocolId protocol)
        {
            return delegate(GameMessage message, IMessagePeer peer)
            {
                Dispatch(protocol, message, peer);
            };
        }

        private void Dispatch(ProtocolId protocol, GameMessage message, IMessagePeer peer)
        {
            Entry entry;
            if (!entries.TryGetValue(protocol, out entry)) return;

            ReceivedCount++;
            entry.ReceivedCount++;
            entry.LastReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (LogPush)
                logger.Info($"收到推送 '{message.Protocol}'（{message.BodyLength} 字节）：{message.BodyAsText()}");

            // 先快照：处理函数里可能订阅 / 退订（改不动这一轮要派发的名单）
            PushHandler[] snapshot = entry.Handlers.ToArray();

            for (int i = 0; i < snapshot.Length; i++)
            {
                PushHandler handler = snapshot[i];
                if (handler == null) continue;

                try
                {
                    handler(message, peer);
                }
                catch (Exception ex)
                {
                    // 一条推送的某个系统炸了，不能影响别的系统，更不能把接收线程带崩
                    logger.Error($"协议 '{message.Protocol}' 的订阅者抛异常（{DescribeOwner(entry, handler)}）", ex);
                }
            }
        }

        private bool Remove(ProtocolId protocol, PushHandler handler)
        {
            Entry entry;
            if (!entries.TryGetValue(protocol, out entry)) return false;

            int index = entry.Handlers.IndexOf(handler);
            if (index < 0) return false;

            entry.Handlers[index] = null;
            entry.Owners[index] = null;

            // 这条协议已经没人听了，从路由表摘掉，免得还占着一个"已注册"的位置
            if (entry.IsEmpty && router != null)
                router.Unregister(protocol);

            return true;
        }

        private Entry GetOrCreate(ProtocolId protocol)
        {
            Entry entry;
            if (entries.TryGetValue(protocol, out entry)) return entry;

            entry = new Entry();
            entries.Add(protocol, entry);
            return entry;
        }

        private static string DescribeOwner(Entry entry, PushHandler handler)
        {
            int index = entry.Handlers.IndexOf(handler);
            if (index < 0 || index >= entry.Owners.Count) return "unknown";
            return entry.Owners[index] ?? "unknown";
        }

        /// <summary>已订阅的协议名（逗号分隔），日志 / 自检用。</summary>
        public string DescribeRegistered()
        {
            StringBuilder sb = new StringBuilder();
            foreach (var pair in entries)
            {
                if (pair.Value.IsEmpty) continue;

                if (sb.Length > 0) sb.Append(", ");
                sb.Append(Protocols.NameOf(pair.Key));

                string owners = DescribeOwners(pair.Value);
                if (!string.IsNullOrEmpty(owners)) sb.Append('(').Append(owners).Append(')');
            }

            return sb.Length == 0 ? "（没有订阅任何协议）" : sb.ToString();
        }

        private static string DescribeOwners(Entry entry)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < entry.Owners.Count; i++)
            {
                string owner = entry.Owners[i];
                if (string.IsNullOrEmpty(owner)) continue;

                if (sb.Length > 0) sb.Append('/');
                sb.Append(owner);
            }
            return sb.ToString();
        }

        /// <summary>导出当前订阅表，查"这条协议到底谁在处理"用。</summary>
        public string Dump()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[ProtocolHub] 协议 ").Append(ProtocolCount)
              .Append(" 条，订阅 ").Append(HandlerCount)
              .Append(" 个，累计收到推送 ").Append(ReceivedCount)
              .Append(" 条，路由=").Append(router != null ? "已绑定" : "未绑定").Append('\n');

            foreach (var pair in entries)
            {
                sb.Append("  - ").Append(Protocols.NameOf(pair.Key))
                  .Append("  订阅 ").Append(pair.Value.HandlerCount)
                  .Append("  收到 ").Append(pair.Value.ReceivedCount)
                  .Append("  处理者=").Append(DescribeOwners(pair.Value))
                  .Append('\n');
            }

            return sb.ToString();
        }

        #endregion

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            Unbind();
            entries.Clear();
        }
    }
}
