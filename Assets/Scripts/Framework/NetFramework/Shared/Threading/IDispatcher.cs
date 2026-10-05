using System;
using System.Collections.Concurrent;

namespace GameFramework.Net.Threading
{
    /// <summary>
    /// 回调派发器：决定“收到消息后处理函数在哪个线程执行”。
    /// 后端默认用 ImmediateDispatcher（接收线程直接跑），
    /// Unity 客户端用 QueuedDispatcher 把回调收集起来，在主线程 Update 里执行，避免访问 Unity API 出错。
    /// </summary>
    public interface IDispatcher
    {
        void Post(Action action);
    }

    /// <summary>立即在当前线程执行。</summary>
    public sealed class ImmediateDispatcher : IDispatcher
    {
        public static readonly ImmediateDispatcher Instance = new ImmediateDispatcher();

        public void Post(Action action)
        {
            if (action != null) action();
        }
    }

    /// <summary>把回调塞进队列，等外部（例如 Unity 的 Update）调用 Pump 时再依次执行。</summary>
    public sealed class QueuedDispatcher : IDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
        private readonly Action<Exception> _onError;

        /// <param name="onError">回调抛异常时的处理方式，默认忽略（只保证不会打断后续回调）。</param>
        public QueuedDispatcher(Action<Exception> onError = null)
        {
            _onError = onError;
        }

        public int Count => _queue.Count;

        public void Post(Action action)
        {
            if (action != null) _queue.Enqueue(action);
        }

        /// <summary>执行队列里的回调，返回本次实际执行的数量。maxActions 用来限制单帧耗时。</summary>
        public int Pump(int maxActions = 256)
        {
            var executed = 0;
            while (executed < maxActions && _queue.TryDequeue(out var action))
            {
                executed++;
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // 单个回调异常不能影响其它消息的处理。
                    _onError?.Invoke(ex);
                }
            }
            return executed;
        }

        public void Clear()
        {
            while (_queue.TryDequeue(out _)) { }
        }
    }
}
