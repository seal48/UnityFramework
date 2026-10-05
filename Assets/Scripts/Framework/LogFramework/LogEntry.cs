using System;
using System.Text;
using UnityEngine;

namespace GameFramework.Log
{
    /// <summary>一条日志。文本在这里格式化好，出口只负责往哪写。</summary>
    public readonly struct LogEntry
    {
        public readonly DateTime Time;
        public readonly LogLevel Level;
        public readonly string Tag;
        public readonly string Message;
        public readonly string StackTrace;

        /// <summary>Unity 对象（GameObject / Component），点一下日志能定位到它。可以为 null。</summary>
        public readonly UnityEngine.Object Context;

        public LogEntry(LogLevel level, string tag, string message, string stackTrace, UnityEngine.Object context)
        {
            Time = DateTime.Now;
            Level = level;
            Tag = string.IsNullOrEmpty(tag) ? LogTag.Root : tag;
            Message = message ?? string.Empty;
            StackTrace = stackTrace;
            Context = context;
        }

        /// <summary>级别名。等宽是为了让控制台里能对齐。</summary>
        public string LevelText
        {
            get
            {
                switch (Level)
                {
                    case LogLevel.Debug: return "DEBUG";
                    case LogLevel.Info: return "INFO ";
                    case LogLevel.Warn: return "WARN ";
                    case LogLevel.Error: return "ERROR";
                    case LogLevel.Fatal: return "FATAL";
                    default: return "NONE ";
                }
            }
        }

        /// <summary>控制台颜色（深色主题下挑的）。</summary>
        public string ColorHex
        {
            get
            {
                switch (Level)
                {
                    case LogLevel.Debug: return "#9AA0A6";
                    case LogLevel.Info: return "#DADCE0";
                    case LogLevel.Warn: return "#F9AB00";
                    case LogLevel.Error: return "#E8710A";
                    case LogLevel.Fatal: return "#C5221F";
                    default: return "#DADCE0";
                }
            }
        }

        /// <summary>落盘 / 上报用的一行文本（不含 context 名字，那个由各自出口决定要不要加）。</summary>
        public string ToLine()
        {
            var sb = new StringBuilder(Message.Length + 48);
            sb.Append('[').Append(Time.ToString("HH:mm:ss.fff")).Append("][");
            sb.Append(LevelText).Append("][").Append(Tag).Append("] ");
            sb.Append(Message);
            return sb.ToString();
        }
    }
}