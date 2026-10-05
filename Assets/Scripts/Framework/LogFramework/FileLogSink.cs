using System;
using System.IO;
using System.Text;

namespace GameFramework.Log
{
    /// <summary>
    /// 写文件：{目录}/yyyy-MM-dd.log，超过 MaxFileCount 就删最旧的。
    /// 日志先攒在缓冲里，按 FlushInterval 或遇到 Error 才碰磁盘 —— 移动端每帧写盘太贵。
    /// </summary>
    public sealed class FileLogSink : ILogSink
    {
        private readonly object _gate = new object();
        private readonly StringBuilder _buffer = new StringBuilder(4096);
        private readonly string _directory;
        private readonly int _maxFileCount;

        private StreamWriter _writer;
        private DateTime _date;
        private bool _broken;

        public FileLogSink(string directory, int maxFileCount)
        {
            _directory = directory;
            _maxFileCount = maxFileCount < 1 ? 1 : maxFileCount;
        }

        /// <summary>目录建不出来（比如权限问题）时为 true，此时这个出口整体失效，但别的出口照常。</summary>
        public bool IsBroken { get { return _broken; } }

        public void Write(LogEntry entry)
        {
            lock (_gate)
            {
                if (_broken) return;
                if (!OpenFor(entry.Time)) return;

                _buffer.Append(entry.ToLine());

                string contextName = SafeContextName(entry.Context);
                if (contextName != null) _buffer.Append(" [").Append(contextName).Append(']');

                _buffer.Append('\n');

                if (!string.IsNullOrEmpty(entry.StackTrace))
                    _buffer.Append(entry.StackTrace).Append('\n');

                // 出错必须马上落盘：进程被系统杀掉时，攒着的那部分就没了
                if (entry.Level <= LogLevel.Error) FlushLocked();
            }
        }

        public void Flush()
        {
            lock (_gate) FlushLocked();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                FlushLocked();

                if (_writer != null)
                {
                    try { _writer.Dispose(); }
                    catch { }
                    _writer = null;
                }
            }
        }

        /// <summary>跨天就换文件。</summary>
        private bool OpenFor(DateTime time)
        {
            if (_writer != null && time.Date == _date) return true;

            CloseWriter();

            try
            {
                Directory.CreateDirectory(_directory);

                string path = Path.Combine(_directory, time.ToString("yyyy-MM-dd") + ".log");
                _writer = new StreamWriter(path, true);
                _writer.AutoFlush = false;
                _date = time.Date;

                CleanupOldFiles();
                return true;
            }
            catch
            {
                // 写不了就整体放弃这个出口，不再反复抛异常
                _broken = true;
                return false;
            }
        }

        /// <summary>取 context 名字。对象被销毁时不能碰它，所以整个兜住。</summary>
        private static string SafeContextName(UnityEngine.Object context)
        {
            if (context == null) return null;

            try { return context.name; }
            catch { return "(已销毁)"; }
        }

        private void CloseWriter()
        {
            if (_writer == null) return;

            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch { }

            _writer = null;
        }

        private void FlushLocked()
        {
            if (_writer == null || _buffer.Length == 0) return;

            try
            {
                _writer.Write(_buffer.ToString());
                _writer.Flush();
            }
            catch
            {
                _broken = true;
            }

            _buffer.Length = 0;
        }

        /// <summary>文件名是 yyyy-MM-dd，字典序就是时间序。</summary>
        private void CleanupOldFiles()
        {
            try
            {
                string[] files = Directory.GetFiles(_directory, "*.log");
                if (files.Length <= _maxFileCount) return;

                Array.Sort(files, StringComparer.Ordinal);

                for (int i = 0; i < files.Length - _maxFileCount; i++)
                {
                    try { File.Delete(files[i]); }
                    catch { }
                }
            }
            catch { }
        }
    }
}