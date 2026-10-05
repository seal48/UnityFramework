using System;
using GameFramework.Timer;

namespace GameFramework.Net
{
    /// <summary>
    /// 日志出口。Unity 侧用 UnityNetLogger，独立后端进程用 ConsoleNetLogger。
    ///
    /// 继承计时框架的 <see cref="ITimerLogger"/>：计时内核只认 ITimerLogger，
    /// 网络层把这个 logger 直接传给它就行 —— 这样计时框架不必反过来依赖网络框架
    /// （原来的反向依赖就是这么来的）。
    /// </summary>
    public interface INetLogger : ITimerLogger
    {
    }

    /// <summary>丢弃全部日志。</summary>
    public sealed class NullNetLogger : INetLogger
    {
        public static readonly NullNetLogger Instance = new NullNetLogger();

        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(string message, Exception exception) { }
    }

    /// <summary>控制台日志，带时间戳与标签（后端独立进程默认使用）。</summary>
    public sealed class ConsoleNetLogger : INetLogger
    {
        private readonly string _tag;

        public ConsoleNetLogger(string tag = "NET")
        {
            _tag = tag;
        }

        public void Info(string message) => Write("INFO ", message);

        public void Warn(string message) => Write("WARN ", message);

        public void Error(string message) => Write("ERROR", message);

        public void Error(string message, Exception exception)
            => Write("ERROR", message + Environment.NewLine + exception);

        private void Write(string level, string message)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}][{level}][{_tag}] {message}");
        }
    }
}
