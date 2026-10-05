namespace GameFramework.Net
{
    /// <summary>
    /// 网络框架的全局常量。
    /// 所有长度字段都使用大端序（网络字节序 Big-Endian），方便跨平台/跨语言对接。
    /// </summary>
    public static class NetConfig
    {
        public const string DefaultHost = "127.0.0.1";
        public const int DefaultPort = 7777;

        /// <summary>协议名称允许的字节长度范围（包头第一段 nameLen 的合法区间）。</summary>
        public const int MinProtocolNameBytes = 1;
        public const int MaxProtocolNameBytes = 128;

        /// <summary>单条消息内容的最大字节数，超出即视为非法帧并断开连接。</summary>
        public const int MaxBodyBytes = 1024 * 1024;

        /// <summary>
        /// 协议消息体加密开关（AES，见 SecurityFramework）。
        /// 开启：body 序列化后加密、解析前解密，抓包看到的是密文；
        /// 关闭：明文，方便联调抓包。**前后端必须一致**，否则对不上。
        /// </summary>
        public const bool EnableProtocolEncryption = true;

        public const int DefaultConnectTimeoutMs = 5000;
        public const int DefaultSendTimeoutMs = 5000;
        public const int DefaultRequestTimeoutMs = 5000;

        /// <summary>客户端心跳间隔：每隔这么久发一次心跳。</summary>
        public const int DefaultHeartbeatIntervalMs = 5000;

        /// <summary>心跳回包超时：超过这么久没收到回包就判定连接超时并断开。</summary>
        public const int DefaultHeartbeatTimeoutMs = 5000;

        /// <summary>单次 Socket.Receive 的接收缓冲区大小。</summary>
        public const int DefaultIoBufferBytes = 16 * 1024;

        public const int DefaultListenBacklog = 64;
        public const int DefaultMaxConnections = 128;
    }
}
