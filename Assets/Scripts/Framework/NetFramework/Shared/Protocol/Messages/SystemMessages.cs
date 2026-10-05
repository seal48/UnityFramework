using System;

namespace GameFramework.Net.Protocol
{
    /// <summary>system.ping 的消息内容（手动测往返延迟用）。</summary>
    [Serializable]
    public class PingRequest
    {
        public long ClientTimeMs;
    }

    /// <summary>system.pong 的消息内容。</summary>
    [Serializable]
    public class PongResponse
    {
        public long ClientTimeMs;
        public long ServerTimeMs;
    }

    /// <summary>system.heartbeat 的消息内容。</summary>
    [Serializable]
    public class HeartbeatMessage
    {
        public int Sequence;
        public long ClientTimeMs;
    }

    /// <summary>system.heartbeat.ack 的消息内容。</summary>
    [Serializable]
    public class HeartbeatAck
    {
        public int Sequence;
        public long ClientTimeMs;
        public long ServerTimeMs;
    }

    /// <summary>system.bye 的消息内容：客户端断开前告诉服务器原因。</summary>
    [Serializable]
    public class ByeMessage
    {
        public string Reason;
    }
}
