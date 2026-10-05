using System;
using System.Text;

namespace GameFramework.Net.Protocol
{
    /// <summary>一条已经拆包完成的消息：协议名称 + 原始消息内容字节。</summary>
    public sealed class GameMessage
    {
        private static readonly byte[] EmptyBody = new byte[0];

        public GameMessage(string protocol, byte[] body)
        {
            if (string.IsNullOrEmpty(protocol))
                throw new ArgumentException("协议名称不能为空", nameof(protocol));

            Protocol = protocol;
            Body = body ?? EmptyBody;

            var nameBytes = Encoding.UTF8.GetByteCount(protocol);
            if (nameBytes < NetConfig.MinProtocolNameBytes || nameBytes > NetConfig.MaxProtocolNameBytes)
                throw new ArgumentException(
                    $"协议名称字节长度必须在 {NetConfig.MinProtocolNameBytes}~{NetConfig.MaxProtocolNameBytes} 之间，当前 {nameBytes}", nameof(protocol));

            if (Body.Length > NetConfig.MaxBodyBytes)
                throw new ArgumentException($"消息内容 {Body.Length} 字节，超过上限 {NetConfig.MaxBodyBytes}", nameof(body));
        }

        /// <summary>协议名称（包头里的 name）。</summary>
        public string Protocol { get; }

        /// <summary>协议名称对应的编号（没登记过的协议是 ProtocolId.Unknown）。</summary>
        public ProtocolId Id => Protocols.IdOf(Protocol);

        /// <summary>消息内容的原始字节（默认是 UTF-8 的 JSON）。</summary>
        public byte[] Body { get; }

        public int BodyLength => Body.Length;

        /// <summary>整帧的字节长度（含包头），用于统计/日志。</summary>
        public int FrameLength => FrameLayout.TotalLength(Encoding.UTF8.GetByteCount(Protocol), Body.Length);

        /// <summary>
        /// 是否已经被消费掉（例如被 GameClient.SendRequest 的同步等待者收走）。
        /// 消费过的消息不再进路由派发，也就不会报“未注册的协议”。
        /// </summary>
        public bool Consumed { get; set; }

        public string BodyAsText() => Body.Length == 0 ? string.Empty : Encoding.UTF8.GetString(Body);

        public T Deserialize<T>(IMessageSerializer serializer)
        {
            if (serializer == null) throw new ArgumentNullException(nameof(serializer));
            return serializer.Deserialize<T>(Body);
        }

        public override string ToString()
            => $"{{协议='{Protocol}', 内容={Body.Length}字节, 整帧={FrameLength}字节}}";
    }
}
