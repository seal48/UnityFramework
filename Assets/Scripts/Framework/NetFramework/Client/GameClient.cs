using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using GameFramework.Net.Protocol;
using GameFramework.Net.Router;
using GameFramework.Net.Threading;
using GameFramework.Net.Transport;

namespace GameFramework.Net.Client
{
    /// <summary>
    /// TCP 客户端（前端）。
    /// 用法：
    ///   var client = new GameClient();
    ///   var response = client.Login("127.0.0.1", 7777, "dev", "dev");   // 连接 + 登录，成功后自动开始心跳
    ///   if (response.Success) client.Send(ProtocolId.PlayerMove, new MoveRequest { X = 1 });
    /// </summary>
    public sealed class GameClient : IMessagePeer, IDisposable
    {
        private readonly INetLogger _logger;
        private readonly IDispatcher _dispatcher;
        private readonly ConcurrentDictionary<string, ReplyBox> _replyWaiters =
            new ConcurrentDictionary<string, ReplyBox>(StringComparer.Ordinal);

        private PeerConnection _connection;

        private Thread _heartbeatThread;
        private ManualResetEventSlim _heartbeatStop;
        private long _heartbeatSentAtMs;      // 0 = 当前没有等待回包的心跳
        private long _lastHeartbeatSentAtMs;
        private int _heartbeatSequence;
        private int _heartbeatAckCount;

        public GameClient(
            IMessageSerializer serializer = null,
            INetLogger logger = null,
            IDispatcher dispatcher = null,
            ProtocolRouter router = null)
        {
            Serializer = serializer ?? JsonMessageSerializer.Instance;
            _logger = logger ?? new ConsoleNetLogger("CLIENT");
            _dispatcher = dispatcher ?? ImmediateDispatcher.Instance;
            Router = router ?? new ProtocolRouter(_dispatcher, _logger);
        }

        public IMessageSerializer Serializer { get; }

        public INetLogger Logger => _logger;

        /// <summary>协议路由表，注册服务器回包的处理函数。</summary>
        public ProtocolRouter Router { get; }

        public string PeerId => _connection?.PeerId ?? "server";

        public object Tag { get; set; }

        public bool IsConnected => _connection != null && _connection.IsConnected;

        public string ServerEndPoint => _connection?.RemoteEndPoint;

        // ---------------- 心跳 ----------------

        /// <summary>是否自动心跳（连接成功后立刻发第一条，之后每 Interval 一条）。</summary>
        public bool HeartbeatEnabled { get; set; } = true;

        /// <summary>心跳间隔，默认 5000ms。</summary>
        public int HeartbeatIntervalMs { get; set; } = NetConfig.DefaultHeartbeatIntervalMs;

        /// <summary>心跳回包超时，默认 5000ms；超过就判定连接超时并主动断开。</summary>
        public int HeartbeatTimeoutMs { get; set; } = NetConfig.DefaultHeartbeatTimeoutMs;

        /// <summary>最近一次心跳的往返耗时（毫秒）。</summary>
        public long LastHeartbeatRoundTripMs { get; private set; }

        /// <summary>累计收到多少次心跳回包。</summary>
        public int HeartbeatAckCount => _heartbeatAckCount;

        /// <summary>心跳线程是否在跑。</summary>
        public bool IsHeartbeatRunning => _heartbeatThread != null;

        /// <summary>收到心跳回包（在 dispatcher 上触发，Unity 侧即主线程），参数是往返毫秒。</summary>
        public event Action<GameClient, long> HeartbeatReceived;

        /// <summary>心跳超时：参数是超时说明。触发后会立刻主动断开连接。</summary>
        public event Action<GameClient, string> ConnectionTimedOut;

        /// <summary>连接成功（在调用 Connect 的线程上触发）。</summary>
        public event Action<GameClient> Connected;

        /// <summary>连接断开，参数是原因（在接收线程上触发）。</summary>
        public event Action<GameClient, string> Disconnected;

        /// <summary>收到一条完整消息（在接收线程上触发，早于路由派发）。</summary>
        public event Action<GameClient, GameMessage> FrameReceived;

        /// <summary>连接过程中出错。</summary>
        public event Action<GameClient, Exception> Error;

        /// <summary>连接服务器。阻塞直到连接成功或超时。</summary>
        public void Connect(string host = NetConfig.DefaultHost, int port = NetConfig.DefaultPort,
            int timeoutMs = NetConfig.DefaultConnectTimeoutMs)
        {
            if (IsConnected) return;

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                var async = socket.BeginConnect(host, port, null, null);
                if (!async.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    throw new TimeoutException($"连接 {host}:{port} 超时（{timeoutMs}ms）");
                }

                socket.EndConnect(async);
            }
            catch (Exception ex)
            {
                try
                {
                    socket.Close();
                }
                catch (Exception)
                {
                    // 忽略
                }

                Error?.Invoke(this, ex);
                throw;
            }

            var connection = new PeerConnection(socket, Serializer, Router, _logger, "server");
            connection.FrameReceived += OnFrameReceived;
            connection.Closed += OnConnectionClosed;
            _connection = connection;
            connection.Start();

            _logger.Info($"已连接到服务器 {host}:{port}（内容格式={Serializer.Name}）");
            Connected?.Invoke(this);
        }

        // ---------------- 登录 ----------------

        /// <summary>是否已经登录成功（只有登录成功后才会开始心跳）。</summary>
        public bool IsLoggedIn { get; private set; }

        /// <summary>登录结果（成功时含 playerId / sessionId）。</summary>
        public LoginResponse Session { get; private set; }

        public string PlayerId => Session?.PlayerId;

        /// <summary>
        /// 登录：先建立 TCP 连接，再发 login.request，拿到 login.response 才算成功；
        /// 成功后才开始心跳。失败会关掉连接并把结果返回给你（不会抛异常）。
        /// </summary>
        public LoginResponse Login(string host, int port, string account, string password,
            int timeoutMs = NetConfig.DefaultRequestTimeoutMs,
            int connectTimeoutMs = NetConfig.DefaultConnectTimeoutMs)
        {
            if (IsConnected) throw new InvalidOperationException("已经连接上了，重复登录请先 Close()");

            // 和服务器同一套规范化：全角数字、首尾空格在这里就修正
            account = AccountNameRules.Normalize(account);

            Connect(host, port, connectTimeoutMs);

            try
            {
                var response = SendRequest<LoginRequest, LoginResponse>(
                    ProtocolId.LoginRequest,
                    new LoginRequest
                    {
                        Account = account,
                        Password = password,
                        ClientVersion = ClientVersion,
                        ClientPlatform = ClientPlatform,
                        ClientTimeMs = NowMs()
                    },
                    ProtocolId.LoginResponse,
                    timeoutMs);

                if (response == null || !response.Success)
                {
                    var reason = response?.Message ?? response?.Reason ?? "登录失败";
                    _logger.Warn($"登录失败：{reason}");
                    Close("登录失败：" + reason);
                    return response;
                }

                Session = response;
                IsLoggedIn = true;
                _logger.Info(
                    $"登录成功：账号='{account}'，playerId='{response.PlayerId}'，sessionId='{response.SessionId}'，服务器='{response.ServerName}'");

                // 登录成功之后才开始心跳检测
                StartHeartbeat();
                return response;
            }
            catch (Exception)
            {
                if (!IsLoggedIn) Close("登录失败");
                throw;
            }
        }

        /// <summary>登录（用 NetConfig 里的默认地址）。</summary>
        public LoginResponse Login(string account, string password,
            int timeoutMs = NetConfig.DefaultRequestTimeoutMs)
            => Login(NetConfig.DefaultHost, NetConfig.DefaultPort, account, password, timeoutMs);

        // ---------------- 注册 ----------------

        /// <summary>
        /// 注册账号：连服务器 → 发 register.request → 收 register.response。
        /// 注册成功只是账号建好了，还没有登录态；要进游戏再调 Login()（或者直接用 RegisterAndLogin）。
        /// 注册完会断开这条连接。
        /// </summary>
        public RegisterResponse Register(string host, int port, string account, string password,
            string playerName = null,
            int timeoutMs = NetConfig.DefaultRequestTimeoutMs,
            int connectTimeoutMs = NetConfig.DefaultConnectTimeoutMs)
        {
            if (IsConnected) throw new InvalidOperationException("已经连接上了，注册请先 Close()");

            account = AccountNameRules.Normalize(account);

            Connect(host, port, connectTimeoutMs);

            try
            {
                var response = SendRequest<RegisterRequest, RegisterResponse>(
                    ProtocolId.RegisterRequest,
                    new RegisterRequest
                    {
                        Account = account,
                        Password = password,
                        PlayerName = playerName,
                        ClientVersion = ClientVersion,
                        ClientPlatform = ClientPlatform,
                        ClientTimeMs = NowMs()
                    },
                    ProtocolId.RegisterResponse,
                    timeoutMs);

                if (response == null || !response.Success)
                    _logger.Warn($"注册失败：{response?.Message ?? response?.Reason ?? "没有收到注册回包"}");
                else
                    _logger.Info($"注册成功：账号='{account}'，playerId='{response.PlayerId}'");

                return response;
            }
            finally
            {
                Close("注册结束");
            }
        }

        /// <summary>注册（用 NetConfig 里的默认地址）。</summary>
        public RegisterResponse Register(string account, string password, string playerName = null,
            int timeoutMs = NetConfig.DefaultRequestTimeoutMs)
            => Register(NetConfig.DefaultHost, NetConfig.DefaultPort, account, password, playerName, timeoutMs);

        /// <summary>
        /// 注册并登录（一站式）。
        /// 注册只是"顺手试一下"：账号已存在（或历史上用更宽松规则建的老账号）都会继续走登录，
        /// 最终以登录结果为准；只有登录也失败、且注册失败的原因是密码/账号不合规时，才把注册的原因报出来。
        /// 所以这个方法可以重复调用（第一次注册，之后登录）。
        /// </summary>
        public LoginResponse RegisterAndLogin(string host, int port, string account, string password,
            string playerName = null, int timeoutMs = NetConfig.DefaultRequestTimeoutMs)
        {
            var register = Register(host, port, account, password, playerName, timeoutMs);

            if (register == null || !register.Success)
            {
                _logger.Info($"注册未成功（{register?.Reason}：{register?.Message}），继续尝试登录…");
            }

            var login = Login(host, port, account, password, timeoutMs);
            if (login != null && login.Success) return login;

            // 登录也失败：注册失败是规则问题（账号/密码不合规）时，那个提示更有用
            if (register != null && !register.Success && register.Reason != RegisterReasons.AccountExists)
            {
                return new LoginResponse
                {
                    Success = false,
                    Reason = register.Reason,
                    Message = register.Message
                };
            }

            return login;
        }

        /// <summary>注册并登录（用 NetConfig 里的默认地址）。</summary>
        public LoginResponse RegisterAndLogin(string account, string password, string playerName = null,
            int timeoutMs = NetConfig.DefaultRequestTimeoutMs)
            => RegisterAndLogin(NetConfig.DefaultHost, NetConfig.DefaultPort, account, password, playerName, timeoutMs);

        /// <summary>客户端版本号/平台，登录请求里会带上。</summary>
        public string ClientVersion { get; set; } = "1.0.0";

        public string ClientPlatform { get; set; } = "unknown";

        public void Send(string protocol, object payload = null)
        {
            RequireConnection().Send(protocol, payload);
        }

        /// <summary>用协议编号发送（推荐），等价于 Send(Protocols.NameOf(id), payload)。</summary>
        public void Send(ProtocolId protocol, object payload = null)
            => Send(Protocols.NameOf(protocol), payload);

        public void SendRaw(GameMessage message)
        {
            RequireConnection().SendRaw(message);
        }

        /// <summary>
        /// 请求/响应：发出 protocol，阻塞等待 expectedReplyProtocol 的回包。
        /// 注意：这是简化实现——同一时刻同一个回复协议只支持一个等待者，
        /// 高并发场景应该在消息内容里带请求 id 做关联。
        /// </summary>
        public GameMessage SendRequest(string protocol, object payload, string expectedReplyProtocol,
            int timeoutMs = NetConfig.DefaultRequestTimeoutMs)
        {
            if (string.IsNullOrEmpty(expectedReplyProtocol))
                throw new ArgumentException("必须指定期望的回复协议", nameof(expectedReplyProtocol));

            var box = new ReplyBox();
            _replyWaiters[expectedReplyProtocol] = box;
            try
            {
                Send(protocol, payload);
                if (!box.Signal.Wait(timeoutMs))
                    throw new TimeoutException($"等待 '{expectedReplyProtocol}' 回包超时（{timeoutMs}ms）");

                return box.Message;
            }
            finally
            {
                _replyWaiters.TryRemove(expectedReplyProtocol, out _);
            }
        }

        /// <summary>请求/响应（带内容反序列化）。</summary>
        public TResponse SendRequest<TRequest, TResponse>(string protocol, TRequest payload,
            string expectedReplyProtocol, int timeoutMs = NetConfig.DefaultRequestTimeoutMs)
        {
            var reply = SendRequest(protocol, payload, expectedReplyProtocol, timeoutMs);
            return reply.Deserialize<TResponse>(Serializer);
        }

        /// <summary>请求/响应（用协议编号，推荐）。</summary>
        public GameMessage SendRequest(ProtocolId protocol, object payload, ProtocolId expectedReplyProtocol,
            int timeoutMs = NetConfig.DefaultRequestTimeoutMs)
            => SendRequest(Protocols.NameOf(protocol), payload, Protocols.NameOf(expectedReplyProtocol), timeoutMs);

        /// <summary>请求/响应（用协议编号，带内容反序列化）。</summary>
        public TResponse SendRequest<TRequest, TResponse>(ProtocolId protocol, TRequest payload,
            ProtocolId expectedReplyProtocol, int timeoutMs = NetConfig.DefaultRequestTimeoutMs)
            => SendRequest<TRequest, TResponse>(
                Protocols.NameOf(protocol), payload, Protocols.NameOf(expectedReplyProtocol), timeoutMs);

        /// <summary>发一条 system.ping 并等待 system.pong，返回往返耗时（毫秒）。</summary>
        public long Ping(int timeoutMs = NetConfig.DefaultRequestTimeoutMs)
        {
            var sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var pong = SendRequest<PingRequest, PongResponse>(
                ProtocolId.SystemPing,
                new PingRequest { ClientTimeMs = sentAt },
                ProtocolId.SystemPong,
                timeoutMs);

            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (pong?.ClientTimeMs ?? sentAt);
        }

        public void Close(string reason = "客户端主动关闭")
        {
            _connection?.Close(reason);
        }

        /// <summary>立刻补发一条心跳（一般不用手动调，心跳线程会自己发）。</summary>
        public void SendHeartbeatNow() => SendHeartbeat();

        private void OnFrameReceived(PeerConnection connection, GameMessage message)
        {
            FrameReceived?.Invoke(this, message);

            // 心跳回包在接收线程上直接处理：不计入路由，也不受主线程是否在跑影响
            if (message.Id == ProtocolId.SystemHeartbeatAck)
            {
                message.Consumed = true;
                HandleHeartbeatAck();
                return;
            }

            // 有同步等待者在等这条回包，就唤醒它
            if (_replyWaiters.TryGetValue(message.Protocol, out var box))
            {
                box.Message = message;
                message.Consumed = true;
                box.Signal.Set();
            }
        }

        private void OnConnectionClosed(PeerConnection connection, string reason)
        {
            StopHeartbeat();
            IsLoggedIn = false;
            _connection = null;
            _logger.Info($"与服务器的连接已断开（{reason}）");
            _dispatcher.Post(() => Disconnected?.Invoke(this, reason));
        }

        // ---------------- 心跳实现 ----------------

        private void StartHeartbeat()
        {
            if (!HeartbeatEnabled) return;
            if (_heartbeatThread != null) return;

            _heartbeatStop = new ManualResetEventSlim(false);
            Interlocked.Exchange(ref _heartbeatSentAtMs, 0);
            Interlocked.Exchange(ref _lastHeartbeatSentAtMs, 0);

            SendHeartbeat();

            _heartbeatThread = new Thread(HeartbeatLoop)
            {
                IsBackground = true,
                Name = "net-heartbeat"
            };
            _heartbeatThread.Start(_heartbeatStop);
        }

        private void StopHeartbeat()
        {
            var stop = _heartbeatStop;
            _heartbeatStop = null;
            _heartbeatThread = null;
            stop?.Set();
        }

        private void HeartbeatLoop(object state)
        {
            var stop = (ManualResetEventSlim)state;

            var interval = Math.Max(100, HeartbeatIntervalMs);
            var timeout = Math.Max(100, HeartbeatTimeoutMs);
            // 检测频率取间隔/超时的 1/5，保证"超时"这件事不会被拖太久才发现
            var checkMs = Math.Max(50, Math.Min(interval, timeout) / 5);

            while (!stop.Wait(checkMs))
            {
                try
                {
                    var now = NowMs();
                    var pendingSince = Interlocked.Read(ref _heartbeatSentAtMs);

                    // 超过阈值还没等到回包 → 判定连接超时
                    if (pendingSince > 0 && now - pendingSince >= timeout)
                    {
                        OnHeartbeatTimeout(now - pendingSince);
                        return;
                    }

                    // 到点就发下一条心跳
                    if (now - Interlocked.Read(ref _lastHeartbeatSentAtMs) >= interval)
                    {
                        SendHeartbeat();
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error("心跳线程异常", ex);
                }
            }
        }

        private void SendHeartbeat()
        {
            if (!IsConnected) return;

            var now = NowMs();
            var sequence = Interlocked.Increment(ref _heartbeatSequence);

            try
            {
                Interlocked.Exchange(ref _lastHeartbeatSentAtMs, now);
                Interlocked.Exchange(ref _heartbeatSentAtMs, now);

                Send(ProtocolId.SystemHeartbeat, new HeartbeatMessage
                {
                    Sequence = sequence,
                    ClientTimeMs = now
                });
            }
            catch (Exception ex)
            {
                _logger.Warn($"发送心跳（#{sequence}）失败：{ex.Message}");
            }
        }

        private void HandleHeartbeatAck()
        {
            var sentAt = Interlocked.Exchange(ref _heartbeatSentAtMs, 0);
            if (sentAt <= 0) return;

            var rtt = NowMs() - sentAt;
            LastHeartbeatRoundTripMs = rtt;
            Interlocked.Increment(ref _heartbeatAckCount);

            _dispatcher.Post(() => HeartbeatReceived?.Invoke(this, rtt));
        }

        private void OnHeartbeatTimeout(long waitedMs)
        {
            var text = $"连接超时：{waitedMs}ms 没有收到心跳回包"
                       + $"（心跳间隔 {HeartbeatIntervalMs}ms，超时阈值 {HeartbeatTimeoutMs}ms）";

            _logger.Error(text);

            // 顺手告诉服务器一声（服务器还活着就能记一笔；发不出去就算了）
            try
            {
                if (IsConnected) Send(ProtocolId.SystemBye, new ByeMessage { Reason = text });
            }
            catch (Exception)
            {
                // 连接有问题时这条消息本来就发不出去，忽略
            }

            _dispatcher.Post(() => ConnectionTimedOut?.Invoke(this, text));

            // 主动断开
            Close(text);
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private PeerConnection RequireConnection()
        {
            var connection = _connection;
            if (connection == null || !connection.IsConnected)
                throw new InvalidOperationException("尚未连接服务器，或连接已断开");
            return connection;
        }

        public void Dispose()
        {
            StopHeartbeat();
            Close("dispose");
        }

        private sealed class ReplyBox
        {
            public readonly ManualResetEventSlim Signal = new ManualResetEventSlim(false);
            public GameMessage Message;
        }
    }
}
