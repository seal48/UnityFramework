using System;
using GameFramework.Net.Protocol;

namespace GameFramework.Net.Server
{
    /// <summary>
    /// 一条连接上的会话状态，挂在 PeerConnection.Tag 上。
    /// 登录成功后 LoggedIn = true，之后再开其它协议。
    /// </summary>
    public sealed class PeerSession
    {
        public bool LoggedIn { get; internal set; }

        public string Account { get; internal set; }

        public string PlayerId { get; internal set; }

        public string SessionId { get; internal set; }

        public DateTime LoginAtUtc { get; internal set; }

        /// <summary>取这条连接的会话；没有就建一个（还没登录）。</summary>
        public static PeerSession GetOrCreate(IMessagePeer peer)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));

            if (peer.Tag is PeerSession session) return session;

            session = new PeerSession();
            peer.Tag = session;
            return session;
        }

        /// <summary>取这条连接的会话，没有则返回 null。</summary>
        public static PeerSession Get(IMessagePeer peer) => peer?.Tag as PeerSession;

        public static bool IsLoggedIn(IMessagePeer peer) => Get(peer)?.LoggedIn ?? false;
    }
}
