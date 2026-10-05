using System;

namespace GameFramework.Log
{
    /// <summary>
    /// 环形缓冲，保留最近 MaxHistory 条。游戏内控制台、崩溃上报都从这里取；
    /// Received 事件可以拿来做「出错弹提示」「错误埋点」。
    /// </summary>
    public sealed class MemoryLogSink : ILogSink
    {
        private readonly object _gate = new object();
        private readonly LogEntry[] _entries;

        private int _next;
        private int _count;

        public MemoryLogSink(int capacity)
        {
            _entries = new LogEntry[capacity < 1 ? 1 : capacity];
        }

        /// <summary>每收一条就触发一次。注意：可能在后台线程触发，别在这里碰 Unity 对象。</summary>
        public event Action<LogEntry> Received;

        public int Count
        {
            get { lock (_gate) return _count; }
        }

        /// <summary>0 = 最新。</summary>
        public LogEntry Get(int index)
        {
            lock (_gate)
            {
                if (index < 0 || index >= _count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                int i = _next - 1 - index;
                if (i < 0) i += _entries.Length;
                return _entries[i];
            }
        }

        public void Write(LogEntry entry)
        {
            lock (_gate)
            {
                _entries[_next] = entry;
                _next = (_next + 1) % _entries.Length;
                if (_count < _entries.Length) _count++;
            }

            Action<LogEntry> handler = Received;
            if (handler != null) handler(entry);
        }

        public void Clear()
        {
            lock (_gate)
            {
                _next = 0;
                _count = 0;
            }
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
            Received = null;
            Clear();
        }
    }
}