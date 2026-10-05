using GameFramework.Log;
using UnityEngine;

namespace GameFramework.Net.Client.Unity
{
    /// <summary>把网络日志转接到统一日志（GameLog）。Unity 的 Debug.Log 支持从后台线程调用。</summary>
    public sealed class UnityNetLogger : INetLogger
    {
        private readonly string _tag;
        private readonly bool _verbose;

        public UnityNetLogger(string tag = "NET", bool verbose = true)
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

        public void Error(string message, System.Exception exception)
            => GameLog.Error(_tag, message, exception);
    }
}
