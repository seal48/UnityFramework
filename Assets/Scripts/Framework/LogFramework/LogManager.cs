using System;
using System.Collections.Generic;
using GameFramework.Core;
using UnityEngine;

namespace GameFramework.Log
{
    /// <summary>日志总管：级别过滤、模块静音、多个出口、定时落盘。生命周期由 GameLog 持有。</summary>
    public sealed class LogManager : IGameModule, ITickable
    {
        private readonly List<ILogSink> _sinks = new List<ILogSink>();
        private readonly HashSet<string> _muted = new HashSet<string>(StringComparer.Ordinal);

        private LogOptions _options;
        private LogLevel _minLevel;
        private float _flushTimer;
        private bool _initialized;

        public bool IsInitialized { get { return _initialized; } }

        /// <summary>当前生效的最低输出级别（开发 / 正式取的值不一样）。</summary>
        public LogLevel MinLevel { get { return _minLevel; } }

        /// <summary>内存出口。没初始化时为 null。</summary>
        public MemoryLogSink Memory { get; private set; }

        /// <summary>日志文件目录。没开文件输出时为 null。</summary>
        public string FileRoot { get; private set; }

        public void Init(LogOptions options)
        {
            if (_initialized) return;

            _options = options != null ? options : new LogOptions();

            // 编辑器 / Development Build 默认更啰嗦，方便开发期排查
            bool develop = Application.isEditor || UnityEngine.Debug.isDebugBuild;
            _minLevel = develop ? _options.MinLevelInDevelopment : _options.MinLevel;

            if (_options.MutedTags != null)
            {
                for (int i = 0; i < _options.MutedTags.Length; i++)
                {
                    string tag = _options.MutedTags[i];
                    if (!string.IsNullOrEmpty(tag)) _muted.Add(tag);
                }
            }

            _sinks.Add(new UnityConsoleSink(_options.ColoredConsole));

            Memory = new MemoryLogSink(_options.MaxHistory);
            _sinks.Add(Memory);

            if (_options.WriteToFile)
            {
                FileRoot = string.IsNullOrEmpty(_options.RootPathOverride)
                    ? System.IO.Path.Combine(Application.persistentDataPath,
                        string.IsNullOrEmpty(_options.FolderName) ? "Logs" : _options.FolderName)
                    : _options.RootPathOverride;

                _sinks.Add(new FileLogSink(FileRoot, _options.MaxFileCount));
            }

            _initialized = true;
        }

        public void Shutdown()
        {
            if (!_initialized) return;

            for (int i = 0; i < _sinks.Count; i++)
            {
                try { _sinks[i].Dispose(); }
                catch { }
            }

            _sinks.Clear();
            _muted.Clear();
            Memory = null;
            FileRoot = null;
            _initialized = false;
        }

        /// <summary>每帧调用：到点就把文件缓冲落盘。</summary>
        public void Tick(float deltaTime, float unscaledDeltaTime)
        {
            if (!_initialized || _options.FlushInterval <= 0f) return;

            _flushTimer += deltaTime;
            if (_flushTimer < _options.FlushInterval) return;

            _flushTimer = 0f;

            for (int i = 0; i < _sinks.Count; i++)
            {
                try { _sinks[i].Flush(); }
                catch { }
            }
        }

        public bool IsEnabled(LogLevel level, string tag)
        {
            if (level == LogLevel.None) return false;
            if (level > _minLevel) return false;
            if (tag != null && _muted.Contains(tag)) return false;
            return true;
        }

        public void Write(LogLevel level, string tag, string message, Exception exception)
        {
            Write(level, tag, message, exception, null);
        }

        public void Write(LogLevel level, string tag, string message, Exception exception,
            UnityEngine.Object context)
        {
            string text = message ?? string.Empty;
            string stack = null;

            if (exception != null)
            {
                text = text + "\n" + exception;
            }
            else if (_options.CaptureStackTrace && level <= LogLevel.Error)
            {
                // 跳过 Write 自己和调用者两层，剩下的才是业务栈
                stack = new System.Diagnostics.StackTrace(2, true).ToString();
            }

            LogEntry entry = new LogEntry(level, tag, text, stack, context);

            // 日志自己绝不能把业务搞崩
            for (int i = 0; i < _sinks.Count; i++)
            {
                try { _sinks[i].Write(entry); }
                catch { }
            }
        }
    }
}