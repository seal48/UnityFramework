namespace GameFramework.Net.Protocol
{
    /// <summary>
    /// 一条消息（帧）的字节布局。包头 + 包头后的消息内容：
    ///
    ///   +----------------+---------------------+----------------+-------------------+
    ///   | nameLen : 4B   | protocol : nameLen B | bodyLen : 4B   | body : bodyLen B  |
    ///   +----------------+---------------------+----------------+-------------------+
    ///   |&lt;---------- 包头 ----------&gt;|&lt;------- 消息内容 -------&gt;|
    ///
    /// - nameLen  ：协议名称的字节长度（包头第一段就是它，UTF-8 编码）
    /// - bodyLen  ：消息内容的字节长度，接收方靠它判断“剩下的内容”取多少字节
    /// - 所有长度字段均为大端序 Int32
    ///
    /// 想改协议布局（例如把 bodyLen 挪到最前面、或加版本号/校验位），只需要改本文件与 FrameCodec。
    /// </summary>
    public static class FrameLayout
    {
        /// <summary>nameLen 字段的偏移。</summary>
        public const int NameLengthOffset = 0;

        /// <summary>协议名称的起始偏移。</summary>
        public const int NameOffset = 4;

        /// <summary>单个长度字段占用的字节数。</summary>
        public const int LengthFieldBytes = 4;

        /// <summary>bodyLen 字段的偏移（取决于协议名称有多长）。</summary>
        public static int BodyLengthOffset(int nameLength) => NameOffset + nameLength;

        /// <summary>消息内容的起始偏移。</summary>
        public static int BodyOffset(int nameLength) => BodyLengthOffset(nameLength) + LengthFieldBytes;

        /// <summary>整帧总长度。</summary>
        public static int TotalLength(int nameLength, int bodyLength) => BodyOffset(nameLength) + bodyLength;

        /// <summary>只凭包头就能判断出的最小帧长度。</summary>
        public const int MinFrameBytes = NameOffset + LengthFieldBytes + LengthFieldBytes;
    }
}
