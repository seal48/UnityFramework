using System;

namespace GameFramework.Log
{
    /// <summary>
    /// 绑定了模块标签的日志器。模块里存一份静态实例就行，别每条日志现拼标签字符串。
    /// </summary>
    public sealed class ModuleLog
    {
        public string Tag { get; private set; }

        public ModuleLog(string tag)
        {
            Tag = string.IsNullOrEmpty(tag) ? LogTag.Root : tag;
        }

        public bool IsEnabled(LogLevel level)
        {
            return GameLog.IsEnabled(level, Tag);
        }

        public void Debug(string message)
        {
            GameLog.Write(LogLevel.Debug, Tag, message, null, null);
        }

        public void Debug(string message, UnityEngine.Object context)
        {
            GameLog.Write(LogLevel.Debug, Tag, message, null, context);
        }

        public void Info(string message)
        {
            GameLog.Write(LogLevel.Info, Tag, message, null, null);
        }

        public void Info(string message, UnityEngine.Object context)
        {
            GameLog.Write(LogLevel.Info, Tag, message, null, context);
        }

        public void Warn(string message)
        {
            GameLog.Write(LogLevel.Warn, Tag, message, null, null);
        }

        public void Warn(string message, UnityEngine.Object context)
        {
            GameLog.Write(LogLevel.Warn, Tag, message, null, context);
        }

        public void Error(string message)
        {
            GameLog.Write(LogLevel.Error, Tag, message, null, null);
        }

        public void Error(string message, UnityEngine.Object context)
        {
            GameLog.Write(LogLevel.Error, Tag, message, null, context);
        }

        public void Error(string message, Exception exception)
        {
            GameLog.Write(LogLevel.Error, Tag, message, exception, null);
        }

        public void Error(string message, Exception exception, UnityEngine.Object context)
        {
            GameLog.Write(LogLevel.Error, Tag, message, exception, context);
        }

        public void Fatal(string message)
        {
            GameLog.Write(LogLevel.Fatal, Tag, message, null, null);
        }

        public void Fatal(string message, Exception exception)
        {
            GameLog.Write(LogLevel.Fatal, Tag, message, exception, null);
        }

        public void Fatal(string message, Exception exception, UnityEngine.Object context)
        {
            GameLog.Write(LogLevel.Fatal, Tag, message, exception, context);
        }

        public void DebugFormat(string format, params object[] args)
        {
            GameLog.DebugFormat(Tag, format, args);
        }

        public void InfoFormat(string format, params object[] args)
        {
            GameLog.InfoFormat(Tag, format, args);
        }

        public void WarnFormat(string format, params object[] args)
        {
            GameLog.WarnFormat(Tag, format, args);
        }

        public void ErrorFormat(string format, params object[] args)
        {
            GameLog.ErrorFormat(Tag, format, args);
        }

        public void FatalFormat(string format, params object[] args)
        {
            GameLog.FatalFormat(Tag, format, args);
        }
    }
}