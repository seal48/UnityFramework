using System;
using System.Collections.Generic;
using System.Text;
using GameFramework.Core;
using GameFramework.Event;
using GameFramework.Net;
using GameFramework.Net.Client;
using GameFramework.Net.Client.Unity;

namespace GameFramework.Business
{
    /// <summary>
    /// 业务层总入口：把"服务端推送"按系统拆开处理，处理完再发逻辑事件给界面。
    ///
    /// 完整链路（从后端到界面）：
    ///   后端 PlayerPushService 推 player.info.push / player.bag.changed
    ///   → 客户端 PeerConnection → ProtocolRouter
    ///   → ProtocolHub（按协议分给系统，一条协议可以多个系统听）
    ///   → PlayerSystem / BagSystem（处理数据、写 PlayerModel）
    ///   → 事件总线 PlayerInfoChangedEvent / BagChangedEvent
    ///   → MainPanel 这类界面 Listen，只管刷新显示
    ///
    /// 由 GameController 统一创建 / 销毁，业务代码用 GameController.Instance.Business 访问。
    /// </summary>
    public sealed class BusinessManager : IGameModule, IDisposable
    {
        private readonly GameClientBehaviour client;
        private readonly IEventBus events;
        private readonly List<GameSystem> systems = new List<GameSystem>();
        private bool disposed;

        public BusinessManager(GameClientBehaviour client, IEventBus events, INetLogger logger = null)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.events = events ?? throw new ArgumentNullException(nameof(events));

            Hub = new ProtocolHub(logger ?? NullNetLogger.Instance);
            Player = new PlayerModel();

            systems.Add(new PlayerSystem(Player));
            systems.Add(new BagSystem(Player));

            for (int i = 0; i < systems.Count; i++)
                systems[i].Bind(Hub, events);

            client.ClientCreated += OnClientCreated;
            client.Disconnected += OnDisconnected;

            // 构造的时候可能已经有客户端了（运行中重建业务层），补绑一次
            if (client.Client != null)
                Hub.Bind(client.Client.Router);
        }

        /// <summary>协议分发中心：想知道某条推送被谁处理，看 Hub.Dump()。</summary>
        public ProtocolHub Hub { get; private set; }

        /// <summary>客户端玩家数据（界面只读）。</summary>
        public PlayerModel Player { get; private set; }

        public IReadOnlyList<GameSystem> Systems { get { return systems; } }

        private void OnClientCreated(GameClientBehaviour behaviour, GameClient gameClient)
        {
            // 每次登录都会重建 GameClient（Router 跟着换），把订阅挂到新路由表上
            Hub.Bind(gameClient.Router);
        }

        private void OnDisconnected(GameClientBehaviour behaviour, string reason)
        {
            // 断线后本地数据作废，否则重连前界面还在显示上一个账号的数据。
            // （登录成功后服务端会立刻推一份全量快照，数据自然就回来了）
            Player.Reset();

            if (events != null)
                events.Post(new PlayerOfflineEvent { Reason = reason });
        }

        /// <summary>导出协议中心 + 各系统的处理情况，排查"这条推送到底有没有人处理"用。</summary>
        public string Dump()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Hub.Dump());

            for (int i = 0; i < systems.Count; i++)
                sb.Append("  · ").Append(systems[i].Name)
                  .Append(" 处理了 ").Append(systems[i].HandledCount).Append(" 条\n");

            return sb.ToString();
        }

        /// <summary>是否可用（业务层构造出来即可用；Shutdown 之后为 false）。</summary>
        public bool IsInitialized { get { return !disposed; } }

        /// <summary>关闭并释放。幂等。</summary>
        public void Shutdown()
        {
            if (disposed) return;
            disposed = true;

            client.ClientCreated -= OnClientCreated;
            client.Disconnected -= OnDisconnected;

            for (int i = 0; i < systems.Count; i++)
                systems[i].Dispose();

            systems.Clear();
            Hub.Dispose();
        }

        /// <summary>IDisposable 转发到 <see cref="Shutdown"/>，两种写法行为一致。</summary>
        public void Dispose() { Shutdown(); }
    }
}
