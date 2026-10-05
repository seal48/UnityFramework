namespace GameFramework.Net.Protocol
{
    /// <summary>一条网络连接的对端抽象（服务端里代表某个客户端，客户端里代表服务器）。</summary>
    public interface IMessagePeer
    {
        /// <summary>对端标识，服务端形如 "client-1"。</summary>
        string PeerId { get; }

        bool IsConnected { get; }

        IMessageSerializer Serializer { get; }

        /// <summary>本端的临时数据（服务端常用来挂玩家对象）。</summary>
        object Tag { get; set; }

        /// <summary>发送一条消息；payload 为 null 表示只有协议名、没有内容。</summary>
        void Send(string protocol, object payload = null);

        void SendRaw(GameMessage message);

        void Close(string reason);
    }
}
