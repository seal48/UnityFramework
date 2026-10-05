using System;
using System.Collections.Concurrent;
using GameFramework.Net.Protocol;
using GameFramework.Net.Threading;

namespace GameFramework.Net.Router
{
    /// <summary>协议处理函数：收到某条协议的消息时被调用。</summary>
    public delegate void ProtocolHandler(GameMessage message, IMessagePeer peer);

    /// <summary>
    /// 协议名 → 处理函数的注册表，前后端共用。
    /// 收到消息后按协议名称查表并派发；没注册的协议会走 Unhandled 事件（默认记日志）。
    /// </summary>
    public sealed class ProtocolRouter
    {
        private readonly ConcurrentDictionary<string, ProtocolHandler> _handlers =
            new ConcurrentDictionary<string, ProtocolHandler>(StringComparer.Ordinal);

        private readonly IDispatcher _dispatcher;
        private readonly INetLogger _logger;

        public ProtocolRouter(IDispatcher dispatcher = null, INetLogger logger = null)
        {
            _dispatcher = dispatcher ?? ImmediateDispatcher.Instance;
            _logger = logger ?? NullNetLogger.Instance;
        }

        /// <summary>没有注册处理函数的协议会触发这里（在 dispatcher 上执行）。</summary>
        public event Action<GameMessage, IMessagePeer> Unhandled;

        public bool Register(string protocol, ProtocolHandler handler)
        {
            if (string.IsNullOrEmpty(protocol)) throw new ArgumentException("协议名称不能为空", nameof(protocol));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return _handlers.TryAdd(protocol, handler);
        }

        /// <summary>用协议编号注册（推荐），等价于 Register(Protocols.NameOf(id), handler)。</summary>
        public bool Register(ProtocolId protocol, ProtocolHandler handler)
            => Register(Protocols.NameOf(protocol), handler);

        /// <summary>用协议编号注册（已存在则覆盖）。</summary>
        public void RegisterOrReplace(ProtocolId protocol, ProtocolHandler handler)
            => RegisterOrReplace(Protocols.NameOf(protocol), handler);

        /// <summary>注册（已存在则覆盖）。</summary>
        public void RegisterOrReplace(string protocol, ProtocolHandler handler)
        {
            if (string.IsNullOrEmpty(protocol)) throw new ArgumentException("协议名称不能为空", nameof(protocol));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers[protocol] = handler;
        }

        public bool Unregister(string protocol) => _handlers.TryRemove(protocol, out _);

        public bool Unregister(ProtocolId protocol) => Unregister(Protocols.NameOf(protocol));

        public bool IsRegistered(string protocol) => _handlers.ContainsKey(protocol);

        public bool IsRegistered(ProtocolId protocol) => IsRegistered(Protocols.NameOf(protocol));

        /// <summary>把消息派发给对应的处理函数。注意：实际执行的线程由 dispatcher 决定。</summary>
        public void Dispatch(GameMessage message, IMessagePeer peer)
        {
            if (message == null) return;

            if (_handlers.TryGetValue(message.Protocol, out var handler) && handler != null)
            {
                _dispatcher.Post(() =>
                {
                    try
                    {
                        handler(message, peer);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"协议 '{message.Protocol}' 的处理函数抛异常", ex);
                    }
                });
                return;
            }

            _dispatcher.Post(() =>
            {
                _logger.Warn($"收到未注册的协议：'{message.Protocol}'（来自 {peer?.PeerId ?? "?"}）");
                Unhandled?.Invoke(message, peer);
            });
        }
    }
}
