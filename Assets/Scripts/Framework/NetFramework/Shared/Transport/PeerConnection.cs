using System;
using System.Net.Sockets;
using System.Threading;
using GameFramework.Net.Protocol;
using GameFramework.Net.Router;

namespace GameFramework.Net.Transport
{
    /// <summary>
    /// 一条 TCP 连接（前后端共用同一份实现）：
    ///  - 后台接收线程负责 Receive → 拆包 → 交给 ProtocolRouter 派发；
    ///  - 发送用锁串行化，保证多线程同时发消息时不会把两条帧的字节交错在一起。
    /// 服务端为每个接入的客户端创建一个实例，客户端自己也持有一个实例。
    /// </summary>
    public sealed class PeerConnection : IMessagePeer, IDisposable
    {
        private readonly Socket _socket;
        private readonly INetLogger _logger;
        private readonly ProtocolRouter _router;
        private readonly FrameParser _parser = new FrameParser();
        private readonly object _sendLock = new object();
        private readonly ManualResetEventSlim _closedSignal = new ManualResetEventSlim(false);
        private readonly byte[] _receiveBuffer = new byte[NetConfig.DefaultIoBufferBytes];

        private Thread _receiveThread;
        private int _closedFlag;

        public PeerConnection(
            Socket socket,
            IMessageSerializer serializer,
            ProtocolRouter router,
            INetLogger logger,
            string peerId,
            object tag = null)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _router = router;
            _logger = logger ?? NullNetLogger.Instance;
            PeerId = peerId;
            Tag = tag;

            ConnectedAt = DateTime.UtcNow;
            try
            {
                RemoteEndPoint = _socket.RemoteEndPoint?.ToString() ?? "unknown";
            }
            catch (Exception)
            {
                RemoteEndPoint = "unknown";
            }
        }

        /// <summary>对端标识，服务端形如 "client-1"。</summary>
        public string PeerId { get; }

        public string RemoteEndPoint { get; }

        public DateTime ConnectedAt { get; }

        /// <summary>本端临时数据（服务端常用来挂玩家对象）。</summary>
        public object Tag { get; set; }

        public IMessageSerializer Serializer { get; }

        public bool IsConnected => _closedFlag == 0 && _socket.Connected;

        /// <summary>收到一条完整消息（在接收线程上触发，早于路由派发）。</summary>
        public event Action<PeerConnection, GameMessage> FrameReceived;

        /// <summary>发送了一条消息（在发送线程上触发，用于日志/统计）。</summary>
        public event Action<PeerConnection, GameMessage> FrameSent;

        /// <summary>连接关闭（在接收线程上触发），参数是关闭原因。</summary>
        public event Action<PeerConnection, string> Closed;

        /// <summary>启动接收线程。</summary>
        public void Start()
        {
            if (_receiveThread != null) throw new InvalidOperationException("PeerConnection 已经启动过");

            _socket.NoDelay = true; // 关闭 Nagle 算法，小包消息更实时
            try
            {
                _socket.SendTimeout = NetConfig.DefaultSendTimeoutMs;
            }
            catch (Exception)
            {
                // 某些平台不支持设置，忽略
            }

            _receiveThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "net-recv-" + PeerId
            };
            _receiveThread.Start();
        }

        public void Send(string protocol, object payload = null)
        {
            var body = payload == null ? new byte[0] : Serializer.Serialize(payload);
            SendRaw(new GameMessage(protocol, body));
        }

        public void SendRaw(GameMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (!IsConnected) throw new InvalidOperationException($"连接已关闭，无法发送 '{message.Protocol}'");

            var frame = FrameCodec.Encode(message.Protocol, message.Body);

            lock (_sendLock)
            {
                var sent = 0;
                while (sent < frame.Length)
                {
                    var n = _socket.Send(frame, sent, frame.Length - sent, SocketFlags.None);
                    if (n <= 0) throw new SocketException((int)SocketError.ConnectionReset);
                    sent += n;
                }
            }

            FrameSent?.Invoke(this, message);
        }

        /// <summary>发送失败不抛异常，只记日志并返回 false。</summary>
        public bool TrySend(string protocol, object payload = null)
        {
            try
            {
                Send(protocol, payload);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn($"[{PeerId}] 发送 '{protocol}' 失败：{ex.Message}");
                return false;
            }
        }

        public void Close(string reason = "local close")
        {
            CloseInternal(reason, true);
        }

        private void ReceiveLoop()
        {
            var reason = "远端关闭了连接";

            try
            {
                while (_closedFlag == 0)
                {
                    var read = _socket.Receive(_receiveBuffer, 0, _receiveBuffer.Length, SocketFlags.None);
                    if (read <= 0)
                    {
                        reason = "远端关闭了连接";
                        break;
                    }

                    var frames = _parser.Append(_receiveBuffer, 0, read);
                    for (var i = 0; i < frames.Count; i++)
                    {
                        var message = frames[i];
                        FrameReceived?.Invoke(this, message);
                        // 已经被同步等待者（SendRequest）消费的消息不用再走路由
                        if (!message.Consumed) _router?.Dispatch(message, this);
                    }
                }
            }
            catch (SocketException ex)
            {
                // 下面这些都属于“连接已经结束”，不是错误：
                //   ConnectionReset / ConnectionAborted / Shutdown —— 对端直接关掉 socket
                //   Interrupted —— 本端 Close 打断了正在阻塞的 Receive
                if (ex.SocketErrorCode == SocketError.ConnectionAborted
                    || ex.SocketErrorCode == SocketError.ConnectionReset
                    || ex.SocketErrorCode == SocketError.Shutdown
                    || ex.SocketErrorCode == SocketError.Interrupted)
                {
                    reason = "连接已结束";
                }
                else
                {
                    reason = $"Socket 异常：{ex.SocketErrorCode}";
                    _logger.Warn($"[{PeerId}] {reason}");
                }
            }
            catch (FrameFormatException ex)
            {
                reason = $"非法报文：{ex.Message}";
                _logger.Error($"[{PeerId}] {reason}");
            }
            catch (Exception ex)
            {
                reason = $"接收线程异常：{ex.Message}";
                _logger.Error($"[{PeerId}] {reason}", ex);
            }
            finally
            {
                CloseInternal(reason, true);
            }
        }

        private void CloseInternal(string reason, bool raiseEvent)
        {
            if (Interlocked.Exchange(ref _closedFlag, 1) != 0) return;

            try
            {
                if (_socket.Connected) _socket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception)
            {
                // 已经断开，忽略
            }

            try
            {
                _socket.Close();
            }
            catch (Exception)
            {
                // 忽略
            }

            _closedSignal.Set();

            if (raiseEvent)
            {
                try
                {
                    Closed?.Invoke(this, reason);
                }
                catch (Exception ex)
                {
                    _logger.Error($"[{PeerId}] Closed 事件处理异常", ex);
                }
            }
        }

        public void Dispose()
        {
            Close("dispose");
            _closedSignal.Dispose();
        }
    }
}
