using System.Text;

namespace GameFramework.Net.Protocol
{
    /// <summary>
    /// 纯文本序列化器：string 直接写 UTF-8 原文，不做 JSON 转义（能省掉引号和转义带来的额外字节）。
    /// 其它类型回落到 JSON。可作为“换一种更省带宽的内容形式”的示例。
    /// </summary>
    public sealed class PlainTextMessageSerializer : IMessageSerializer
    {
        public static readonly PlainTextMessageSerializer Instance = new PlainTextMessageSerializer();

        private static readonly byte[] Empty = new byte[0];

        public string Name => "text";

        public byte[] Serialize<T>(T payload)
        {
            if (payload == null) return Empty;

            if (payload is string text) return Encoding.UTF8.GetBytes(text);
            if (payload is byte[] bytes) return bytes;

            return JsonMessageSerializer.Instance.Serialize(payload);
        }

        public T Deserialize<T>(byte[] body)
        {
            if (typeof(T) == typeof(string))
                return (T)(object)(body == null || body.Length == 0 ? string.Empty : Encoding.UTF8.GetString(body));

            if (typeof(T) == typeof(byte[]))
                return (T)(object)(body ?? Empty);

            return JsonMessageSerializer.Instance.Deserialize<T>(body);
        }
    }
}
