namespace GameFramework.Log
{
    /// <summary>日志出口。可以有多个（控制台 / 文件 / 内存），实现要自己保证线程安全。</summary>
    public interface ILogSink
    {
        void Write(LogEntry entry);

        void Flush();

        void Dispose();
    }
}