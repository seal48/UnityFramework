using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using GameFramework.Net.Protocol;
using GameFramework.Net.Router;
using GameFramework.Net.Server.Persistence;
using GameFramework.Net.Threading;
using GameFramework.Net.Transport;

namespace GameFramework.Net.Server
{
    /// <summary>
    /// TCP 游戏服务器。
    /// 用法：
    ///   var server = new GameServer(new GameServerOptions { Port = 7777 });
    ///   server.Start();   // 立刻返回，后台线程负责 accept
    ///   server.Broadcast(ProtocolId.PlayerMove, new MoveRequest { X = 1 });
    ///   server.Stop();
    /// </summary>
    public sealed class GameServer : IDisposable
    {
        private readonly GameServerOptions _options;
        private readonly INetLogger _logger;
        private readonly List<PeerConnection> _clients = new List<PeerConnection>();
        private readonly object _clientsLock = new object();

        private TcpListener _listener;
        private Thread _acceptThread;
        private ServerTimerService _timers;
        private int _peerCounter;
        private volatile bool _running;
        private readonly bool _ownsAccountStore;

        public GameServer(GameServerOptions options = null)
        {
            _options = options ?? new GameServerOptions();
            _logger = _options.Logger ?? new ConsoleNetLogger("SERVER");
            Serializer = _options.Serializer ?? JsonMessageSerializer.Instance;
            Router = new ProtocolRouter(_options.Dispatcher ?? ImmediateDispatcher.Instance, _logger);
            _ownsAccountStore = _options.AccountStore == null;

            if (_options.RegisterBuiltinProtocols)
            {
                SystemProtocols.Register(this);

                // 默认用账号存储做登录校验；也可以自己实现 ILoginValidator 接外部鉴权
                Accounts = new AccountService(
                    _options.AccountStore ?? new JsonAccountStore(_options.AccountStorePath, _logger),
                    _logger)
                {
                    AutoRegisterOnLogin = _options.AutoRegisterOnLogin
                };

                LoginProtocols.Register(this, _options.LoginValidator ?? Accounts);
                RegisterProtocols.Register(this, Accounts);

                if (Accounts.List(1).Count == 0)
                    _logger.Warn("账号库里还没有任何账号，登录都会失败；用 server.AddAccount(账号, 密码) 或 AccountService.Register 添加。");
            }
        }

        /// <summary>协议路由表，注册自己的处理函数：server.Router.Register("player.move", ...)。</summary>
        public ProtocolRouter Router { get; }

        public IMessageSerializer Serializer { get; }

        public GameServerOptions Options => _options;

        public string ServerName => _options.ServerName;

        /// <summary>账号与玩家数据（等级/属性/道具）的业务层。RegisterBuiltinProtocols=false 时为 null。</summary>
        public AccountService Accounts { get; private set; }

        public INetLogger Logger => _logger;

        /// <summary>服务端定时线程：定时推送 / 冷却 / 超时都挂在它上面。Start() 之后可用。</summary>
        public ServerTimerService Timers => _timers;

        public bool IsRunning => _running;

        /// <summary>实际监听的端口（Port 填 0 时由系统分配）。</summary>
        public int Port { get; private set; }

        /// <summary>当前连接的客户端快照。</summary>
        public IReadOnlyList<PeerConnection> Clients
        {
            get
            {
                lock (_clientsLock) return _clients.ToArray();
            }
        }

        public int ClientCount
        {
            get
            {
                lock (_clientsLock) return _clients.Count;
            }
        }

        /// <summary>有新客户端接入（在 accept 线程上触发）。</summary>
        public event Action<PeerConnection> ClientConnected;

        /// <summary>有客户端断开（在对方连接的接收线程上触发）。</summary>
        public event Action<PeerConnection> ClientDisconnected;

        /// <summary>
        /// 某个连接登录成功了（login.response 已经发出去之后才触发）。
        /// 业务侧在这里下发初始数据 —— 比如把玩家全量数据推给刚上线的客户端。
        /// </summary>
        public event Action<IMessagePeer> ClientLoggedIn;

        /// <summary>启动服务器（非阻塞）。</summary>
        public void Start()
        {
            if (_running) return;

            var address = ResolveAddress(_options.Host);
            _listener = new TcpListener(address, _options.Port);

            try
            {
                _listener.Start(_options.Backlog);
            }
            catch (SocketException ex)
            {
                _listener = null;
                throw new InvalidOperationException(
                    $"端口 {_options.Port} 无法监听（{ex.SocketErrorCode}）：{ex.Message}。" +
                    "常见原因是该端口已被另一个程序占用（比如上一次启动的服务器还在跑）。", ex);
            }

            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;

            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "net-accept"
            };
            _acceptThread.Start();

            // 定时线程：服务端没有帧，定时逻辑单独起一条线程驱动
            _timers = new ServerTimerService(_logger, _options.TimerIntervalMs);
            _timers.Start();

            _logger.Info($"服务器已启动：{_options.Host}:{Port}（内容格式={Serializer.Name}，最大连接={_options.MaxConnections}）");
        }

        /// <summary>启动失败不抛异常，把原因放进 error。</summary>
        public bool TryStart(out string error)
        {
            try
            {
                Start();
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>停止服务器并断开所有客户端。</summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;

            try
            {
                _listener?.Stop();
            }
            catch (Exception)
            {
                // 忽略
            }

            _listener = null;

            foreach (var client in Clients)
                client.Close("服务器已关闭");

            lock (_clientsLock) _clients.Clear();

            if (_timers != null)
            {
                _timers.Dispose();
                _timers = null;
            }

            _logger.Info("服务器已停止");
        }

        /// <summary>给所有（或满足条件的）客户端发消息。</summary>
        public void Broadcast(string protocol, object payload = null, Func<PeerConnection, bool> filter = null)
        {
            foreach (var client in Clients)
            {
                if (filter != null && !filter(client)) continue;
                client.TrySend(protocol, payload);
            }
        }

        /// <summary>用协议编号广播（推荐）。</summary>
        public void Broadcast(ProtocolId protocol, object payload = null, Func<PeerConnection, bool> filter = null)
            => Broadcast(Protocols.NameOf(protocol), payload, filter);

        /// <summary>添加一个账号（写进当前账号存储）。</summary>
        public GameServer AddAccount(string account, string password, string playerId = null)
        {
            if (Accounts == null) throw new InvalidOperationException("服务器没有启用内置协议，无法管理账号");

            if (!Accounts.Register(account, password, out var error, playerId))
                _logger.Warn($"添加账号失败：{error}（account='{account}'）");

            return this;
        }

        /// <summary>当前已登录的客户端。</summary>
        public List<PeerConnection> LoggedInClients
        {
            get
            {
                var result = new List<PeerConnection>();
                foreach (var client in Clients)
                {
                    if (PeerSession.IsLoggedIn(client)) result.Add(client);
                }
                return result;
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                Socket socket = null;
                try
                {
                    socket = _listener.AcceptSocket();
                }
                catch (Exception)
                {
                    // Stop() 会关掉 listener，这里正常退出
                    if (!_running) break;
                    continue;
                }

                if (socket == null) continue;

                if (ClientCount >= _options.MaxConnections)
                {
                    _logger.Warn("连接数已达上限，拒绝新连接");
                    try
                    {
                        socket.Close();
                    }
                    catch (Exception)
                    {
                        // 忽略
                    }
                    continue;
                }

                RegisterClient(socket);
            }
        }

        private void RegisterClient(Socket socket)
        {
            var id = $"client-{Interlocked.Increment(ref _peerCounter)}";
            var connection = new PeerConnection(socket, Serializer, Router, _logger, id);

            connection.FrameReceived += OnClientFrameReceived;
            connection.FrameSent += OnClientFrameSent;
            connection.Closed += OnClientClosed;

            lock (_clientsLock) _clients.Add(connection);

            _logger.Info($"[{id}] 客户端已接入：{connection.RemoteEndPoint}（当前在线 {ClientCount}）");

            connection.Start();
            ClientConnected?.Invoke(connection);
        }

        private void OnClientFrameReceived(PeerConnection connection, GameMessage message)
        {
            // 未登录的连接只能发登录/告别消息，其它协议一律拦下来
            if (_options.RequireLogin
                && !PeerSession.IsLoggedIn(connection)
                && (_options.PreLoginProtocols == null || !_options.PreLoginProtocols.Contains(message.Id)))
            {
                _logger.Warn($"[{connection.PeerId}] 未登录就发了 '{message.Protocol}'，已丢弃");
                message.Consumed = true;   // 标记成已处理，路由层就不会再派发
                return;
            }

            if (_options.LogReceivedFrames && !IsFrameLogExcluded(message.Protocol))
                _logger.Info($"[{connection.PeerId}] 收到 '{message.Protocol}'，内容 {message.BodyLength} 字节：{message.BodyAsText()}");
        }

        private void OnClientFrameSent(PeerConnection connection, GameMessage message)
        {
            if (_options.LogSentFrames && !IsFrameLogExcluded(message.Protocol))
                _logger.Info($"[{connection.PeerId}] 发送 '{message.Protocol}'，内容 {message.BodyLength} 字节");
        }

        private bool IsFrameLogExcluded(string protocol)
            => _options.FrameLogExclusions != null && _options.FrameLogExclusions.Contains(protocol);

        private void OnClientClosed(PeerConnection connection, string reason)
        {
            lock (_clientsLock) _clients.Remove(connection);
            _logger.Info($"[{connection.PeerId}] 连接关闭（{reason}），当前在线 {ClientCount}");
            ClientDisconnected?.Invoke(connection);
        }

        /// <summary>触发 ClientLoggedIn。登录协议在回包发完之后调它。</summary>
        internal void RaiseClientLoggedIn(IMessagePeer peer)
        {
            if (peer == null) return;

            try
            {
                ClientLoggedIn?.Invoke(peer);
            }
            catch (Exception ex)
            {
                _logger.Error($"[{peer.PeerId}] 登录成功钩子抛异常", ex);
            }
        }

        private static IPAddress ResolveAddress(string host)
        {
            if (string.IsNullOrWhiteSpace(host) || host == "0.0.0.0" || host == "*")
                return IPAddress.Any;
            if (host == "localhost")
                return IPAddress.Loopback;
            return IPAddress.TryParse(host, out var parsed) ? parsed : IPAddress.Any;
        }

        public void Dispose()
        {
            Stop();

            // 存储是我们自己创建的就由我们负责关闭（外部传进来的由调用方管理）
            if (_ownsAccountStore) Accounts?.Store?.Dispose();
        }
    }
}
