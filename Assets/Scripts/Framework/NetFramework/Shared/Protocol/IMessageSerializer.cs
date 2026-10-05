namespace GameFramework.Net.Protocol
{
    /// <summary>
    /// 消息内容的转译方式。默认实现是 JsonMessageSerializer（紧凑 JSON，可读性最好）；
    /// 想更省带宽可以换成 MessagePack/Protobuf 等，只要实现这个接口，前后端换成同一个实现即可。
    /// </summary>
    public interface IMessageSerializer
    {
        /// <summary>序列化器名称，用于日志和握手信息。</summary>
        string Name { get; }

        byte[] Serialize<T>(T payload);

        T Deserialize<T>(byte[] body);
    }
}
