using System;
using System.Collections.Generic;

namespace GameFramework.Net.Protocol
{
    /// <summary>
    /// 流式拆包器。
    /// TCP 是字节流：一次 Receive 可能收到半条消息、正好一条、或者好几条粘在一起。
    /// 这里把收到的字节累积起来，按包头给出的长度切出完整消息。
    /// </summary>
    public sealed class FrameParser
    {
        private byte[] _buffer;
        private int _start;
        private int _end;

        public FrameParser(int initialCapacity = NetConfig.DefaultIoBufferBytes)
        {
            _buffer = new byte[Math.Max(256, initialCapacity)];
        }

        /// <summary>当前还留在缓冲区里、尚未组成完整消息的字节数。</summary>
        public int BufferedBytes => _end - _start;

        /// <summary>追加一段刚收到的字节，返回本次能拆出的所有完整消息。</summary>
        public List<GameMessage> Append(byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset + count > data.Length)
                throw new ArgumentOutOfRangeException(nameof(offset), "待追加的区间非法");

            var frames = new List<GameMessage>();

            if (count > 0)
            {
                EnsureWritable(count);
                Buffer.BlockCopy(data, offset, _buffer, _end, count);
                _end += count;
            }

            while (true)
            {
                if (!FrameCodec.TryDecode(_buffer, _start, _end - _start, out var message, out var consumed))
                    break;

                frames.Add(message);
                _start += consumed;
            }

            if (_start == _end)
            {
                // 全部消费完，指针归零，避免缓冲区无限增长
                _start = 0;
                _end = 0;
            }

            return frames;
        }

        public void Reset()
        {
            _start = 0;
            _end = 0;
        }

        private void EnsureWritable(int count)
        {
            // 先把已消费的空间回收掉
            if (_start > 0)
            {
                var alive = _end - _start;
                if (alive > 0) Buffer.BlockCopy(_buffer, _start, _buffer, 0, alive);
                _start = 0;
                _end = alive;
            }

            var required = _end + count;
            if (required <= _buffer.Length) return;

            var doubled = _buffer.Length * 2;
            var newSize = Math.Max(doubled, required);
            Array.Resize(ref _buffer, newSize);
        }
    }
}
