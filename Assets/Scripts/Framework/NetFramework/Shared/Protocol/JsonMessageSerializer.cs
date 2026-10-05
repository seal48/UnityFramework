using System.Text;
using Newtonsoft.Json;

namespace GameFramework.Net.Protocol
{
    /// <summary>
    /// 默认序列化器：UTF-8 紧凑 JSON（不输出多余空格/换行）。
    /// 前后端共用同一份代码和同一个库（Unity 用 com.unity.nuget.newtonsoft-json）。
    /// </summary>
    public sealed class JsonMessageSerializer : IMessageSerializer
    {
        public static readonly JsonMessageSerializer Instance = new JsonMessageSerializer();

        private static readonly byte[] Empty = new byte[0];

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            Culture = System.Globalization.CultureInfo.InvariantCulture
        };

        public string Name => "json";

        public byte[] Serialize<T>(T payload)
        {
            if (payload == null) return Empty;
            var json = JsonConvert.SerializeObject(payload, Settings);
            return Encoding.UTF8.GetBytes(json);
        }

        public T Deserialize<T>(byte[] body)
        {
            if (body == null || body.Length == 0) return default(T);
            var json = Encoding.UTF8.GetString(body);
            return JsonConvert.DeserializeObject<T>(json, Settings);
        }
    }
}
