using System;

namespace GameFramework.Timer
{
    /// <summary>
    /// 计时内核的日志出口（纯 C#，客户端 / 服务端都能用）。
    ///
    /// 内核不依赖任何具体日志实现，只认这个口：
    ///   客户端 —— UnityTimerLogger（走 GameLog）
    ///   服务端 —— 直接把 INetLogger 传进来（INetLogger 继承了本接口）
    /// 这样计时框架不会反过来依赖网络框架。
    /// </summary>
    public interface ITimerLogger
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
        void Error(string message, Exception exception);
    }

    /// <summary>丢弃全部日志。</summary>
    public sealed class NullTimerLogger : ITimerLogger
    {
        public static readonly NullTimerLogger Instance = new NullTimerLogger();

        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(string message, Exception exception) { }
    }
}
