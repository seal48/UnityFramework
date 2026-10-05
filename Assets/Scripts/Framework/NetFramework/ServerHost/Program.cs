#if !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.IO;
using GameFramework.Config;
using GameFramework.Net;
using GameFramework.Net.Client;
using GameFramework.Net.Protocol;
using GameFramework.Net.Server;
using GameFramework.Net.Server.Persistence;

namespace GameFramework.ServerHost
{
    /// <summary>
    /// 独立进程的后端入口（start-server.cmd / dotnet run）。
    /// 这个文件被 #if 排除，Unity 不会编译它；Unity 侧请用 Editor/ServerMenu.cs。
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                // 重定向到文件时默认编码不是 UTF-8，中文日志会变乱码
                Console.OutputEncoding = System.Text.Encoding.UTF8;
            }
            catch (Exception)
            {
                // 某些环境不支持修改，忽略
            }

            var port = NetConfig.DefaultPort;
            var portSpecified = false;
            var selfTest = false;
            var frameTest = false;
            var protocolTest = false;
            var pushTest = false;
            var configTest = false;
            var devAccount = true;
            var dataDirectory = Path.Combine(Directory.GetCurrentDirectory(), "GameData");
            var webPort = 8000;
            var webServerDisabled = false;
            string webRoot = null;
            string configDirectory = null;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--port":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed))
                        {
                            port = parsed;
                            portSpecified = true;
                        }
                        break;
                    case "--data-dir":
                        if (i + 1 < args.Length) dataDirectory = args[i + 1];
                        break;
                    case "--selftest":
                        selfTest = true;
                        break;
                    case "--frametest":
                        frameTest = true;
                        break;
                    case "--protocoltest":
                        protocolTest = true;
                        break;
                    case "--pushtest":
                        pushTest = true;
                        break;
                    case "--cfgtest":
                        configTest = true;
                        break;
                    case "--config-dir":
                        if (i + 1 < args.Length) configDirectory = args[i + 1];
                        break;
                    case "--no-dev-account":
                        devAccount = false;
                        break;
                    case "--web-port":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedWebPort))
                            webPort = parsedWebPort;
                        break;
                    case "--web-root":
                        if (i + 1 < args.Length) webRoot = args[i + 1];
                        break;
                    case "--no-web":
                        webServerDisabled = true;
                        break;
                }
            }

            var logger = new ConsoleNetLogger("SERVER");

            if (protocolTest) return RunProtocolTest(logger) ? 0 : 1;
            if (frameTest) return RunFrameTest(logger) ? 0 : 1;
            if (pushTest) return RunPushTest(logger) ? 0 : 1;
            if (configTest) return RunConfigTest(configDirectory, logger) ? 0 : 1;

            var serverLogger = selfTest ? new RecordingLogger(logger) : (INetLogger)logger;

            // 账号存储：优先 SQLite（单文件数据库），驱动不可用时回落 JSON
            var store = CreateAccountStore(dataDirectory, serverLogger);

            var server = CreateAndStartServer(port, portSpecified, serverLogger, store, out var startError);
            if (server == null)
            {
                logger.Error(startError);
                logger.Error("换个端口再试：start-server.cmd --port 7788");
                return 2;
            }

            // 玩家数据推送服务：登录成功后推全量快照，改数据时推增量
            var push = new PlayerPushService(server);
            push.Pushed += (account, protocol, count) =>
                logger.Info($"[推送] {protocol} → 账号 '{account}' 的 {count} 条连接");

            if (devAccount)
            {
                if (server.Accounts.Exists("dev"))
                {
                    logger.Info("开发账号已存在：dev / dev");
                }
                else
                {
                    server.AddAccount("dev", "dev", "player-dev");
                    logger.Info("已创建开发账号：dev / dev（本地开发用；正式部署请换成自己的 ILoginValidator）");
                }
            }

            if (selfTest)
            {
                var ok = RunSelfTest(server, store, dataDirectory, (RecordingLogger)serverLogger, logger);
                server.Dispose();
                return ok ? 0 : 1;
            }

            // 让后端进程同时兼任热更资源站：一个进程既跑逻辑服（TCP）也提供静态资源（HTTP）
            StaticFileServer staticFileServer = null;
            if (!webServerDisabled)
                staticFileServer = StartStaticFileServer(webRoot, webPort, logger);

            Console.WriteLine();
            Console.WriteLine("可用命令：");
            Console.WriteLine("  clients                         查看连接与登录状态");
            Console.WriteLine("  accounts [关键字]                列出账号");
            Console.WriteLine("  account <账号>                   查看账号详情（等级/属性/道具）");
            Console.WriteLine("  register <账号> <密码>            注册账号");
            Console.WriteLine("  delete <账号>                    删除账号");
            Console.WriteLine("  setlevel <账号> <等级>            改等级（改完自动推给在线客户端）");
            Console.WriteLine("  gold <账号> <增减量>              加/扣金币（改完自动推送）");
            Console.WriteLine("  setattr <账号> <属性名> <值>       改属性（力量/敏捷/智力…，改完自动推送）");
            Console.WriteLine("  additem <账号> <道具id> <数量>     加道具（改完自动推送）");
            Console.WriteLine("  delitem <账号> <道具id> <数量>     扣道具（改完自动推送）");
            Console.WriteLine("  push <账号>                      手动推一次玩家全量数据");
            Console.WriteLine("  quit");
            Console.WriteLine();

            // 读键盘命令：
            //   有输入（终端手打 / 管道喂命令）→ 执行完就退出
            //   一条都没读到（被 Start-Process、双击 exe、重定向空输入启动）→ 进后台常驻，
            //   千万别在这里退出，否则"启动"看起来就是闪一下就没了。
            if (!RunInteractiveConsole(server, push, logger))
            {
                logger.Info("没有读到命令输入，进入后台常驻模式（结束该进程即可停止服务器）");
                var stop = new System.Threading.ManualResetEventSlim(false);
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    stop.Set();
                };
                AppDomain.CurrentDomain.ProcessExit += (sender, e) => stop.Set();
                stop.Wait();
            }

            push.Dispose();
            if (staticFileServer != null) staticFileServer.Dispose();
            server.Dispose();
            return 0;
        }

        /// <summary>
        /// 启动内置静态资源服务（热更资源站）。
        /// 找不到资源目录、或端口被占用时只警告，不影响逻辑服正常启动。
        /// </summary>
        private static StaticFileServer StartStaticFileServer(string webRoot, int webPort, INetLogger logger)
        {
            var root = ResolveWebRoot(webRoot, logger);
            if (root == null)
            {
                logger.Warn("没有找到热更资源目录（ServerData），内置静态服务器未启动；可用 --web-root <目录> 指定");
                return null;
            }

            var staticServer = new StaticFileServer(root, webPort, logger);
            if (staticServer.TryStart(out var error))
            {
                logger.Info("内置静态服务器已启动：http://127.0.0.1:" + staticServer.Port + "/（根目录 " + staticServer.RootDirectory + "）");
                return staticServer;
            }

            logger.Warn("内置静态服务器启动失败（端口 " + webPort + "）：" + error + "；可用 --web-port 换一个端口");
            return null;
        }

        /// <summary>找热更资源目录：优先用参数，否则从当前目录逐级往上找 ServerData。</summary>
        private static string ResolveWebRoot(string explicitRoot, INetLogger logger)
        {
            if (!string.IsNullOrEmpty(explicitRoot))
            {
                if (Directory.Exists(explicitRoot)) return Path.GetFullPath(explicitRoot);
                logger.Warn("--web-root 指定的目录不存在：" + explicitRoot);
                return null;
            }

            var directory = Directory.GetCurrentDirectory();
            for (var i = 0; i < 4 && !string.IsNullOrEmpty(directory); i++)
            {
                var candidate = Path.Combine(directory, "ServerData");
                if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);

                var parent = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == directory) break;
                directory = parent;
            }

            return null;
        }

        /// <summary>优先用 SQLite；驱动缺失/打不开时回落到 JSON 文件（保证服务器总能起来）。</summary>
        private static IAccountStore CreateAccountStore(string dataDirectory, INetLogger logger)
        {
            try
            {
                return new SqliteAccountStore(Path.Combine(dataDirectory, "accounts.db"), logger);
            }
            catch (Exception ex)
            {
                logger.Warn($"SQLite 不可用（{ex.Message}），回落到 JSON 文件存储");
                return new JsonAccountStore(Path.Combine(dataDirectory, "accounts.json"), logger);
            }
        }

        private static GameServer CreateAndStartServer(int port, bool portSpecified, INetLogger logger,
            IAccountStore store, out string error)
        {
            error = null;

            if (portSpecified)
            {
                var exact = BuildServer(port, logger, store);
                if (exact.TryStart(out error)) return exact;
                return null;
            }

            var attempts = new List<string>();
            for (var candidate = port; candidate < port + 10; candidate++)
            {
                var server = BuildServer(candidate, logger, store);
                if (server.TryStart(out var candidateError)) return server;

                attempts.Add($"  {candidate}: {candidateError}");
                logger.Warn($"端口 {candidate} 不可用，换下一个：{candidateError}");
            }

            error = "连续 10 个端口都无法监听：\n" + string.Join("\n", attempts);
            return null;
        }

        private static GameServer BuildServer(int port, INetLogger logger, IAccountStore store)
        {
            return new GameServer(new GameServerOptions
            {
                Host = NetConfig.DefaultHost,
                Port = port,
                ServerName = "localserver",
                Logger = logger,
                AccountStore = store,
                LogReceivedFrames = true
            });
        }

        /// <summary>
        /// 控制台命令循环。
        /// 返回 true：读到过命令（含 quit），调用方执行完就可以退出；
        /// 返回 false：一条命令都没读到（空重定向/双击启动），调用方应转入后台常驻。
        /// </summary>
        private static bool RunInteractiveConsole(GameServer server, PlayerPushService push, INetLogger logger)
        {
            var sawCommands = false;

            while (true)
            {
                var line = Console.ReadLine();
                if (line == null) return sawCommands;

                sawCommands = true;

                var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                var command = parts[0].ToLowerInvariant();
                if (command == "quit" || command == "exit") return true;

                try
                {
                    HandleCommand(server, push, command, parts, logger);
                }
                catch (Exception ex)
                {
                    logger.Error("命令执行失败", ex);
                }
            }
        }

        private static void HandleCommand(GameServer server, PlayerPushService push, string command, string[] parts, INetLogger logger)
        {
            switch (command)
            {
                case "clients":
                    PrintClients(server, logger);
                    break;

                case "accounts":
                    PrintAccounts(server, parts.Length > 1 ? parts[1] : null, logger);
                    break;

                case "account":
                    PrintAccountDetail(server, parts, logger);
                    break;

                case "register":
                    if (parts.Length >= 3)
                    {
                        logger.Info(server.Accounts.Register(parts[1], parts[2], out var error)
                            ? $"账号已创建：{parts[1]}"
                            : $"创建失败：{error}");
                    }
                    else logger.Warn("用法：register <账号> <密码>");
                    break;

                case "delete":
                    if (parts.Length >= 2)
                        logger.Info(server.Accounts.Delete(parts[1]) ? "账号已删除" : "账号不存在");
                    else logger.Warn("用法：delete <账号>");
                    break;

                case "setlevel":
                    if (parts.Length >= 3 && int.TryParse(parts[2], out var level))
                        logger.Info(push.SetLevel(parts[1], level) ? "已修改并推送" : "账号不存在");
                    else logger.Warn("用法：setlevel <账号> <等级>");
                    break;

                case "gold":
                    if (parts.Length >= 3 && long.TryParse(parts[2], out var goldDelta))
                        logger.Info(push.AddGold(parts[1], goldDelta) ? "已修改并推送" : "账号不存在");
                    else logger.Warn("用法：gold <账号> <增减量>");
                    break;

                case "setattr":
                    if (parts.Length >= 4 && long.TryParse(parts[3], out var value))
                        logger.Info(push.SetAttribute(parts[1], parts[2], value) ? "已修改并推送" : "账号不存在");
                    else logger.Warn("用法：setattr <账号> <属性名> <值>");
                    break;

                case "additem":
                    if (parts.Length >= 4 && int.TryParse(parts[3], out var addCount))
                        logger.Info(push.AddItem(parts[1], parts[2], addCount) ? "已添加并推送" : "失败（账号不存在？）");
                    else logger.Warn("用法：additem <账号> <道具id> <数量>");
                    break;

                case "delitem":
                    if (parts.Length >= 4 && int.TryParse(parts[3], out var removeCount))
                        logger.Info(push.RemoveItem(parts[1], parts[2], removeCount) ? "已扣除并推送" : "失败（数量不足或账号不存在）");
                    else logger.Warn("用法：delitem <账号> <道具id> <数量>");
                    break;

                case "push":
                    if (parts.Length >= 2)
                        logger.Info($"已推送玩家数据，命中 {push.PushPlayerInfo(parts[1])} 条连接");
                    else logger.Warn("用法：push <账号>");
                    break;

                default:
                    logger.Warn($"未知命令：{command}");
                    break;
            }
        }

        private static void PrintClients(GameServer server, INetLogger logger)
        {
            logger.Info($"当前连接：{server.ClientCount}，其中已登录：{server.LoggedInClients.Count}");
            foreach (var peer in server.Clients)
            {
                var session = PeerSession.Get(peer);
                logger.Info($"  - {peer.PeerId} @ {peer.RemoteEndPoint}，"
                            + (session != null && session.LoggedIn
                                ? $"已登录 account='{session.Account}' playerId='{session.PlayerId}'"
                                : "未登录"));
            }
        }

        private static void PrintAccounts(GameServer server, string search, INetLogger logger)
        {
            var accounts = server.Accounts.List(50, search);
            logger.Info($"账号共 {accounts.Count} 个（最多列 50 个）：");

            foreach (var account in accounts)
            {
                logger.Info($"  - {account.Account}  playerId={account.PlayerId}  "
                            + $"等级={account.Player?.Level ?? 1}  金币={account.Player?.Gold ?? 0}  "
                            + $"道具={account.Player?.Items?.Count ?? 0} 种  "
                            + $"封禁={(account.Banned ? "是" : "否")}");
            }
        }

        private static void PrintAccountDetail(GameServer server, string[] parts, INetLogger logger)
        {
            if (parts.Length < 2)
            {
                logger.Warn("用法：account <账号>");
                return;
            }

            if (!server.Accounts.TryGet(parts[1], out var record))
            {
                logger.Warn($"账号不存在：{parts[1]}");
                return;
            }

            logger.Info($"账号 {record.Account}：playerId={record.PlayerId}，封禁={record.Banned}");
            logger.Info($"  等级={record.Player.Level}  经验={record.Player.Exp}  金币={record.Player.Gold}");

            var attributes = record.Player.Attributes;
            if (attributes == null || attributes.Count == 0)
            {
                logger.Info("  属性：无");
            }
            else
            {
                var text = new System.Text.StringBuilder();
                foreach (var pair in attributes)
                {
                    if (text.Length > 0) text.Append("，");
                    text.Append(pair.Key).Append('=').Append(pair.Value);
                }
                logger.Info("  属性：" + text);
            }

            var items = server.Accounts.GetItems(record.Account);
            if (items.Count == 0)
            {
                logger.Info("  道具：无");
                return;
            }

            logger.Info($"  道具（{items.Count} 种）：");
            foreach (var item in items)
            {
                logger.Info($"    - {item.ItemId} x{item.Count}"
                            + (string.IsNullOrEmpty(item.ExtraJson) ? string.Empty : $"  附加={item.ExtraJson}"));
            }
        }

        // ---------------- 自检 ----------------

        private static bool RunSelfTest(GameServer server, IAccountStore store, string dataDirectory,
            RecordingLogger serverLog, INetLogger logger)
        {
            var ok = true;

            logger.Info("===== 1/5 协议名总表 =====");
            ok &= RunProtocolTest(logger);

            logger.Info("===== 2/5 报文层 =====");
            ok &= RunFrameTest(logger);

            logger.Info("===== 3/5 账号存储（增删改查 + 持久化 + 密码哈希）=====");
            ok &= RunAccountStoreTest(dataDirectory, logger);

            logger.Info("===== 4/5 登录 =====");
            ok &= RunRegisterTest(server, logger);
            ok &= RunLoginTest(server, serverLog, logger);

            logger.Info("===== 5/5 心跳 =====");
            ok &= RunHeartbeatTest(server, logger);

            logger.Info(ok ? "全部自检通过" : "自检失败");
            return ok;
        }

        private static bool RunAccountStoreTest(string dataDirectory, INetLogger logger)
        {
            const string account = "selftest-user";
            var ok = true;

            var store = CreateAccountStore(dataDirectory, logger);
            var service = new AccountService(store, logger, 10000);

            // 先清干净，重复跑自检不会被"账号已存在"卡住
            store.DeleteAccount(account);

            // 增
            if (service.Register(account, "pwd-123456", out var registerError, "player-selftest"))
                logger.Info($"注册账号成功：{account}（存储={store.Kind}）-> OK");
            else
            {
                ok = false;
                logger.Error($"注册账号失败：{registerError}");
            }

            // 密码必须是哈希
            store.TryGetAccount(account, out var record);
            var hash = record?.PasswordHash ?? string.Empty;
            var hashed = hash.StartsWith("pbkdf2$", StringComparison.Ordinal);
            logger.Info($"密码以哈希保存（不存明文）：{hash.Substring(0, Math.Min(28, hash.Length))}… -> {(hashed ? "OK" : "FAIL")}");
            ok &= hashed;

            // 改：等级 / 经验 / 金币 / 属性
            service.SetLevel(account, 12);
            service.AddExp(account, 3456);
            service.AddGold(account, 999);
            service.SetAttribute(account, "strength", 18);
            service.SetAttribute(account, "agility", 7);

            // 道具增删改查
            service.AddItem(account, "sword_iron", 1);
            service.AddItem(account, "potion_hp", 5);
            service.AddItem(account, "potion_hp", 5);      // 叠加
            service.RemoveItem(account, "potion_hp", 2);   // 扣 2
            service.SetItemCount(account, "gold_key", 3);   // 直接设数量
            service.RemoveItem(account, "sword_iron", 1);   // 归零 → 这格被删掉

            var potions = service.GetItemCount(account, "potion_hp");
            var keys = service.GetItemCount(account, "gold_key");
            var sword = service.GetItemCount(account, "sword_iron");
            logger.Info($"道具：potion_hp={potions}（期望 8），gold_key={keys}（期望 3），sword_iron={sword}（期望 0）");

            if (potions == 8 && keys == 3 && sword == 0) logger.Info("道具增删改查 -> OK");
            else
            {
                ok = false;
                logger.Error("道具增删改查结果不对");
            }

            var insufficient = service.RemoveItem(account, "gold_key", 99);
            logger.Info($"数量不足时扣道具被拒绝：{!insufficient} -> {(!insufficient ? "OK" : "FAIL")}");
            ok &= !insufficient;

            // 查 + 持久化：重新打开存储实例（相当于重启后端）再读
            var reopened = CreateAccountStore(dataDirectory, logger);
            try
            {
                if (reopened.TryGetAccount(account, out var reloaded))
                {
                    var attributes = reloaded.Player.Attributes;
                    attributes.TryGetValue("strength", out var strength);

                    var allOk = reloaded.Player.Level == 12
                                && reloaded.Player.Exp == 3456
                                && reloaded.Player.Gold == 999
                                && strength == 18
                                && reloaded.Player.Items.Count == 2;

                    logger.Info($"重新打开存储后读取：等级={reloaded.Player.Level} 经验={reloaded.Player.Exp} "
                                + $"金币={reloaded.Player.Gold} 力量={strength} 道具格数={reloaded.Player.Items.Count}");
                    logger.Info($"持久化（重启后数据还在）-> {(allOk ? "OK" : "FAIL")}");
                    ok &= allOk;
                }
                else
                {
                    ok = false;
                    logger.Error("重新打开存储后读不到账号");
                }
            }
            finally
            {
                reopened.Dispose();
            }

            // 改密码
            if (service.ChangePassword(account, "pwd-123456", "pwd-654321", out var changeError))
            {
                store.TryGetAccount(account, out var after);
                var oldRejected = !PasswordHasher.Verify("pwd-123456", after.PasswordHash);
                var newAccepted = PasswordHasher.Verify("pwd-654321", after.PasswordHash);
                logger.Info($"改密码后：旧密码被拒={oldRejected}，新密码可用={newAccepted} -> {(oldRejected && newAccepted ? "OK" : "FAIL")}");
                ok &= oldRejected && newAccepted;
            }
            else
            {
                ok = false;
                logger.Error($"改密码失败：{changeError}");
            }

            // 删
            var deleted = service.Delete(account);
            var gone = !store.Exists(account);
            logger.Info($"删除账号：deleted={deleted}，再查已不存在={gone} -> {(deleted && gone ? "OK" : "FAIL")}");
            ok &= deleted && gone;

            store.Dispose();
            logger.Info(ok ? "账号存储自检通过" : "账号存储自检失败");
            return ok;
        }

        /// <summary>注册协议自检：非法账号 / 弱密码 / 正常注册 / 重复注册 / 注册后能登录。</summary>
        private static bool RunRegisterTest(GameServer server, INetLogger logger)
        {
            const string account = "reg-net-test";
            const string password = "pwd123456";
            var ok = true;

            server.Accounts.Delete(account);

            // 1) 账号太短
            using (var client = new GameClient(logger: new ConsoleNetLogger("CLIENT")))
            {
                var response = client.Register(NetConfig.DefaultHost, server.Port, "ab", password);
                var rejected = response != null && !response.Success && response.Reason == RegisterReasons.InvalidAccount;
                ok &= rejected;
                logger.Info($"账号过短被拒：reason='{response?.Reason}'，message='{response?.Message}' -> {(rejected ? "OK" : "FAIL")}");
            }

            // 2) 密码太短
            using (var client = new GameClient(logger: new ConsoleNetLogger("CLIENT")))
            {
                var response = client.Register(NetConfig.DefaultHost, server.Port, account, "123");
                var rejected = response != null && !response.Success && response.Reason == RegisterReasons.WeakPassword;
                ok &= rejected;
                logger.Info($"弱密码被拒：reason='{response?.Reason}'，message='{response?.Message}' -> {(rejected ? "OK" : "FAIL")}");
            }

            // 3) 正常注册
            using (var client = new GameClient(logger: new ConsoleNetLogger("CLIENT")))
            {
                var response = client.Register(NetConfig.DefaultHost, server.Port, account, password, "注册测试号");
                var registered = response != null && response.Success && !string.IsNullOrEmpty(response.PlayerId);
                ok &= registered;
                logger.Info($"注册成功：account='{account}'，playerId='{response?.PlayerId}'，"
                            + $"server='{response?.ServerName}' -> {(registered ? "OK" : "FAIL")}");
            }

            // 4) 重复注册同一个账号
            using (var client = new GameClient(logger: new ConsoleNetLogger("CLIENT")))
            {
                var response = client.Register(NetConfig.DefaultHost, server.Port, account, password);
                var rejected = response != null && !response.Success && response.Reason == RegisterReasons.AccountExists;
                ok &= rejected;
                logger.Info($"重复注册被拒：reason='{response?.Reason}'，message='{response?.Message}' -> {(rejected ? "OK" : "FAIL")}");
            }

            // 5) 关键：用刚注册的账号能登录成功
            using (var client = new GameClient(logger: new ConsoleNetLogger("CLIENT")))
            {
                var response = client.Login(NetConfig.DefaultHost, server.Port, account, password);
                var loggedIn = response != null && response.Success;
                ok &= loggedIn;
                logger.Info($"注册后的账号可以登录：playerId='{response?.PlayerId}' -> {(loggedIn ? "OK" : "FAIL")}");
            }

            // 6) 注册并登录（一站式，账号已存在时也应当成功）
            using (var client = new GameClient(logger: new ConsoleNetLogger("CLIENT")))
            {
                var response = client.RegisterAndLogin(NetConfig.DefaultHost, server.Port, account, password);
                var okHere = response != null && response.Success;
                ok &= okHere;
                logger.Info($"注册并登录（账号已存在时直接登录）：playerId='{response?.PlayerId}' -> {(okHere ? "OK" : "FAIL")}");
            }

            // 7) 全角数字 / 首尾空格：中文输入法和复制粘贴最常见的坑，规范化后应当照样成功
            const string numeric = "99887766";
            server.Accounts.Delete(numeric);

            using (var client = new GameClient(logger: new ConsoleNetLogger("CLIENT")))
            {
                // 中文输入法打出来的全角数字：９９８８７７６６
                var response = client.Register(NetConfig.DefaultHost, server.Port, "９９８８７７６６", password);
                var normalizedOk = response != null && response.Success
                                   && string.Equals(response.PlayerId, numeric, StringComparison.Ordinal);
                ok &= normalizedOk;
                logger.Info($"全角数字 '９９８８７７６６' 注册 -> playerId='{response?.PlayerId}'（应为 {numeric}）"
                            + $" -> {(normalizedOk ? "OK" : "FAIL")}");
            }

            using (var client = new GameClient(logger: new ConsoleNetLogger("CLIENT")))
            {
                // 带尾随空格 + 半角数字，应当被 trim 成同一个账号
                var response = client.Register(NetConfig.DefaultHost, server.Port, numeric + " ", password);
                var trimOk = response != null && !response.Success && response.Reason == RegisterReasons.AccountExists;
                ok &= trimOk;
                logger.Info($"带尾随空格的 '{numeric} ' 被识别成同一个账号：reason='{response?.Reason}' -> {(trimOk ? "OK" : "FAIL")}");
            }

            using (var client = new GameClient(logger: new ConsoleNetLogger("CLIENT")))
            {
                var response = client.Login(NetConfig.DefaultHost, server.Port, numeric, password);
                var loginOk = response != null && response.Success;
                ok &= loginOk;
                logger.Info($"用半角数字 '{numeric}' 能登录上面注册的账号：playerId='{response?.PlayerId}' -> {(loginOk ? "OK" : "FAIL")}");
            }

            // 中间夹空格这种确实不合法，但报错必须指出是哪个字符
            using (var client = new GameClient(logger: new ConsoleNetLogger("CLIENT")))
            {
                var response = client.Register(NetConfig.DefaultHost, server.Port, "12 34", password);
                var messageOk = response != null && !response.Success
                                && response.Message != null && response.Message.Contains("空格");
                ok &= messageOk;
                logger.Info($"账号中间带空格：message='{response?.Message}' -> {(messageOk ? "OK" : "FAIL")}");
            }

            server.Accounts.Delete(numeric);

            server.Accounts.Delete(account);
            logger.Info(ok ? "注册自检通过" : "注册自检失败");
            return ok;
        }

        private static bool RunLoginTest(GameServer server, RecordingLogger serverLog, INetLogger logger)
        {
            const string account = "login-test";
            var ok = true;

            server.Accounts.Delete(account);
            server.Accounts.Register(account, "correct-pwd", out _, "player-login-test");

            // 1) 密码错误：应当被拒并断开
            using (var badClient = new GameClient(logger: new ConsoleNetLogger("CLIENT")))
            {
                var response = badClient.Login(NetConfig.DefaultHost, server.Port, account, "wrong-password");

                if (response != null && !response.Success && response.Reason == LoginReasons.InvalidCredentials)
                    logger.Info($"密码错误被拒：reason='{response.Reason}'，message='{response.Message}' -> OK");
                else
                {
                    ok = false;
                    logger.Error($"密码错误没有被正确拒绝：Success={response?.Success}，Reason={response?.Reason}");
                }

                if (!badClient.IsConnected) logger.Info("登录失败后服务器已断开该连接 -> OK");
                else
                {
                    ok = false;
                    logger.Error("登录失败后连接仍然存在");
                }
            }

            // 2) 未登录就发系统消息：应当被拦下
            using (var rawClient = new GameClient(logger: new ConsoleNetLogger("CLIENT")))
            {
                rawClient.Connect(NetConfig.DefaultHost, server.Port);
                // 心跳不在"登录前允许列表"里，用它来验证拦截
                rawClient.Send(ProtocolId.SystemHeartbeat, new HeartbeatMessage { Sequence = 1, ClientTimeMs = 1 });
                System.Threading.Thread.Sleep(400);
                rawClient.Close();
            }

            if (serverLog.Contains("未登录就发了"))
                logger.Info("未登录发送其它协议被服务器拦截并告警 -> OK");
            else
            {
                ok = false;
                logger.Error("未登录发送协议没有被拦截");
            }

            // 3) 正确密码
            using (var client = new GameClient(logger: new ConsoleNetLogger("CLIENT")))
            {
                client.HeartbeatIntervalMs = 500;
                client.HeartbeatTimeoutMs = 500;

                if (client.IsLoggedIn)
                {
                    ok = false;
                    logger.Error("登录前的 IsLoggedIn 应该是 false");
                }

                var response = client.Login(NetConfig.DefaultHost, server.Port, account, "correct-pwd");

                if (response != null && response.Success && response.PlayerId == "player-login-test")
                    logger.Info($"登录成功：playerId='{response.PlayerId}'，sessionId='{response.SessionId}' -> OK");
                else
                {
                    ok = false;
                    logger.Error($"登录失败：{response?.Message}");
                }

                if (server.LoggedInClients.Count == 1) logger.Info("服务器侧已登录客户端数 = 1 -> OK");
                else
                {
                    ok = false;
                    logger.Error($"服务器侧已登录客户端数 = {server.LoggedInClients.Count}，应为 1");
                }

                System.Threading.Thread.Sleep(1200);
                if (client.HeartbeatAckCount >= 2)
                    logger.Info($"登录后心跳正常：1.2 秒收到 {client.HeartbeatAckCount} 次回包 -> OK");
                else
                {
                    ok = false;
                    logger.Error($"登录后心跳回包不足：{client.HeartbeatAckCount} 次");
                }
            }

            // 4) 账号不存在与密码错误返回同一提示，不泄漏账号是否存在
            server.Accounts.Delete(account);
            using (var client = new GameClient(logger: new ConsoleNetLogger("CLIENT")))
            {
                var response = client.Login(NetConfig.DefaultHost, server.Port, account, "whatever");
                var message = response?.Message;

                if (response != null && !response.Success && message == "账号或密码错误")
                    logger.Info("账号不存在与密码错误返回同一提示（不泄漏账号是否存在）-> OK");
                else
                {
                    ok = false;
                    logger.Error($"提示不符合预期：{message}");
                }
            }

            logger.Info(ok ? "登录自检通过" : "登录自检失败");
            return ok;
        }

        private static bool RunHeartbeatTest(GameServer server, INetLogger logger)
        {
            const string account = "heartbeat-test";
            var ok = true;
            var timedOut = 0;
            var timeoutMessage = new string[1];

            server.Accounts.Delete(account);
            server.Accounts.Register(account, "pwd-123456", out _, "player-heartbeat-test");

            using (var client = new GameClient(logger: new ConsoleNetLogger("CLIENT")))
            {
                client.HeartbeatIntervalMs = 500;
                client.HeartbeatTimeoutMs = 500;
                client.ConnectionTimedOut += (sender, message) =>
                {
                    timeoutMessage[0] = message;
                    System.Threading.Interlocked.Exchange(ref timedOut, 1);
                };

                var response = client.Login(NetConfig.DefaultHost, server.Port, account, "pwd-123456");
                if (response == null || !response.Success)
                {
                    logger.Error($"心跳测试登录失败：{response?.Message}");
                    return false;
                }

                System.Threading.Thread.Sleep(1200);
                logger.Info($"心跳正常阶段：收到 {client.HeartbeatAckCount} 次回包");

                server.Router.RegisterOrReplace(ProtocolId.SystemHeartbeat, (message, peer) =>
                {
                    // 故意不回包，模拟服务器卡死
                });
                logger.Info("已让服务器停止回心跳，等待客户端判定超时并断开…");

                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (System.Threading.Interlocked.CompareExchange(ref timedOut, 0, 0) == 0
                       && DateTime.UtcNow < deadline)
                {
                    System.Threading.Thread.Sleep(50);
                }

                var timeoutFired = System.Threading.Interlocked.CompareExchange(ref timedOut, 0, 0) == 1;
                logger.Info($"超时回调={timeoutFired}，客户端已断开={!client.IsConnected}");
                logger.Info($"超时消息 = '{timeoutMessage[0]}'");

                if (!timeoutFired)
                {
                    ok = false;
                    logger.Error("没有触发连接超时回调");
                }

                if (client.IsConnected)
                {
                    ok = false;
                    logger.Error("超时后客户端没有主动断开");
                }
            }

            server.Accounts.Delete(account);
            logger.Info(ok ? "心跳自检通过" : "心跳自检失败");
            return ok;
        }

        /// <summary>协议名总表自检：打印所有已登记的协议，并校验枚举与名字是否一一对应、有无重名、长度是否合法。</summary>
        private static bool RunProtocolTest(INetLogger logger)
        {
            logger.Info("协议名总表（唯一登记处：Assets/Scripts/Framework/NetFramework/Shared/Protocol/Protocols.cs）");
            logger.Info("编号                        线上名字              名称字节数   反查编号");

            var ok = true;
            foreach (var name in Protocols.AllNames)
            {
                var id = Protocols.IdOf(name);
                var bytes = System.Text.Encoding.UTF8.GetByteCount(name);
                var roundTripOk = id != ProtocolId.Unknown && Protocols.NameOf(id) == name;
                ok &= roundTripOk;

                logger.Info($"  {id,-26} {name,-20} {bytes,6}      {(roundTripOk ? "OK" : "FAIL")}");
            }

            if (Protocols.Validate(out var error)) logger.Info("协议表校验：通过（枚举与名字一一对应、无重名、长度合法）");
            else
            {
                ok = false;
                logger.Error("协议表校验失败：" + error);
            }

            logger.Info(ok ? "协议表自检通过" : "协议表自检失败");
            return ok;
        }

        /// <summary>
        /// 后端推送链路自检（不需要 Unity）：
        ///   服务器起在随机端口 → 客户端登录 → 服务端主动推数据
        ///   → 客户端 ProtocolHub 按协议分发给订阅者。
        /// 验的是"服务端推送 → 协议分发 → 系统处理"这一段；
        /// 再往后（逻辑事件 → 界面）由 GameController/EventBus 在 Unity 里接管。
        /// </summary>
        private static bool RunPushTest(INetLogger logger)
        {
            var dataDirectory = Path.Combine(Path.GetTempPath(), "gf-pushtest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataDirectory);

            var ok = true;
            GameServer server = null;
            GameClient client = null;
            ProtocolHub hub = null;
            PlayerPushService push = null;

            var infoCount = 0;
            var infoEchoCount = 0;
            PlayerInfoPush lastInfo = null;
            BagChangedPush lastBag = null;

            try
            {
                server = new GameServer(new GameServerOptions
                {
                    Host = NetConfig.DefaultHost,
                    Port = 0,                 // 0 = 让系统挑一个空闲端口，避免和正在跑的服务器撞车
                    ServerName = "pushtest",
                    Logger = logger,
                    AccountStore = new JsonAccountStore(Path.Combine(dataDirectory, "accounts.json"), logger),
                    LogReceivedFrames = false,
                    LogSentFrames = false
                });

                if (!server.TryStart(out var startError))
                {
                    logger.Error("推送自检：服务器启动失败：" + startError);
                    return false;
                }

                server.AddAccount("pushtest", "pwd123456", "player-pushtest");
                push = new PlayerPushService(server);

                client = new GameClient(
                    JsonMessageSerializer.Instance,
                    logger,
                    GameFramework.Net.Threading.ImmediateDispatcher.Instance);

                hub = new ProtocolHub(logger);
                hub.LogPush = true;

                // 同一条协议挂两个订阅者 —— ProtocolRouter 是单播，做不到这件事
                hub.Register<PlayerInfoPush>(ProtocolId.PlayerInfoPush, payload =>
                {
                    System.Threading.Interlocked.Increment(ref infoCount);
                    lastInfo = payload;
                }, "PlayerSystem");

                hub.Register<PlayerInfoPush>(ProtocolId.PlayerInfoPush, payload =>
                {
                    System.Threading.Interlocked.Increment(ref infoEchoCount);
                }, "DebugEcho");

                hub.Register<BagChangedPush>(ProtocolId.BagChangedPush, payload => lastBag = payload, "BagSystem");

                hub.Bind(client.Router);

                logger.Info($"推送自检：连接 127.0.0.1:{server.Port} 并登录…");
                var login = client.Login(NetConfig.DefaultHost, server.Port, "pushtest", "pwd123456");
                ok &= Check(logger, login != null && login.Success, "登录成功");

                // 1) 登录成功后服务端应该已经推了一份全量快照下来
                WaitUntil(() => lastInfo != null, 3000);
                ok &= Check(logger, lastInfo != null, "登录后收到全量推送 player.info.push");
                if (lastInfo != null)
                {
                    logger.Info($"     playerId='{lastInfo.PlayerId}'，等级={lastInfo.Level}，金币={lastInfo.Gold}，版本={lastInfo.Version}");
                    ok &= Check(logger, lastInfo.PlayerId == "player-pushtest", "推送内容正确（playerId）");
                    ok &= Check(logger, lastInfo.Level == 1, "推送内容正确（等级=1）");
                }

                ok &= Check(logger, infoCount >= 1 && infoEchoCount >= 1, "同一条推送被两个订阅者都收到了（多播）");

                // 2) 服务端改数据 → 自动推增量
                ok &= Check(logger, push.AddItem("pushtest", "1001", 5), "服务端加道具（改数据 + 自动推送）");
                WaitUntil(() => lastBag != null, 3000);
                ok &= Check(logger, lastBag != null, "收到背包推送 player.bag.changed");
                if (lastBag != null)
                {
                    logger.Info($"     道具={lastBag.ItemId}，数量={lastBag.Count}，本次变化={lastBag.Delta}，新增={lastBag.Added}，种类={lastBag.BagKinds}");
                    ok &= Check(logger,
                        lastBag.ItemId == "1001" && lastBag.Count == 5 && lastBag.Delta == 5 && lastBag.Added,
                        "背包推送内容正确（1001 ×5，本次 +5，属于新增）");
                }

                // 3) 再推一次：数量叠加，Added 变 false
                lastBag = null;
                ok &= Check(logger, push.AddItem("pushtest", "1001", 2), "服务端再加 2 个");
                WaitUntil(() => lastBag != null, 3000);
                if (lastBag != null)
                    ok &= Check(logger, lastBag.Count == 7 && lastBag.Delta == 2 && !lastBag.Added,
                        "背包增量正确（1001 ×7，本次 +2，不是新增）");

                // 4) 改等级 → 再推一次全量
                lastInfo = null;
                ok &= Check(logger, push.SetLevel("pushtest", 9), "服务端改等级");
                WaitUntil(() => lastInfo != null && lastInfo.Level == 9, 3000);
                ok &= Check(logger, lastInfo != null && lastInfo.Level == 9, "改等级后收到新的全量推送（等级=9）");

                logger.Info("推送自检：协议中心当前的订阅表");
                foreach (var line in hub.Dump().Split('\n'))
                {
                    if (line.Trim().Length == 0) continue;
                    logger.Info("  " + line.TrimEnd());
                }
            }
            catch (Exception ex)
            {
                logger.Error("推送自检异常", ex);
                ok = false;
            }
            finally
            {
                try { client?.Close("自检结束"); } catch (Exception) { }
                hub?.Dispose();
                push?.Dispose();
                server?.Dispose();

                try { if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, true); } catch (Exception) { }
            }

            logger.Info(ok ? "推送链路自检通过" : "推送链路自检失败");
            return ok;
        }

        /// <summary>自检里的一条断言：只打日志，失败也继续跑，好把问题一次看全。</summary>
        /// <summary>
        /// 配置表自检（不依赖 Unity）：用与服务端运行时完全相同的那份生成代码读 Assets/ConfigData，
        /// 顺便验证 主键索引、枚举、浮点、以及"表结构与代码不同步时会报错"的防护。
        /// </summary>
        /// <summary>
        /// 配置表自检（不依赖 Unity）：用与服务端运行时完全相同的那份生成代码读 Assets/ConfigDataServer，
        /// 验证 主键索引、枚举、浮点、数组、分端字段，以及"表结构与代码不同步时会报错"的防护。
        /// </summary>
        private static bool RunConfigTest(string directory, INetLogger logger)
        {
            logger.Info("=== 配置表自检 ===");

            if (string.IsNullOrEmpty(directory))
            {
                directory = ResolveConfigDirectory("ConfigDataServer", logger);
                if (directory == null)
                    directory = ResolveConfigDirectory("ConfigData", logger);
                if (directory == null) return false;
            }
            else
            {
                directory = Path.GetFullPath(directory);
                if (!Directory.Exists(directory))
                {
                    logger.Error("[配置表] 目录不存在：" + directory);
                    return false;
                }
            }

            ConfigDatabase database;
            try
            {
                database = ConfigFileLoader.Load(directory);
            }
            catch (Exception ex)
            {
                logger.Error("[配置表] 加载失败：" + ex.Message);
                return false;
            }

            logger.Info($"[配置表] 已加载 {database.LoadedTables.Count} 张表（目录：{directory}）");

            var ok = true;
            var item = database.Item;
            ok &= Check(logger, item != null, "Item 表存在");
            if (item == null) return false;

            ok &= Check(logger, item.Count == 2, $"Item 行数 = {item.Count}（期望 2）");

            var sword = item.Get(2);
            ok &= Check(logger, sword != null, "Item.Get(2) 能取到数据");
            if (sword != null)
            {
                ok &= Check(logger, sword.Name == "铁剑", $"Name = {sword.Name}（期望 铁剑）");
                ok &= Check(logger, sword.Quality == ItemQuality.Fine, $"Quality = {sword.Quality}（期望 Fine）");
                ok &= Check(logger, sword.Level == 5, $"Level = {sword.Level}（期望 5）");
                ok &= Check(logger, sword.Price == 120f, $"Price = {sword.Price}（期望 120）");
                ok &= Check(logger, sword.Stackable, $"Stackable = {sword.Stackable}（期望 true）");

                ok &= Check(logger, sword.Droplist != null && sword.Droplist.Length == 3,
                    $"Droplist（int[]）= {(sword.Droplist == null ? "null" : sword.Droplist.Length + " 项")}（期望 3 项）");
                if (sword.Droplist != null && sword.Droplist.Length == 3)
                {
                    ok &= Check(logger, sword.Droplist[0] == 2001 && sword.Droplist[2] == 2003,
                        $"Droplist = [{sword.Droplist[0]}, {sword.Droplist[1]}, {sword.Droplist[2]}]（期望 [2001, 2002, 2003]）");
                }

                ok &= Check(logger, sword.Tags != null && sword.Tags.Length == 2 && sword.Tags[0] == "剑" && sword.Tags[1] == "装备",
                    $"Tags（string[]）= {(sword.Tags == null ? "null" : string.Join("&", sword.Tags))}（期望 剑&装备）");

                // ServerOnly 是 ##group=S 的字段：只有服务端这份代码里有
                ok &= Check(logger, sword.ServerOnly == 20, $"ServerOnly（##group=S）= {sword.ServerOnly}（期望 20）");
            }

            var potion = item.Get(1001);
            ok &= Check(logger, potion != null && potion.Droplist != null && potion.Droplist.Length == 1 && potion.Droplist[0] == 3001,
                "红药水 Droplist = [3001]（单项数组）");

            ok &= Check(logger, item.Get(9999) == null, "不存在的 id 返回 null");
            ok &= Check(logger, item.Contains(1001), "Contains(1001) = true");

            var indexOk = true;
            for (var i = 0; i < item.Rows.Count; i++)
            {
                var row = item.Rows[i];
                if (!ReferenceEquals(row, item.Get(row.Id))) { indexOk = false; break; }
            }
            ok &= Check(logger, indexOk, "主键索引与顺序表一致");

            // 客户端数据里没有服务端字段，两份文件的哈希必然不同；用服务端代码读客户端文件应该被拦下
            var clientDirectory = ResolveConfigDirectory("ConfigData", logger);
            if (clientDirectory != null && !string.Equals(clientDirectory, directory, StringComparison.OrdinalIgnoreCase))
            {
                var clientPath = Path.Combine(clientDirectory, "Item.bytes");
                if (File.Exists(clientPath))
                {
                    var clientBytes = File.ReadAllBytes(clientPath);
                    var serverBytes = File.ReadAllBytes(Path.Combine(directory, "Item.bytes"));
                    if (!BytesEqual(clientBytes, serverBytes))
                    {
                        var rejected = false;
                        try { TbItem.Read(clientBytes); }
                        catch (ConfigFormatException) { rejected = true; }
                        ok &= Check(logger, rejected, "用服务端结构读客户端 Item.bytes 会被拦下（分端字段确实生效）");
                    }
                }
            }

            // 故意改掉 schemaHash，应该被 ConfigReader 拦下来
            var bytes = File.ReadAllBytes(Path.Combine(directory, "Item.bytes"));
            bytes[8] = (byte)(bytes[8] ^ 0xFF);
            var guarded = false;
            try { TbItem.Read(bytes); }
            catch (ConfigFormatException) { guarded = true; }
            ok &= Check(logger, guarded, "表结构与代码不同步时会报错（防止读到脏数据）");

            logger.Info(ok ? "=== 配置表自检全部通过 ===" : "=== 配置表自检存在失败项 ===");
            return ok;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static string ResolveConfigDirectory(string folderName, INetLogger logger)
        {
            var cwd = Directory.GetCurrentDirectory();
            var candidates = new[]
            {
                Path.Combine(cwd, "Assets", folderName),
                Path.Combine(cwd, "..", "Assets", folderName),
            };

            for (var i = 0; i < candidates.Length; i++)
            {
                var full = Path.GetFullPath(candidates[i]);
                if (Directory.Exists(full)) return full;
            }

            return null;
        }        private static bool Check(INetLogger logger, bool condition, string what)
        {
            if (condition) logger.Info("  [通过] " + what);
            else logger.Error("  [失败] " + what);
            return condition;
        }

        /// <summary>等条件成立，超时返回 false。</summary>
        private static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                System.Threading.Thread.Sleep(10);
            }
            return condition();
        }

        /// <summary>报文层自检：打印包头字段，并验证半包（分多次到达）与粘包（多条挤在一次接收里）。</summary>
        private static bool RunFrameTest(INetLogger logger)
        {
            var payload = new LoginRequest
            {
                Account = "frametest",
                Password = "pwd",
                ClientVersion = "1.0",
                ClientTimeMs = 1
            };

            var protocolName = Protocols.NameOf(ProtocolId.LoginRequest);
            var frame = FrameCodec.Encode(protocolName, JsonMessageSerializer.Instance.Serialize(payload));

            var nameLength = FrameCodec.ReadInt32BigEndian(frame, FrameLayout.NameLengthOffset);
            var bodyLength = FrameCodec.ReadInt32BigEndian(frame, FrameLayout.BodyLengthOffset(nameLength));
            var name = System.Text.Encoding.UTF8.GetString(frame, FrameLayout.NameOffset, nameLength);

            logger.Info($"整帧 {frame.Length} 字节 = 包头 {FrameLayout.BodyOffset(nameLength)} 字节 + 内容 {bodyLength} 字节");
            logger.Info($"包头：nameLen={nameLength}，name='{name}'，bodyLen={bodyLength}");

            var ok = true;

            var single = FrameCodec.Decode(frame);
            ok &= single.Protocol == protocolName;
            logger.Info($"一次性解析：协议='{single.Protocol}'，内容 {single.BodyLength} 字节 -> {(ok ? "OK" : "FAIL")}");

            var splitParser = new FrameParser();
            var splitFrames = new List<GameMessage>();
            for (var i = 0; i < frame.Length; i++)
                splitFrames.AddRange(splitParser.Append(frame, i, 1));
            var splitOk = splitFrames.Count == 1 && splitFrames[0].Protocol == protocolName;
            ok &= splitOk;
            logger.Info($"半包测试（逐字节喂 {frame.Length} 次）：拆出 {splitFrames.Count} 条 -> {(splitOk ? "OK" : "FAIL")}");

            var merged = new byte[frame.Length * 2];
            Buffer.BlockCopy(frame, 0, merged, 0, frame.Length);
            Buffer.BlockCopy(frame, 0, merged, frame.Length, frame.Length);
            var mergedFrames = new FrameParser().Append(merged, 0, merged.Length);
            var mergedOk = mergedFrames.Count == 2;
            ok &= mergedOk;
            logger.Info($"粘包测试（两条拼一起）：拆出 {mergedFrames.Count} 条 -> {(mergedOk ? "OK" : "FAIL")}");

            var broken = (byte[])frame.Clone();
            FrameCodec.WriteInt32BigEndian(broken, FrameLayout.NameLengthOffset, -5);
            var rejected = false;
            try
            {
                FrameCodec.Decode(broken);
            }
            catch (FrameFormatException)
            {
                rejected = true;
            }
            ok &= rejected;
            logger.Info($"非法报文测试（nameLen = -5）：{(rejected ? "已拒绝 OK" : "未拒绝 FAIL")}");

            // 安全：协议加密开启时，篡改消息体（改一个字节）必须导致解密失败、按非法帧拒绝
            if (NetConfig.EnableProtocolEncryption)
            {
                var tampered = (byte[])frame.Clone();
                tampered[tampered.Length - 3] ^= 0x01;   // 改 body 最后一个字节附近
                var tamperRejected = false;
                try
                {
                    FrameCodec.Decode(tampered);
                }
                catch (FrameFormatException)
                {
                    tamperRejected = true;
                }
                ok &= tamperRejected;
                logger.Info($"篡改测试（改消息体 1 字节）：{(tamperRejected ? "已拒绝 OK" : "未拒绝 FAIL")}");
            }
            else
            {
                logger.Info("篡改测试：协议加密已关闭，跳过（明文模式下改 body 无法检测）");
            }

            logger.Info(ok ? "报文层自检通过" : "报文层自检失败");
            return ok;
        }

        /// <summary>转发日志的同时把内容记下来，供自检断言用。</summary>
        private sealed class RecordingLogger : INetLogger
        {
            private readonly INetLogger _inner;
            private readonly List<string> _messages = new List<string>();
            private readonly object _lock = new object();

            public RecordingLogger(INetLogger inner) => _inner = inner;

            public bool Contains(string fragment)
            {
                lock (_lock)
                {
                    return _messages.Exists(m => m != null && m.Contains(fragment));
                }
            }

            public void Info(string message) => Record(message, () => _inner.Info(message));

            public void Warn(string message) => Record(message, () => _inner.Warn(message));

            public void Error(string message) => Record(message, () => _inner.Error(message));

            public void Error(string message, Exception exception) => Record(message, () => _inner.Error(message, exception));

            private void Record(string message, Action forward)
            {
                lock (_lock) _messages.Add(message);
                forward();
            }
        }
    }
}
#endif
