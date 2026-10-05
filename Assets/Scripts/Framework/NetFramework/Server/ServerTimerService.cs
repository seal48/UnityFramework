using System;
using System.Threading;
using GameFramework.Timer;

namespace GameFramework.Net.Server
{
    /// <summary>
    /// 服务端定时线程：按固定间隔驱动一个 TimerScheduler。
    ///
    /// 服务端没有「帧」，accept / 收包线程都是事件驱动的，不适合承载定时逻辑，所以单独起一条线程。
    /// 回调默认跑在**这条定时线程**上；要动会话、要广播，就自己经 Router 的 Dispatcher 派发回去，
    /// 或者自己加锁 —— 和收包回调是同一套心智。
    ///
    /// 时间基准是 RealTimeBase（单调时钟 + UTC 锚点），
    /// 所以 Delay / Repeat / DelayUntil 都是准的，线程偶尔忙一下也不会累积成误差。
    /// </summary>
    public sealed class ServerTimerService : IDisposable
    {
        private readonly TimerScheduler _scheduler;
        private Thread _thread;
        private volatile bool _running;
        private readonly int _intervalMs;

        public ServerTimerService(INetLogger logger = null, int intervalMs = 15)
        {
            _intervalMs = intervalMs < 1 ? 1 : intervalMs;

            TimeBase = new RealTimeBase();

            TimerSchedulerOptions options = new TimerSchedulerOptions();
            options.LogOnInit = false;

            _scheduler = new TimerScheduler(TimeBase, logger, options);
        }

        /// <summary>服务端的权威时间轴。客户端校时用的 ServerTimeMs 就从它取。</summary>
        public RealTimeBase TimeBase { get; private set; }

        /// <summary>调度器本体：Delay / Repeat / DelayUntil / CancelOwner 都在它上面。</summary>
        public TimerScheduler Scheduler { get { return _scheduler; } }

        /// <summary>当前（权威）UTC 毫秒。下发 ServerTimeMs 时用它，别用 DateTime.Now。</summary>
        public long NowUtcMs { get { return TimeBase.NowUtcMs; } }

        public bool IsRunning { get { return _running; } }

        public void Start()
        {
            if (_running) return;

            _running = true;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "net-timer"
            };
            _thread.Start();
        }

        public void Stop()
        {
            if (!_running) return;

            _running = false;

            Thread thread = _thread;
            _thread = null;

            if (thread != null)
            {
                try
                {
                    thread.Join(500);
                }
                catch (Exception)
                {
                    // 忽略：线程是后台线程，不会拦住进程退出
                }
            }

            _scheduler.CancelAll();
        }

        public void Dispose()
        {
            Stop();
        }

        private void Loop()
        {
            // 小片睡：既保证间隔，又让 Stop() 在 5ms 内就能响应
            const int SliceMs = 5;

            while (_running)
            {
                try
                {
                    _scheduler.Tick();
                }
                catch (Exception)
                {
                    // 回调异常调度器内部已经吞掉了，这里再兜一层，保证线程不会意外退出
                }

                for (int slept = 0; slept < _intervalMs && _running; slept += SliceMs)
                    Thread.Sleep(SliceMs);
            }
        }
    }
}