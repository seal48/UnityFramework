using System;
using System.Collections.Generic;
using System.Threading;
using GameFramework.Net.Protocol;
using GameFramework.Net.Server.Persistence;
using GameFramework.Net.Transport;

namespace GameFramework.Net.Server
{
    /// <summary>
    /// 玩家数据推送服务：把账号里的玩家数据"主动推"给该账号当前在线的客户端。
    ///
    /// 这就是后端主动推送的完整示例，两条触发路径：
    ///   1. 登录成功（GameServer.ClientLoggedIn）→ 推一份全量快照，客户端拿它做初始同步；
    ///   2. 服务端改了玩家数据（SetLevel / AddItem / ...）→ 推一条增量，客户端按需刷新。
    ///
    /// 客户端侧对应：ProtocolHub 收下来 → PlayerSystem / BagSystem 处理 → 发逻辑事件给界面。
    ///
    /// 用法（后端进程里）：
    ///   var push = new PlayerPushService(server);
    ///   push.AddItem("dev", "1001", 5);     // 改数据 + 自动推送，客户端界面自己会变
    /// </summary>
    public sealed class PlayerPushService : IDisposable
    {
        private readonly GameServer server;
        private readonly AccountService accounts;
        private readonly INetLogger logger;

        private long version;
        private bool disposed;

        public PlayerPushService(GameServer server, AccountService accounts = null, INetLogger logger = null)
        {
            this.server = server ?? throw new ArgumentNullException(nameof(server));
            this.accounts = accounts ?? server.Accounts;
            this.logger = logger ?? server.Logger;

            if (this.accounts == null)
                throw new InvalidOperationException("服务器没有账号服务，无法推送玩家数据");

            server.ClientLoggedIn += OnClientLoggedIn;
        }

        /// <summary>登录成功后是否自动推一份全量快照。</summary>
        public bool PushOnLogin { get; set; } = true;

        /// <summary>本服务改数据时是否自动推增量（关掉就只改数据不推送，方便对比调试）。</summary>
        public bool PushOnDataChanged { get; set; } = true;

        /// <summary>数据版本号，每推一次 +1。客户端用它判断收到的是不是新数据。</summary>
        public long Version { get { return Interlocked.Read(ref version); } }

        /// <summary>推送成功时触发（账号, 协议, 实际推给了几条连接），联调时打日志用。</summary>
        public event Action<string, ProtocolId, int> Pushed;

        // ---------------- 推全量 / 推增量 ----------------

        /// <summary>把该账号的玩家全量数据推给它在线的客户端，返回推给了几条连接。</summary>
        public int PushPlayerInfo(string account)
        {
            var normalized = AccountNameRules.Normalize(account);

            AccountRecord record;
            if (!accounts.TryGet(normalized, out record) || record == null)
            {
                logger.Warn($"推送玩家数据失败：账号不存在（account='{account}'）");
                return 0;
            }

            var data = record.Player ?? new PlayerData();
            var payload = new PlayerInfoPush
            {
                PlayerId = record.PlayerId,
                PlayerName = record.Account,
                Level = data.Level,
                Exp = data.Exp,
                Gold = data.Gold,
                BagKinds = data.Items != null ? data.Items.Count : 0,
                Version = Interlocked.Increment(ref version),
                ServerTimeMs = NowMs()
            };

            if (data.Attributes != null)
            {
                foreach (var pair in data.Attributes)
                    payload.Attributes.Add(new PlayerAttribute { Name = pair.Key, Value = pair.Value });
            }

            return Send(normalized, ProtocolId.PlayerInfoPush, payload);
        }

        /// <summary>推一条背包变化（增量）。</summary>
        public int PushBagChanged(string account, string itemId, int count, int delta, bool added)
        {
            var normalized = AccountNameRules.Normalize(account);

            var payload = new BagChangedPush
            {
                ItemId = itemId,
                Count = count,
                Delta = delta,
                Added = added,
                BagKinds = accounts.GetItems(normalized).Count,
                Version = Interlocked.Increment(ref version),
                ServerTimeMs = NowMs()
            };

            return Send(normalized, ProtocolId.BagChangedPush, payload);
        }

        // ---------------- 改数据 + 自动推送 ----------------

        public bool SetLevel(string account, int level)
        {
            if (!accounts.SetLevel(account, level)) return false;
            PushIfNeeded(account, () => PushPlayerInfo(account));
            return true;
        }

        public bool AddGold(string account, long delta)
        {
            if (!accounts.AddGold(account, delta)) return false;
            PushIfNeeded(account, () => PushPlayerInfo(account));
            return true;
        }

        public bool SetAttribute(string account, string name, long value)
        {
            if (!accounts.SetAttribute(account, name, value)) return false;
            PushIfNeeded(account, () => PushPlayerInfo(account));
            return true;
        }

        public bool AddItem(string account, string itemId, int count = 1)
        {
            var before = accounts.GetItemCount(account, itemId);
            if (!accounts.AddItem(account, itemId, count)) return false;

            var after = accounts.GetItemCount(account, itemId);
            PushIfNeeded(account, () => PushBagChanged(account, itemId, after, after - before, before <= 0 && after > 0));
            return true;
        }

        public bool RemoveItem(string account, string itemId, int count = 1)
        {
            var before = accounts.GetItemCount(account, itemId);
            if (!accounts.RemoveItem(account, itemId, count)) return false;

            var after = accounts.GetItemCount(account, itemId);
            PushIfNeeded(account, () => PushBagChanged(account, itemId, after, after - before, false));
            return true;
        }

        // ---------------- 内部 ----------------

        private void OnClientLoggedIn(IMessagePeer peer)
        {
            if (!PushOnLogin || peer == null) return;

            var session = PeerSession.Get(peer);
            if (session == null || !session.LoggedIn) return;

            PushPlayerInfo(session.Account);
        }

        private void PushIfNeeded(string account, Action push)
        {
            if (!PushOnDataChanged) return;
            push();
        }

        /// <summary>推给该账号所有在线连接，返回条数。账号没在线就只记一条日志。</summary>
        private int Send(string account, ProtocolId protocol, object payload)
        {
            var count = 0;

            foreach (var peer in server.LoggedInClients)
            {
                var session = PeerSession.Get(peer);
                if (session == null || !session.LoggedIn) continue;
                if (!string.Equals(session.Account, account, StringComparison.Ordinal)) continue;

                try
                {
                    peer.Send(protocol, payload);
                    count++;
                }
                catch (Exception ex)
                {
                    logger.Warn($"[{peer.PeerId}] 推送 {Protocols.NameOf(protocol)} 失败：{ex.Message}");
                }
            }

            if (count == 0)
                logger.Info($"账号 '{account}' 没有在线连接，{Protocols.NameOf(protocol)} 没有推出去（数据已保存，下次登录会同步）");

            Pushed?.Invoke(account, protocol, count);
            return count;
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            server.ClientLoggedIn -= OnClientLoggedIn;
        }
    }
}
