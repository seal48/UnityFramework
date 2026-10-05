using System;
using System.Text;
using GameFramework.Security;

namespace GameFramework.Net.Protocol
{
    /// <summary>整帧的编码 / 解码（包头 + 消息内容），长度字段一律大端序。</summary>
    public static class FrameCodec
    {
        /// <summary>把“协议名称 + 消息内容字节”拼成一整帧。</summary>
        public static byte[] Encode(string protocol, byte[] body)
        {
            if (string.IsNullOrEmpty(protocol))
                throw new ArgumentException("协议名称不能为空", nameof(protocol));

            var nameBytes = Encoding.UTF8.GetBytes(protocol);
            if (nameBytes.Length < NetConfig.MinProtocolNameBytes || nameBytes.Length > NetConfig.MaxProtocolNameBytes)
                throw new ArgumentException($"协议名称字节长度非法：{nameBytes.Length}", nameof(protocol));

            var content = body ?? new byte[0];

            // 协议加密：加密「消息体」，包头（协议名 + 长度）保持明文，方便拆包 / 路由。
            // 加密会让 body 变长（AES 加 IV/填充），所以长度校验放在加密之后。
            if (NetConfig.EnableProtocolEncryption && content.Length > 0)
                content = SymmetricCrypto.Encrypt(content);

            if (content.Length > NetConfig.MaxBodyBytes)
                throw new ArgumentException($"消息内容超过上限：{content.Length} > {NetConfig.MaxBodyBytes}", nameof(body));

            var frame = new byte[FrameLayout.TotalLength(nameBytes.Length, content.Length)];

            // 包头第 1 段：协议名称的字节长度
            WriteInt32BigEndian(frame, FrameLayout.NameLengthOffset, nameBytes.Length);
            // 包头第 2 段：协议名称本身
            Buffer.BlockCopy(nameBytes, 0, frame, FrameLayout.NameOffset, nameBytes.Length);
            // 包头第 3 段：消息内容的字节长度
            WriteInt32BigEndian(frame, FrameLayout.BodyLengthOffset(nameBytes.Length), content.Length);
            // 包头之后：消息内容
            if (content.Length > 0)
                Buffer.BlockCopy(content, 0, frame, FrameLayout.BodyOffset(nameBytes.Length), content.Length);

            return frame;
        }

        /// <summary>把对象负载先序列化再编码成整帧。</summary>
        public static byte[] Encode<T>(string protocol, T payload, IMessageSerializer serializer)
        {
            if (serializer == null) throw new ArgumentNullException(nameof(serializer));
            return Encode(protocol, serializer.Serialize(payload));
        }

        /// <summary>
        /// 尝试从缓冲区里解出一帧。
        /// 返回 false 表示“数据还不够，等下一次接收”；
        /// 包头长度字段非法时抛 FrameFormatException（调用方应断开连接）。
        /// </summary>
        public static bool TryDecode(byte[] buffer, int offset, int count, out GameMessage message, out int consumedBytes)
        {
            message = null;
            consumedBytes = 0;

            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset), "缓冲区区间非法");

            // 至少要能读到 nameLen
            if (count < FrameLayout.NameOffset + FrameLayout.LengthFieldBytes) return false;

            var nameLength = ReadInt32BigEndian(buffer, offset + FrameLayout.NameLengthOffset);
            if (nameLength < NetConfig.MinProtocolNameBytes || nameLength > NetConfig.MaxProtocolNameBytes)
                throw new FrameFormatException($"协议名称长度字段非法：{nameLength}");

            var bodyLengthOffset = offset + FrameLayout.BodyLengthOffset(nameLength);
            // 协议名称尚未收全
            if (count < FrameLayout.BodyLengthOffset(nameLength) + FrameLayout.LengthFieldBytes) return false;

            var bodyLength = ReadInt32BigEndian(buffer, bodyLengthOffset);
            if (bodyLength < 0 || bodyLength > NetConfig.MaxBodyBytes)
                throw new FrameFormatException($"消息内容长度字段非法：{bodyLength}");

            var totalLength = FrameLayout.TotalLength(nameLength, bodyLength);
            // 内容尚未收全（半包）
            if (count < totalLength) return false;

            var protocol = Encoding.UTF8.GetString(buffer, offset + FrameLayout.NameOffset, nameLength);
            var body = new byte[bodyLength];
            if (bodyLength > 0)
                Buffer.BlockCopy(buffer, offset + FrameLayout.BodyOffset(nameLength), body, 0, bodyLength);

            // 协议加密：解密消息体。解密失败（被改过 / 密钥不一致）→ 按非法帧断开连接
            if (NetConfig.EnableProtocolEncryption && body.Length > 0)
            {
                byte[] plain = SymmetricCrypto.Decrypt(body);
                if (plain == null)
                    throw new FrameFormatException("消息体解密失败（可能被篡改或密钥不一致）");
                body = plain;
            }

            message = new GameMessage(protocol, body);
            consumedBytes = totalLength;
            return true;
        }

        /// <summary>解析一个正好只包含一帧的缓冲区（常用于单测/工具）。</summary>
        public static GameMessage Decode(byte[] frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (!TryDecode(frame, 0, frame.Length, out var message, out var consumed))
                throw new FrameFormatException("缓冲区数据不足，无法解析出一帧");
            if (consumed != frame.Length)
                throw new FrameFormatException($"缓冲区里有多余数据：已解析 {consumed} 字节，共 {frame.Length} 字节");
            return message;
        }

        internal static void WriteInt32BigEndian(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        internal static int ReadInt32BigEndian(byte[] buffer, int offset)
        {
            return (buffer[offset] << 24)
                 | (buffer[offset + 1] << 16)
                 | (buffer[offset + 2] << 8)
                 | buffer[offset + 3];
        }
    }
}
