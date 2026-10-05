using System;
using GameFramework.Log;

namespace GameFramework.Timer
{
    /// <summary>
    /// 计时器的 Unity 日志出口：转接到统一日志（GameLog）。
    /// 计时框架自带的实现 —— 不再借用网络框架的 UnityNetLogger，避免反向依赖。
    /// </summary>
    public sealed class UnityTimerLogger : ITimerLogger
    {
        private readonly string _tag;
        private readonly bool _verbose;

        public UnityTimerLogger(string tag = "Timer", bool verbose = true)
        {
            _tag = tag;
            _verbose = verbose;
        }

        public void Info(string message)
        {
            if (_verbose) GameLog.Info(_tag, message);
        }

        public void Warn(string message) => GameLog.Warn(_tag, message);

        public void Error(string message) => GameLog.Error(_tag, message);

        public void Error(string message, Exception exception)
            => GameLog.Error(_tag, message, exception);
    }
}
