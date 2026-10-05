using UnityEngine;

namespace GameFramework.Log
{
    /// <summary>写 Unity 控制台。真机上就是 logcat / Xcode 控制台。Debug.Log 允许从后台线程调用。</summary>
    public sealed class UnityConsoleSink : ILogSink
    {
        private readonly bool _colored;

        public UnityConsoleSink(bool colored)
        {
            _colored = colored;
        }

        public void Write(LogEntry entry)
        {
            string line = entry.ToLine();

            if (_colored)
                line = "<color=" + entry.ColorHex + ">" + line + "</color>";

            // 带上 context，控制台里点一下就能选中对应物体
            if (entry.Level == LogLevel.Error || entry.Level == LogLevel.Fatal)
                Debug.LogError(line, entry.Context);
            else if (entry.Level == LogLevel.Warn)
                Debug.LogWarning(line, entry.Context);
            else
                Debug.Log(line, entry.Context);
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }
}