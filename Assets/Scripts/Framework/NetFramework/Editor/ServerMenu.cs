#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using GameFramework.Net.Client;
using GameFramework.Net.Client.Unity;
using GameFramework.Net.Protocol;
using GameFramework.Net.Server;
using GameFramework.ServerHost;
using UnityEditor;
using UnityEngine;

namespace GameFramework.Net.EditorTools
{
    /// <summary>
    /// 编辑器菜单（Unity 顶部 "Game Framework"）：起/停本地服务器 + 一个登录用的开发工具。
    /// 这些只是开发期便利工具，正式包里不会被打进去（Editor 文件夹）。
    /// </summary>
    public static class ServerMenu
    {
        private const string MenuRoot = "Game Framework/";
        private const string DevMenuRoot = MenuRoot + "Dev Tools/";
        private const int PreferredPort = 7777;
        private const int PortSearchCount = 10;

        /// <summary>记住"用户希望服务器是开着的"，脚本重编译/进出 Play 模式后自动拉起来。</summary>
        private const string AutoStartKey = "GameFramework.Server.AutoStart";

        private static GameServer _server;
        private static StaticFileServer _staticFileServer;

        /// <summary>编辑器内热更资源站的端口（独立后端进程默认也是 8000）。</summary>
        private const int PreferredWebPort = 8000;

        [InitializeOnLoadMethod]
        private static void RestoreServerAfterDomainReload()
        {
            if (!EditorPrefs.GetBool(AutoStartKey, false)) return;

            EditorApplication.delayCall += () =>
            {
                if (_server != null && _server.IsRunning) return;
                StartServer();
            };
        }

        [MenuItem(MenuRoot + "Start Local Server", priority = 1)]
        public static void StartServer()
        {
            if (_server != null && _server.IsRunning)
            {
                Debug.Log($"[框架] 服务器已经在 127.0.0.1:{_server.Port} 运行（在线 {_server.ClientCount}）");
                return;
            }

            var failures = new List<string>();

            // 端口被占（比如上一次的服务器还没退干净）时自动往后找一个可用端口
            for (var port = PreferredPort; port < PreferredPort + PortSearchCount; port++)
            {
                var server = new GameServer(new GameServerOptions
                {
                    Host = NetConfig.DefaultHost,
                    Port = port,
                    ServerName = "editor-localserver",
                    Logger = new UnityNetLogger("SERVER"),
                    // Unity 里没有 SQLite 驱动，编辑器内用 JSON 账号库（独立进程后端用 SQLite）
                    AccountStorePath = System.IO.Path.GetFullPath(
                        System.IO.Path.Combine(Application.dataPath, "..", "GameData", "accounts.json")),
                    LogReceivedFrames = true
                });

                if (!server.TryStart(out var error))
                {
                    failures.Add($"  {port}: {error}");
                    continue;
                }

                _server = server;
                EditorPrefs.SetBool(AutoStartKey, true);

                Debug.Log($"[框架] 本地服务器已启动：127.0.0.1:{server.Port}（内容格式=json）");
                if (port != PreferredPort)
                    Debug.LogWarning($"[框架] {PreferredPort} 端口被占用，已自动改用 {port} 端口；客户端也要连这个端口。");

                EnsureStaticFileServer();
                return;
            }

            Debug.LogError($"[框架] 服务器启动失败，{PreferredPort}~{PreferredPort + PortSearchCount - 1} 端口都不可用：\n"
                           + string.Join("\n", failures));
        }

        [MenuItem(MenuRoot + "Start Local Server", validate = true)]
        private static bool StartServerValidate() => _server == null || !_server.IsRunning;

        /// <summary>
        /// 编辑器内也兼职热更资源站，行为和独立后端进程一致，省得再开一个 Node 静态服务器。
        /// 找不到资源目录、或端口被占用时只警告，不影响逻辑服。
        /// </summary>
        private static void EnsureStaticFileServer()
        {
            if (_staticFileServer != null) return;

            var root = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "..", "ServerData"));
            if (!System.IO.Directory.Exists(root))
            {
                Debug.LogWarning($"[框架] 没找到热更资源目录：{root}，本进程不提供资源下载。");
                return;
            }

            var staticServer = new StaticFileServer(root, PreferredWebPort, new UnityNetLogger("RES"));
            if (staticServer.TryStart(out var error))
            {
                _staticFileServer = staticServer;
                Debug.Log($"[框架] 内置静态资源站已启动：http://127.0.0.1:{staticServer.Port}/（根目录 {root}）");
                return;
            }

            Debug.LogWarning($"[框架] 静态资源站启动失败（端口 {PreferredWebPort}）：{error}");
        }

        [MenuItem(MenuRoot + "Stop Local Server", priority = 2)]
        public static void StopServer()
        {
            EditorPrefs.SetBool(AutoStartKey, false);

            if (_staticFileServer != null)
            {
                _staticFileServer.Dispose();
                _staticFileServer = null;
                Debug.Log("[框架] 内置静态资源站已停止。");
            }

            if (_server == null) return;
            _server.Stop();
            _server = null;
        }

        [MenuItem(MenuRoot + "Stop Local Server", validate = true)]
        private static bool StopServerValidate() => (_server != null && _server.IsRunning) || _staticFileServer != null;

        [MenuItem(MenuRoot + "Server Status", priority = 3)]
        public static void ServerStatus()
        {
            if (_server == null || !_server.IsRunning)
            {
                Debug.Log("[框架] 服务器未运行（本进程内）。若你用的是独立进程后端，用 netstat 看端口是否在监听。");
                return;
            }

            Debug.Log($"[框架] 服务器运行中：127.0.0.1:{_server.Port}，在线连接 {_server.ClientCount}，"
                      + $"已登录 {_server.LoggedInClients.Count}，"
                      + $"账号库={_server.Accounts.Store.Kind}（{_server.Accounts.List(999).Count} 个账号）"
                      + (_staticFileServer != null ? $"；静态资源站 http://127.0.0.1:{_staticFileServer.Port}/" : ""));
        }

        // ---------------- 开发工具 ----------------

        [MenuItem(DevMenuRoot + "添加开发账号（dev/dev）", priority = 20)]
        public static void AddDevAccount()
        {
            if (_server == null || !_server.IsRunning)
            {
                Debug.LogError("[框架] 先启动本地服务器");
                return;
            }

            if (_server.Accounts.Exists("dev"))
            {
                Debug.Log("[框架] 开发账号已存在：dev / dev");
                return;
            }

            _server.AddAccount("dev", "dev", "player-dev");
            Debug.Log("[框架] 已添加开发账号：dev / dev（playerId=player-dev）");
        }

        [MenuItem(DevMenuRoot + "注册并登录（dev/dev）", priority = 21)]
        public static void RegisterAndLogin()
        {
            var port = _server != null && _server.IsRunning ? _server.Port : PreferredPort;

            using (var client = new GameClient(
                       logger: new UnityNetLogger("DEV"),
                       dispatcher: GameFramework.Net.Threading.ImmediateDispatcher.Instance))
            {
                client.HeartbeatReceived += (c, rtt) => Debug.Log($"[框架] 心跳正常，往返 {rtt}ms（第 {c.HeartbeatAckCount} 次）");
                client.ConnectionTimedOut += (c, message) => Debug.LogError($"[框架] {message} —— 已主动断开");

                // 注册并登录：账号已存在时会直接走登录，所以这个菜单可以重复点
                var response = client.RegisterAndLogin(NetConfig.DefaultHost, port, "dev", "dev");

                if (response == null || !response.Success)
                {
                    // 开发便利：本进程内的服务器上还没有 dev 账号时，直接补一个再用原密码登录
                    if (_server != null && _server.IsRunning && !_server.Accounts.Exists("dev"))
                    {
                        _server.AddAccount("dev", "dev", "player-dev");
                        Debug.Log("[框架] 账号库里没有 dev，已自动创建 dev / dev，重试登录…");
                        response = client.Login(NetConfig.DefaultHost, port, "dev", "dev");
                    }

                    if (response == null || !response.Success)
                    {
                        Debug.LogError($"[框架] 注册并登录失败（目标 127.0.0.1:{port}）："
                                       + $"{response?.Message ?? response?.Reason ?? "没有回包"}");
                        return;
                    }
                }

                Debug.Log($"[框架] 注册并登录成功：playerId='{response.PlayerId}'，sessionId='{response.SessionId}'");

                // 登录成功后心跳才启动，这里等一会儿，好让心跳回包日志打出来
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (DateTime.UtcNow < deadline && client.HeartbeatAckCount == 0)
                    System.Threading.Thread.Sleep(50);

                Debug.Log($"[框架] 心跳已收到 {client.HeartbeatAckCount} 次回包，最近往返 {client.LastHeartbeatRoundTripMs}ms");
            }
        }

        [MenuItem(DevMenuRoot + "注册并登录（dev/dev）", validate = true)]
        private static bool LoginValidate() => Application.isPlaying || _server != null;

        [MenuItem(DevMenuRoot + "注册新账号并登录（自动命名）", priority = 22)]
        public static void RegisterNewAccountAndLogin()
        {
            var port = _server != null && _server.IsRunning ? _server.Port : PreferredPort;
            var account = "tester-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            const string password = "pwd123456";

            using (var client = new GameClient(
                       logger: new UnityNetLogger("DEV"),
                       dispatcher: GameFramework.Net.Threading.ImmediateDispatcher.Instance))
            {
                var response = client.RegisterAndLogin(NetConfig.DefaultHost, port, account, password);

                if (response == null || !response.Success)
                {
                    Debug.LogError($"[框架] 注册新账号失败（127.0.0.1:{port}）：{response?.Message ?? response?.Reason ?? "没有回包"}");
                    return;
                }

                Debug.Log($"[框架] 新账号注册并登录成功：账号='{account}'，密码='{password}'，"
                          + $"playerId='{response.PlayerId}'，sessionId='{response.SessionId}'");
            }
        }

        [MenuItem(DevMenuRoot + "注册新账号并登录（自动命名）", validate = true)]
        private static bool RegisterNewAccountValidate() => Application.isPlaying || _server != null;
    }
}
#endif
