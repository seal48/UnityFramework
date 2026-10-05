using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace GameFramework.Log
{
    /// <summary>
    /// 全局日志入口，也是唯一的初始化点（由 GameController 在 Awake 里调 Init）。
    ///
    /// 模块里推荐缓存一个 ModuleLog：
    ///     private static readonly ModuleLog Log = GameLog.Get(LogTag.UI);
    ///     Log.Info("打开面板 " + name);
    ///
    /// 零散地方直接写：
    ///     GameLog.Info(LogTag.Storage, "落盘完成");
    /// </summary>
    public static class GameLog
    {
        private static readonly Dictionary<string, ModuleLog> _modules =
            new Dictionary<string, ModuleLog>(StringComparer.Ordinal);

        private static LogManager _manager;

        public static bool IsInitialized { get { return _manager != null && _manager.IsInitialized; } }

        /// <summary>内存里最近若干条日志。游戏内控制台 / 崩溃上报用，未初始化时为 null。</summary>
        public static MemoryLogSink Memory { get { return _manager == null ? null : _manager.Memory; } }

        /// <summary>日志文件目录，没开文件输出时为 null。</summary>
        public static string FileRoot { get { return _manager == null ? null : _manager.FileRoot; } }

        /// <summary>当前生效的最低输出级别。未初始化时按 Debug 算。</summary>
        public static LogLevel MinLevel { get { return _manager == null ? LogLevel.Debug : _manager.MinLevel; } }

        public static void Init(LogOptions options)
        {
            if (_manager == null) _manager = new LogManager();
            _manager.Init(options);
        }

        public static void Shutdown()
        {
            if (_manager == null) return;

            _manager.Shutdown();
            _manager = null;
            _modules.Clear();
        }

        /// <summary>每帧调用，驱动文件落盘。由 GameController 转发。</summary>
        public static void Tick(float deltaTime, float unscaledDeltaTime)
        {
            if (_manager == null) return;
            _manager.Tick(deltaTime, unscaledDeltaTime);
        }

        /// <summary>拿一个模块日志器。按标签缓存，重复调用不会重复分配。</summary>
        public static ModuleLog Get(string tag)
        {
            string key = string.IsNullOrEmpty(tag) ? LogTag.Root : tag;

            ModuleLog log;
            if (_modules.TryGetValue(key, out log)) return log;

            log = new ModuleLog(key);
            _modules[key] = log;
            return log;
        }

        public static bool IsEnabled(LogLevel level, string tag)
        {
            // 还没初始化也要放行：启动早期的日志不能被吞掉
            if (_manager == null) return level != LogLevel.None;
            return _manager.IsEnabled(level, tag);
        }

        public static void Write(LogLevel level, string tag, string message, Exception exception)
        {
            Write(level, tag, message, exception, null);
        }

        public static void Write(LogLevel level, string tag, string message, Exception exception,
            UnityEngine.Object context)
        {
            if (_manager == null)
            {
                UnityEngine.Debug.Log("[" + (string.IsNullOrEmpty(tag) ? LogTag.Root : tag) + "] " + message, context);
                return;
            }

            if (!_manager.IsEnabled(level, tag)) return;
            _manager.Write(level, tag, message, exception, context);
        }

        public static void Debug(string tag, string message)
        {
            Write(LogLevel.Debug, tag, message, null, null);
        }

        public static void Debug(string tag, string message, UnityEngine.Object context)
        {
            Write(LogLevel.Debug, tag, message, null, context);
        }

        public static void Info(string tag, string message)
        {
            Write(LogLevel.Info, tag, message, null, null);
        }

        public static void Info(string tag, string message, UnityEngine.Object context)
        {
            Write(LogLevel.Info, tag, message, null, context);
        }

        public static void Warn(string tag, string message)
        {
            Write(LogLevel.Warn, tag, message, null, null);
        }

        public static void Warn(string tag, string message, UnityEngine.Object context)
        {
            Write(LogLevel.Warn, tag, message, null, context);
        }

        public static void Error(string tag, string message)
        {
            Write(LogLevel.Error, tag, message, null, null);
        }

        public static void Error(string tag, string message, UnityEngine.Object context)
        {
            Write(LogLevel.Error, tag, message, null, context);
        }

        public static void Error(string tag, string message, Exception exception)
        {
            Write(LogLevel.Error, tag, message, exception, null);
        }

        public static void Error(string tag, string message, Exception exception, UnityEngine.Object context)
        {
            Write(LogLevel.Error, tag, message, exception, context);
        }

        public static void Fatal(string tag, string message)
        {
            Write(LogLevel.Fatal, tag, message, null, null);
        }

        public static void Fatal(string tag, string message, UnityEngine.Object context)
        {
            Write(LogLevel.Fatal, tag, message, null, context);
        }

        public static void Fatal(string tag, string message, Exception exception)
        {
            Write(LogLevel.Fatal, tag, message, exception, null);
        }

        public static void Fatal(string tag, string message, Exception exception, UnityEngine.Object context)
        {
            Write(LogLevel.Fatal, tag, message, exception, context);
        }

        public static void DebugFormat(string tag, string format, params object[] args)
        {
            Write(LogLevel.Debug, tag, Format(format, args), null, null);
        }

        public static void InfoFormat(string tag, string format, params object[] args)
        {
            Write(LogLevel.Info, tag, Format(format, args), null, null);
        }

        public static void WarnFormat(string tag, string format, params object[] args)
        {
            Write(LogLevel.Warn, tag, Format(format, args), null, null);
        }

        public static void ErrorFormat(string tag, string format, params object[] args)
        {
            Write(LogLevel.Error, tag, Format(format, args), null, null);
        }

        public static void FatalFormat(string tag, string format, params object[] args)
        {
            Write(LogLevel.Fatal, tag, Format(format, args), null, null);
        }

        private static string Format(string format, object[] args)
        {
            if (string.IsNullOrEmpty(format)) return string.Empty;
            if (args == null || args.Length == 0) return format;

            // 跟 Unity 的 Debug.LogFormat 一致，用不变文化，免得小数点在部分地区变成逗号
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
    }
}