using GameFramework.Log;
using System;
using System.Collections;
using GameFramework.Net.Protocol;
using GameFramework.Net.Router;
using GameFramework.Net.Threading;
using UnityEngine;

namespace GameFramework.Net.Client.Unity
{
    /// <summary>
    /// Unity 侧的前端入口：挂在场景里的空物体上，然后由你主动调用 Login() 登录。
    ///
    /// 这里刻意**不做任何自动连接**：Start 里不会去连服务器，
    /// 只有你调用 Login() 之后才会建立 TCP 连接、发 login.request、拿到 login.response，
    /// 登录成功才开始心跳检测。
    ///
    /// 所有网络回调都在主线程执行（QueuedDispatcher 在 Update 里 Pump），
    /// 所以在处理函数里可以放心调用 transform、Instantiate 等 Unity API。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameClientBehaviour : MonoBehaviour
    {
        [Header("服务器地址")]
        [SerializeField] private string host = NetConfig.DefaultHost;
        [SerializeField] private int port = NetConfig.DefaultPort;

        [Header("登录（Login() 用这两个值，也可以在运行时传参覆盖）")]
        [SerializeField] private string account = "dev";
        [SerializeField] private string password = "dev";

        [Header("行为")]
        [SerializeField] private bool logVerbose = true;

        [Header("心跳（登录成功后才会开始）")]
        [SerializeField] private bool enableHeartbeat = true;
        [Tooltip("心跳间隔（毫秒）。默认 5000，即每 5 秒一条心跳。")]
        [SerializeField] private int heartbeatIntervalMs = NetConfig.DefaultHeartbeatIntervalMs;
        [Tooltip("心跳回包超时（毫秒）。超过这个时间没回包就判定连接超时并主动断开。")]
        [SerializeField] private int heartbeatTimeoutMs = NetConfig.DefaultHeartbeatTimeoutMs;

        [Header("断线自动重连")]
        [Tooltip("运行中连接意外断开（心跳超时 / 服务器重启 / 网络闪断）时自动重连。主动 Disconnect() 不会触发。")]
        [SerializeField] private bool autoReconnect = true;
        [Tooltip("自动重连最大尝试次数。用尽后发 ReconnectFailed。")]
        [SerializeField] private int reconnectMaxAttempts = 3;
        [Tooltip("每次重连失败后的重试间隔（秒）。")]
        [SerializeField] private float reconnectRetryDelay = 2f;

        private QueuedDispatcher _dispatcher;
        private GameClient _client;
        private volatile bool _loginInFlight;
        private readonly object _loginLock = new object();

        // 断线自动重连状态
        private bool _autoReconnecting;
        private bool _manualDisconnect;          // 用户主动断开（Disconnect / 退出登录）→ 不自动重连
        private int _reconnectAttempt;
        private Coroutine _reconnectCoroutine;

        /// <summary>底层客户端，业务代码用它注册协议处理函数。</summary>
        public GameClient Client => _client;

        public ProtocolRouter Router => _client?.Router;

        public bool IsConnected => _client != null && _client.IsConnected;

        /// <summary>是否已登录成功（只有登录成功后才会开始心跳）。</summary>
        public bool IsLoggedIn => _client != null && _client.IsLoggedIn;

        /// <summary>登录结果（成功时含 playerId / sessionId）。</summary>
        public LoginResponse Session { get; private set; }
        /// <summary>最近一次 Login(account, password) 用的账号；登录成功后业务可以做本地记忆。</summary>
        public string LastLoginAccount { get; private set; }

        /// <summary>最近一次 Login 用的密码；登录成功后业务可据此做「记住密码」。</summary>
        public string LastLoginPassword { get; private set; }

        /// <summary>服务器地址 host:port，业务做本地记忆 / 换服时用。</summary>
        public string ServerAddress { get { return host + ":" + port; } }

        /// <summary>
        /// 运行时切换目标服务器（选服用）。改的是 Inspector 里填的 host/port，
        /// 下一次 Login / LoginAsync 就会连到新服务器。已连接时不会自动重连到新服。
        /// </summary>
        public void SetServer(string newHost, int newPort)
        {
            if (!string.IsNullOrEmpty(newHost))
                host = newHost;
            port = newPort;
            GameLog.Info(LogTag.Net, $"目标服务器已切换：{host}:{port}");
        }

        public event Action<GameClientBehaviour> Connected;
        public event Action<GameClientBehaviour> LoggedIn;
        public event Action<GameClientBehaviour, LoginResponse> LoginFailed;
        public event Action<GameClientBehaviour, string> Disconnected;

        /// <summary>连接超时（心跳没回包）：参数是超时说明。触发后客户端已经主动断开。</summary>
        public event Action<GameClientBehaviour, string> ConnectionTimedOut;

        /// <summary>断线自动重连：开始尝试重连（第 1 次前触发）。</summary>
        public event Action<GameClientBehaviour> Reconnecting;
        /// <summary>断线自动重连：成功（重新登录完成）。</summary>
        public event Action<GameClientBehaviour> Reconnected;
        /// <summary>断线自动重连：尝试次数用尽，放弃。参数是最后一次失败原因。</summary>
        public event Action<GameClientBehaviour, string> ReconnectFailed;

        /// <summary>
        /// 每次新建底层客户端时触发（登录、注册并登录都会重建 GameClient，Router 也跟着换新）。
        /// 协议中心（ProtocolHub）就是在这里 Bind 新路由表的：
        /// 订阅关系登记一次，之后每次重连自动挂到新客户端上，不用重新注册。
        /// 触发时机在真正连接之前，所以不会有"漏掉第一条推送"的问题。
        /// </summary>
        public event Action<GameClientBehaviour, GameClient> ClientCreated;

        private void Awake()
        {

        }

        public void Init() 
        {
            // 回调统一收集到队列，Update 里在主线程执行
            _dispatcher = new QueuedDispatcher(ex => GameLog.Error(LogTag.Net, $"回调异常：{ex}"));
        }

        private void Update()
        {
            _dispatcher?.Pump();
        }

        private void OnDestroy()
        {
            Disconnect();
        }

        private void OnApplicationQuit()
        {
            Disconnect();
        }

        /// <summary>用 Inspector 里填的账号登录。</summary>
        public void Login() => Login(account, password);

        /// <summary>
        /// 注册账号（不登录）。注册成功只是账号建好了，要进游戏还需要 Login()。
        /// 返回 true 表示注册成功；失败原因可以看 <paramref name="response"/>。
        /// </summary>
        public bool Register(string registerAccount, string registerPassword, out RegisterResponse response)
        {
            var client = new GameClient(
                JsonMessageSerializer.Instance,
                new UnityNetLogger("NET", logVerbose),
                _dispatcher);

            client.ClientVersion = Application.version;
            client.ClientPlatform = Application.platform.ToString();

            try
            {
                response = client.Register(host, port, registerAccount, registerPassword);

                if (response == null || !response.Success)
                {
                    GameLog.Error(LogTag.Net, $"注册失败：{response?.Message ?? response?.Reason ?? "没有收到注册回包"}");
                    return false;
                }

                GameLog.Info(LogTag.Net, $"注册成功：账号='{registerAccount}'，playerId='{response.PlayerId}'");
                return true;
            }
            catch (Exception ex)
            {
                GameLog.Error(LogTag.Net, $"注册异常（{host}:{port}）：{ex.Message}");
                response = null;
                return false;
            }
            finally
            {
                client.Dispose();
            }
        }

        /// <summary>
        /// 注册并登录（一站式）：账号已存在时会直接登录，所以按钮可以重复点。
        /// 成功返回 true 并开始心跳。
        /// </summary>
        public bool RegisterAndLogin(string registerAccount, string registerPassword)
        {
            LastLoginAccount = registerAccount;
            LastLoginPassword = registerPassword;

            if (IsConnected) Disconnect();

            _client = new GameClient(
                JsonMessageSerializer.Instance,
                new UnityNetLogger("NET", logVerbose),
                _dispatcher);

            _client.ClientVersion = Application.version;
            _client.ClientPlatform = Application.platform.ToString();
            _client.HeartbeatEnabled = enableHeartbeat;
            _client.HeartbeatIntervalMs = heartbeatIntervalMs;
            _client.HeartbeatTimeoutMs = heartbeatTimeoutMs;

            _client.Connected += OnConnected;
            _client.Disconnected += OnDisconnected;
            _client.HeartbeatReceived += OnHeartbeatReceived;
            _client.ConnectionTimedOut += OnConnectionTimedOut;

            // 让协议中心挂到新客户端的路由表上（在连接之前，保证第一条推送不会漏）
            ClientCreated?.Invoke(this, _client);

            try
            {
                var response = _client.RegisterAndLogin(host, port, registerAccount, registerPassword);

                if (response == null || !response.Success)
                {
                    GameLog.Error(LogTag.Net, $"注册并登录失败：{response?.Message ?? response?.Reason ?? "没有收到回包"}");
                    LoginFailed?.Invoke(this, response);
                    return false;
                }

                Session = response;
                GameLog.Info(LogTag.Net, $"注册并登录成功：playerId='{response.PlayerId}'，sessionId='{response.SessionId}'");
                LoggedIn?.Invoke(this);
                return true;
            }
            catch (Exception ex)
            {
                GameLog.Error(LogTag.Net, $"注册并登录异常（{host}:{port}）：{ex.Message}");
                LoginFailed?.Invoke(this, null);
                return false;
            }
        }

        /// <summary>
        /// 主动登录：建立连接 → 发 login.request → 等 login.response。
        /// 成功返回 true 并开始心跳；失败返回 false（连接已断开），原因看 LoginResponse.Message。
        /// </summary>
        public bool Login(string loginAccount, string loginPassword)
        {
            LastLoginAccount = loginAccount;
            LastLoginPassword = loginPassword;

            if (IsConnected) Disconnect();

            _client = new GameClient(
                JsonMessageSerializer.Instance,
                new UnityNetLogger("NET", logVerbose),
                _dispatcher);

            _client.ClientVersion = Application.version;
            _client.ClientPlatform = Application.platform.ToString();
            _client.HeartbeatEnabled = enableHeartbeat;
            _client.HeartbeatIntervalMs = heartbeatIntervalMs;
            _client.HeartbeatTimeoutMs = heartbeatTimeoutMs;

            _client.Connected += OnConnected;
            _client.Disconnected += OnDisconnected;
            _client.HeartbeatReceived += OnHeartbeatReceived;
            _client.ConnectionTimedOut += OnConnectionTimedOut;

            // 让协议中心挂到新客户端的路由表上（在连接之前，保证第一条推送不会漏）
            ClientCreated?.Invoke(this, _client);

            try
            {
                var response = _client.Login(host, port, loginAccount, loginPassword);

                if (response == null || !response.Success)
                {
                    GameLog.Error(LogTag.Net, $"登录失败：{response?.Message ?? response?.Reason ?? "没有收到登录回包"}");
                    LoginFailed?.Invoke(this, response);
                    return false;
                }

                Session = response;
                GameLog.Info(LogTag.Net, $"登录成功：playerId='{response.PlayerId}'，sessionId='{response.SessionId}'，服务器='{response.ServerName}'");
                LoggedIn?.Invoke(this);
                return true;
            }
            catch (Exception ex)
            {
                GameLog.Error(LogTag.Net, $"登录异常（{host}:{port}）：{ex.Message}");
                LoginFailed?.Invoke(this, null);
                return false;
            }
        }

        /// <summary>
        /// 异步登录：后台线程执行阻塞的登录流程，事件（Connected / LoggedIn / LoginFailed / ClientCreated）
        /// 通过派发器回到主线程触发，不会卡住主线程。用于「回前台自动重连」这类场景。
        /// 同一时刻只允许一个登录流程在跑；正在进行时新的调用会被忽略并回调 false。
        /// </summary>
        public void LoginAsync(string loginAccount, string loginPassword, Action<bool> onComplete = null)
        {
            if (_dispatcher == null)
                Init();

            bool start;
            lock (_loginLock)
            {
                start = !_loginInFlight;
                if (start)
                    _loginInFlight = true;
            }

            if (!start)
            {
                GameLog.Warn(LogTag.Net, "已有登录在进行，忽略本次 LoginAsync");
                if (onComplete != null)
                    onComplete(false);
                return;
            }

            LastLoginAccount = loginAccount;
            LastLoginPassword = loginPassword;

            // 新的登录尝试：清除"主动断开"标志（可能是重连流程发起的，也可能是用户重新点登录）
            _manualDisconnect = false;

            if (IsConnected)
                Disconnect();

            var client = new GameClient(
                JsonMessageSerializer.Instance,
                new UnityNetLogger("NET", logVerbose),
                _dispatcher);

            client.ClientVersion = Application.version;
            client.ClientPlatform = Application.platform.ToString();
            client.HeartbeatEnabled = enableHeartbeat;
            client.HeartbeatIntervalMs = heartbeatIntervalMs;
            client.HeartbeatTimeoutMs = heartbeatTimeoutMs;

            // Connected 是在登录线程上触发的，这里转回主线程；其余事件底层已经走 dispatcher
            client.Connected += c => _dispatcher.Post(() =>
            {
                if (logVerbose)
                    GameLog.Info(LogTag.Net, $"已连接到 {c.ServerEndPoint}，正在登录…");
                Connected?.Invoke(this);
            });
            client.Disconnected += OnDisconnected;
            client.HeartbeatReceived += OnHeartbeatReceived;
            client.ConnectionTimedOut += OnConnectionTimedOut;

            _client = client;
            _dispatcher.Post(() => ClientCreated?.Invoke(this, client));

            System.Threading.ThreadPool.QueueUserWorkItem(_ => DoLoginAsyncBody(loginAccount, loginPassword, client, onComplete));
        }

        private void DoLoginAsyncBody(string account, string password, GameClient client, Action<bool> onComplete)
        {
            LoginResponse response = null;
            Exception error = null;

            try
            {
                response = client.Login(host, port, account, password);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            if (error != null)
            {
                GameLog.Error(LogTag.Net, $"登录异常（{host}:{port}）：{error.Message}");
                _dispatcher.Post(() =>
                {
                    lock (_loginLock)
                        _loginInFlight = false;
                    LoginFailed?.Invoke(this, null);
                    if (onComplete != null)
                        onComplete(false);
                });
                return;
            }

            if (response == null || !response.Success)
            {
                GameLog.Error(LogTag.Net, $"登录失败：{response?.Message ?? response?.Reason ?? "没有收到登录回包"}");
                _dispatcher.Post(() =>
                {
                    lock (_loginLock)
                        _loginInFlight = false;
                    LoginFailed?.Invoke(this, response);
                    if (onComplete != null)
                        onComplete(false);
                });
                return;
            }

            _dispatcher.Post(() =>
            {
                Session = response;
                lock (_loginLock)
                    _loginInFlight = false;
                GameLog.Info(LogTag.Net, $"登录成功：playerId='{response.PlayerId}'，sessionId='{response.SessionId}'，服务器='{response.ServerName}'");
                LoggedIn?.Invoke(this);
                if (onComplete != null)
                    onComplete(true);
            });
        }

        /// <summary>退出登录 / 主动断开连接。主动断开不会触发自动重连。</summary>
        public void Disconnect()
        {
            StopAutoReconnect();
            _manualDisconnect = true;

            if (_client == null) return;

            _client.Connected -= OnConnected;
            _client.Disconnected -= OnDisconnected;
            _client.HeartbeatReceived -= OnHeartbeatReceived;
            _client.ConnectionTimedOut -= OnConnectionTimedOut;
            _client.Close();
            _client = null;
            Session = null;
        }

        public void Send(string protocol, object payload = null)
        {
            if (!IsConnected)
            {
                GameLog.Warn(LogTag.Net, $"尚未连接服务器，'{protocol}' 未发送");
                return;
            }
            _client.Send(protocol, payload);
        }

        /// <summary>用协议编号发送（推荐）。</summary>
        public void Send(ProtocolId protocol, object payload = null)
            => Send(Protocols.NameOf(protocol), payload);

        /// <summary>是否正在自动重连中。</summary>
        public bool IsReconnecting { get { return _autoReconnecting; } }

        /// <summary>是否允许自动重连（默认 true；主动 Disconnect 后为 false，直到下一次 Login 重置）。</summary>
        public bool AutoReconnectEnabled { get { return autoReconnect; } }

        /// <summary>
        /// 手动触发一次自动重连（用上次登录的账号密码）。一般不用调 —— 断线会自动触发；
        /// 给「回前台重连」这类需要主动拉起重连的场景用。
        /// </summary>
        public void StartAutoReconnect()
        {
            if (!autoReconnect) return;
            if (_autoReconnecting) return;
            if (string.IsNullOrEmpty(LastLoginAccount)) return;   // 从没登录过，没有可重连的凭据
            if (IsConnected) return;                              // 已经连上了

            _manualDisconnect = false;
            _autoReconnecting = true;
            _reconnectAttempt = 0;
            GameLog.Info(LogTag.Net, $"断线自动重连启动（最多 {reconnectMaxAttempts} 次，间隔 {reconnectRetryDelay}s）");
            Reconnecting?.Invoke(this);
            _reconnectCoroutine = StartCoroutine(DoAutoReconnect());
        }

        /// <summary>停止自动重连（主动断开 / 销毁时调用）。</summary>
        public void StopAutoReconnect()
        {
            _autoReconnecting = false;
            _reconnectAttempt = 0;
            if (_reconnectCoroutine != null)
            {
                StopCoroutine(_reconnectCoroutine);
                _reconnectCoroutine = null;
            }
        }

        private IEnumerator DoAutoReconnect()
        {
            while (_autoReconnecting)
            {
                _reconnectAttempt++;

                // 重连期间可能已经连上了（比如 PlatformManager 的 resume 重连先成功了）
                if (IsConnected)
                {
                    GameLog.Info(LogTag.Net, "自动重连：检测到已连接，停止重连。");
                    break;
                }

                GameLog.InfoFormat(LogTag.Net, "断线自动重连：第 {0}/{1} 次，账号='{2}'…", _reconnectAttempt, reconnectMaxAttempts, LastLoginAccount);

                bool success = false;
                string failReason = "未知错误";
                LoginAsync(LastLoginAccount, LastLoginPassword, ok =>
                {
                    success = ok;
                    if (!ok && Session == null)
                        failReason = "登录失败";
                });

                // 等本次登录流程结束（LoginAsync 是异步的，轮询它的结束状态）
                // 注意：LoginAsync 结束会触发 LoggedIn 或 LoginFailed，我们在这等 success 标志
                yield return WaitForLoginDone(() => success || !_autoReconnecting);

                if (!_autoReconnecting)
                    yield break;

                if (success)
                {
                    GameLog.Info(LogTag.Net, "断线自动重连成功。");
                    _autoReconnecting = false;
                    _reconnectCoroutine = null;
                    Reconnected?.Invoke(this);
                    yield break;
                }

                if (_reconnectAttempt >= reconnectMaxAttempts)
                {
                    GameLog.Warn(LogTag.Net, $"断线自动重连失败：尝试 {reconnectMaxAttempts} 次仍失败，放弃。原因：{failReason}");
                    _autoReconnecting = false;
                    _reconnectCoroutine = null;
                    ReconnectFailed?.Invoke(this, failReason);
                    yield break;
                }

                GameLog.InfoFormat(LogTag.Net, "断线自动重连：{0:F1}s 后重试…", reconnectRetryDelay);
                yield return new WaitForSecondsRealtime(reconnectRetryDelay);
            }

            _autoReconnecting = false;
            _reconnectCoroutine = null;
        }

        /// <summary>
        /// 等 LoginAsync 结束：轮询 success 标志，或直到 autoReconnecting 被外部取消。
        /// 用一个轻量的循环等待（每帧检查），不阻塞主线程。
        /// </summary>
        private IEnumerator WaitForLoginDone(Func<bool> done)
        {
            float elapsed = 0f;
            const float maxWait = 15f;   // 单次登录上限，防止协程永远挂着
            while (!done() && elapsed < maxWait)
            {
                yield return null;
                elapsed += Time.unscaledDeltaTime;
            }
        }

        private void OnConnected(GameClient client)
        {
            if (logVerbose) GameLog.Info(LogTag.Net, $"已连接到 {client.ServerEndPoint}，正在登录…");
            Connected?.Invoke(this);
        }

        private void OnDisconnected(GameClient client, string reason)
        {
            Session = null;
            Disconnected?.Invoke(this, reason);

            // 连接意外断开（非主动 Disconnect）→ 自动重连
            if (autoReconnect && !_manualDisconnect)
                StartAutoReconnect();
        }

        private void OnHeartbeatReceived(GameClient client, long roundTripMs)
        {
            if (logVerbose)
            {
                //Debug.Log($"[NET] 心跳正常，往返 {roundTripMs}ms");
            }
        }

        private void OnConnectionTimedOut(GameClient client, string message)
        {
            GameLog.Error(LogTag.Net, $"{message}");
            ConnectionTimedOut?.Invoke(this, message);

            // 心跳超时 → 连接已断开 → 自动重连（ConnectionTimedOut 后底层会再发 Disconnected，
            // 所以这里不直接启动，等 OnDisconnected 统一处理，避免重复触发）
        }
    }
}
