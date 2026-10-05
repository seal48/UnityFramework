using System;
using GameFramework.Net.Protocol;

namespace GameFramework.Net.Server
{
    /// <summary>
    /// 服务器内置的系统协议：
    ///   system.ping      → 回 system.pong（手动测往返延迟）
    ///   system.heartbeat → 回 system.heartbeat.ack（客户端靠它判断连接是否还活着）
    ///   system.bye       → 只记日志（客户端超时/主动断开前的最后一条消息）
    /// 协议名统一在 Shared/Protocol/Protocols.cs 里登记，这里只用编号。
    /// </summary>
    public static class SystemProtocols
    {
        /// <summary>协议版本号，登录回包里带回去，前后端可以做兼容判断。</summary>
        public const int ProtocolVersion = 1;

        public static void Register(GameServer server)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));

            server.Router.RegisterOrReplace(ProtocolId.SystemPing, (message, peer) =>
            {
                var ping = message.Deserialize<PingRequest>(peer.Serializer);
                peer.Send(ProtocolId.SystemPong, new PongResponse
                {
                    ClientTimeMs = ping?.ClientTimeMs ?? 0,
                    ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            });

            server.Router.RegisterOrReplace(ProtocolId.SystemHeartbeat, (message, peer) =>
            {
                var beat = message.Deserialize<HeartbeatMessage>(peer.Serializer);

                // 心跳只做最轻量的回包：不写日志（默认 5 秒一条，写日志会刷屏）
                peer.Send(ProtocolId.SystemHeartbeatAck, new HeartbeatAck
                {
                    Sequence = beat?.Sequence ?? 0,
                    ClientTimeMs = beat?.ClientTimeMs ?? 0,
                    ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            });

            server.Router.RegisterOrReplace(ProtocolId.SystemBye, (message, peer) =>
            {
                var bye = message.Deserialize<ByeMessage>(peer.Serializer);
                server.Logger.Info($"[{peer.PeerId}] 客户端断开：{bye?.Reason}");
            });
        }
    }
}
