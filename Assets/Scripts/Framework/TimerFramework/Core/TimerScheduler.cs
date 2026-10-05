using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace GameFramework.Timer
{
    /// <summary>调度器配置。客户端由 TimerInitOptions 映射过来，服务端直接 new 一个。</summary>
    public sealed class TimerSchedulerOptions
    {
        /// <summary>同时存在的计时器上限，0 = 不限制。</summary>
        public int MaxTimers = 0;

        /// <summary>单次 Tick 触发超过多少个就打警告，0 = 不检查。间隔为 0 的死循环会在这里现形。</summary>
        public int WarnFiresPerTick = 1000;

        /// <summary>单个回调耗时超过多少毫秒就打警告，0 = 不检查。</summary>
        public float WarnSlowCallbackMs = 20f;

        /// <summary>初始化时打一条日志。</summary>
        public bool LogOnInit = false;
    }

    /// <summary>某一时刻的统计快照。</summary>
    public struct TimerStatistics
    {
        public int ActiveCount;
        public long CreatedCount;
        public long FiredCount;
        public long CancelledCount;

        public override string ToString()
        {
            return "计时器 " + ActiveCount + " 个（累计注册 " + CreatedCount
                + "，触发 " + FiredCount + "，取消 " + CancelledCount + "）";
        }
    }

    /// <summary>
    /// 两端共用的调度器，纯 C#，不引用 UnityEngine。
    ///
    /// 时间全部来自 ITimeBase 的单调秒，内部存的是**到期时刻**而不是「还剩多久」，
    /// 所以宿主 Tick 的频率抖动、卡顿、断点都不会累积成误差——这对服务端尤其重要。
    ///
    /// 宿主只需要在自己的循环里每轮调一次 Tick()：
    ///   Unity —— GameController.Update（TimerManager 封装了 Scaled/Unscaled 两条时间轴）
    ///   服务端 —— ServerTimerService 的定时线程
    ///
    /// 几条约定：
    /// - 回调里可以放心再注册 / 取消：本轮新加的留到下一次 Tick，取消只打标记；
    /// - 一次性计时器触发后自动回收，无限循环的要自己 Cancel，或者按归属一次性全停；
    /// - 卡顿之后不补发，一次最多触发一次，下一次至少推后一整个间隔。
    /// </summary>
    public sealed class TimerScheduler
    {
        private readonly List<TimerTask> _active = new List<TimerTask>();
        private readonly Dictionary<int, TimerTask> _byId = new Dictionary<int, TimerTask>();
        private readonly List<int> _cancelBuffer = new List<int>();

        private readonly ITimeBase _time;
        private readonly ITimerLogger _logger;
        private readonly TimerSchedulerOptions _options;

        private int _nextId = 1;
        private int _tick;
        private int _firedThisTick;
        private long _createdCount;
        private long _firedCount;
        private long _cancelledCount;
        private bool _shutdown;
        private bool _inTick;

        public TimerScheduler(ITimeBase time, ITimerLogger logger = null, TimerSchedulerOptions options = null)
        {
            if (time == null) throw new ArgumentNullException("time");

            _time = time;
            _logger = logger != null ? logger : NullTimerLogger.Instance;
            _options = options != null ? options : new TimerSchedulerOptions();

            if (_options.LogOnInit)
                _logger.Info("调度器就绪：" + GetStatistics());
        }

        /// <summary>当前时间轴。</summary>
        public ITimeBase Time { get { return _time; } }

        /// <summary>当前活着的计时器数量。</summary>
        public int ActiveCount { get { return _byId.Count; } }

        /// <summary>已经跑过多少次 Tick。</summary>
        public int TickCount { get { return _tick; } }

        /// <summary>Drain：丢掉所有计时器，之后注册会被拒绝，取消静默返回 false。</summary>
        public void Shutdown()
        {
            _active.Clear();
            _byId.Clear();
            _cancelBuffer.Clear();
            _shutdown = true;
        }

        #region 驱动

        /// <summary>宿主每轮调一次。参数取哪个时间轴的「现在」由调用方决定（见 ITimeBase）。</summary>
        public void Tick()
        {
            if (_shutdown) return;

            _tick++;
            _firedThisTick = 0;

            if (_active.Count == 0) return;

            // 先记下本轮要处理的条数：回调里新注册的留到下一次 Tick，
            // 回调里取消的只打标记，所以这里的下标不会被打乱。
            int count = _active.Count;

            // 打标记：NextTick 要能区分「在回调里注册」和「在回调外注册」，见 NextTick 的实现
            _inTick = true;

            try
            {
                for (int i = 0; i < count; i++)
            {
                TimerTask task = _active[i];
                if (task.Cancelled || task.Done) continue;
                if (_tick < task.MinTick) continue;

                double now = _time.NowSeconds;

                if (task.Condition != null)
                {
                    if (task.TimeoutDeadline > 0.0 && now >= task.TimeoutDeadline)
                    {
                        task.Done = true;
                        continue;
                    }

                    if (!task.Condition()) continue;

                    Fire(task);
                    task.Done = true;
                    continue;
                }

                if (now < task.Deadline) continue;

                Fire(task);

                if (task.RepeatCount < 0)
                {
                    task.Deadline += task.Interval;
                }
                else
                {
                    task.RepeatCount--;
                    if (task.RepeatCount <= 0)
                    {
                        task.Done = true;
                        continue;
                    }

                    task.Deadline += task.Interval;
                }

                // 卡顿 / 断点之后不补发：下一次触发至少推到一整个间隔以后
                if (task.Deadline <= now)
                    task.Deadline = now + (task.Interval > 0.0 ? task.Interval : 0.0);
            }

            }
            finally
            {
                _inTick = false;
            }

            Sweep();

            if (_options.WarnFiresPerTick > 0 && _firedThisTick > _options.WarnFiresPerTick)
            {
                _logger.Warn("单次 Tick 触发了 " + _firedThisTick + " 个计时器（阈值 " + _options.WarnFiresPerTick
                    + "），查一下有没有间隔为 0 的死循环。");
            }
        }

        /// <summary>把已取消 / 已跑完的条目摘掉。只在 Tick 末尾做，保证遍历期间列表结构不变。</summary>
        private void Sweep()
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                TimerTask task = _active[i];
                if (!task.Cancelled && !task.Done) continue;

                _active.RemoveAt(i);
                _byId.Remove(task.Id);
            }
        }

        private void Fire(TimerTask task)
        {
            _firedThisTick++;
            _firedCount++;

            bool measure = _options.WarnSlowCallbackMs > 0f;
            long start = measure ? Stopwatch.GetTimestamp() : 0L;

            try
            {
                task.Callback();
            }
            catch (Exception ex)
            {
                _logger.Error("计时器回调异常：" + task.Describe(), ex);
            }

            if (!measure) return;

            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (ms >= _options.WarnSlowCallbackMs)
            {
                _logger.Warn("计时器回调耗时 " + ms.ToString("F1") + " ms（阈值 "
                    + _options.WarnSlowCallbackMs.ToString("F1") + " ms）：" + task.Describe());
            }
        }

        #endregion

        #region 注册

        /// <summary>延迟 seconds 秒后执行一次。</summary>
        public TimerHandle Delay(double seconds, Action callback)
        {
            return Delay(null, seconds, callback);
        }

        /// <summary>延迟 seconds 秒后执行一次，并归属到 owner。</summary>
        public TimerHandle Delay(object owner, double seconds, Action callback)
        {
            return Add(owner, seconds, 1L, callback, null, 0.0, _tick);
        }

        /// <summary>到绝对时刻（UTC 毫秒）执行一次。要求时间轴支持 UTC。</summary>
        public TimerHandle DelayUntil(long utcMs, Action callback)
        {
            return DelayUntil(null, utcMs, callback);
        }

        /// <summary>到绝对时刻（UTC 毫秒）执行一次，并归属到 owner。</summary>
        public TimerHandle DelayUntil(object owner, long utcMs, Action callback)
        {
            if (!_time.SupportsUtc)
            {
                _logger.Error("当前时间轴不支持绝对时间（UTC），DelayUntil 被忽略。");
                return TimerHandle.Invalid;
            }

            double deadline = _time.FromUtcMs(utcMs);
            return AddAt(owner, deadline, 0.0, 1L, callback, null, 0.0, _tick);
        }

        /// <summary>每隔 interval 秒执行一次。times &lt;= 0 表示无限循环，1 等价于 Delay。</summary>
        public TimerHandle Repeat(double interval, Action callback, long times = -1L)
        {
            return Repeat(null, interval, callback, times);
        }

        /// <summary>每隔 interval 秒执行一次，并归属到 owner。</summary>
        public TimerHandle Repeat(object owner, double interval, Action callback, long times = -1L)
        {
            return Add(owner, interval, times <= 0L ? -1L : times, callback, null, 0.0, _tick);
        }

        /// <summary>下一次 Tick 执行一次（服务端的「下一轮」，客户端的「下一帧」）。</summary>
        public TimerHandle NextTick(Action callback)
        {
            return NextTick(null, callback);
        }

        /// <summary>下一次 Tick 执行一次，并归属到 owner。</summary>
        public TimerHandle NextTick(object owner, Action callback)
        {
            // 在回调里注册（_inTick）时，「下一次 Tick」就是即将到来的那一次；
            // 在回调外注册时，马上就要来的那一 Tick 其实属于「当前这一段」，要跳过它，
            // 否则 Unity 侧会出现「NextFrame 和调用方同一帧触发」。
            int minTick = _tick + (_inTick ? 1 : 2);
            return Add(owner, 0.0, 1L, callback, null, 0.0, minTick);
        }

        /// <summary>
        /// 等条件成立再执行一次，每次 Tick 检查一次。
        /// timeout &gt; 0 时超时直接放弃，不会触发回调；&lt;= 0 表示一直等。
        /// </summary>
        public TimerHandle WaitUntil(Func<bool> condition, Action callback, double timeout = 0.0)
        {
            return WaitUntil(null, condition, callback, timeout);
        }

        /// <summary>等条件成立再执行一次，并归属到 owner。</summary>
        public TimerHandle WaitUntil(object owner, Func<bool> condition, Action callback, double timeout = 0.0)
        {
            double timeoutDeadline = timeout > 0.0 ? _time.NowSeconds + timeout : 0.0;
            return Add(owner, 0.0, 1L, callback, condition, timeoutDeadline, _tick);
        }

        private TimerHandle Add(object owner, double delay, long repeatCount, Action callback,
            Func<bool> condition, double timeoutDeadline, int minTick)
        {
            if (delay < 0.0) delay = 0.0;
            return AddAt(owner, _time.NowSeconds + delay, delay, repeatCount, callback, condition, timeoutDeadline, minTick);
        }

        private TimerHandle AddAt(object owner, double deadline, double interval, long repeatCount, Action callback,
            Func<bool> condition, double timeoutDeadline, int minTick)
        {
            if (_shutdown)
            {
                _logger.Error("调度器已经关闭，注册被忽略。");
                return TimerHandle.Invalid;
            }

            if (callback == null)
            {
                _logger.Error("计时器回调为 null，注册被忽略。");
                return TimerHandle.Invalid;
            }

            if (_options.MaxTimers > 0 && _byId.Count >= _options.MaxTimers)
            {
                _logger.Error("计时器数量已达上限 " + _options.MaxTimers + "，注册被拒绝（一般是有地方只加不减）。");
                return TimerHandle.Invalid;
            }

            TimerTask task = new TimerTask();
            task.Id = _nextId++;
            task.Owner = owner;
            task.Interval = interval;
            task.Deadline = deadline;
            task.RepeatCount = repeatCount;
            task.Callback = callback;
            task.Condition = condition;
            task.TimeoutDeadline = timeoutDeadline;
            task.MinTick = minTick;

            _active.Add(task);
            _byId[task.Id] = task;
            _createdCount++;

            return new TimerHandle(this, task.Id);
        }

        #endregion

        #region 取消

        /// <summary>取消一个计时器。返回它是否本来就还在跑。</summary>
        public bool Cancel(TimerHandle handle)
        {
            return handle.Id > 0 && Cancel(handle.Id);
        }

        /// <summary>按编号取消。没在跑返回 false。</summary>
        public bool Cancel(int id)
        {
            TimerTask task;
            if (!_byId.TryGetValue(id, out task)) return false;

            _byId.Remove(id);

            if (task.Cancelled || task.Done) return false;

            // 只打标记，不动 _active：万一这是回调里发起的取消，列表结构不能变
            task.Cancelled = true;
            _cancelledCount++;
            return true;
        }

        /// <summary>取消某个归属对象注册的所有计时器，返回取消了几个。</summary>
        public int CancelOwner(object owner)
        {
            if (owner == null) return 0;

            _cancelBuffer.Clear();
            foreach (TimerTask task in _byId.Values)
            {
                if (ReferenceEquals(task.Owner, owner))
                    _cancelBuffer.Add(task.Id);
            }

            int count = 0;
            for (int i = 0; i < _cancelBuffer.Count; i++)
            {
                if (Cancel(_cancelBuffer[i])) count++;
            }

            _cancelBuffer.Clear();
            return count;
        }

        /// <summary>取消所有计时器（切场景 / 重登 / 停服用）。</summary>
        public void CancelAll()
        {
            for (int i = 0; i < _active.Count; i++)
            {
                TimerTask task = _active[i];
                if (task.Cancelled || task.Done) continue;

                task.Cancelled = true;
                _cancelledCount++;
            }

            _byId.Clear();
        }

        /// <summary>这个编号的计时器是否还在跑。</summary>
        public bool IsAlive(int id)
        {
            TimerTask task;
            if (!_byId.TryGetValue(id, out task)) return false;

            return !task.Cancelled && !task.Done;
        }

        #endregion

        /// <summary>统计快照。</summary>
        public TimerStatistics GetStatistics()
        {
            TimerStatistics stats = new TimerStatistics();
            stats.ActiveCount = _byId.Count;
            stats.CreatedCount = _createdCount;
            stats.FiredCount = _firedCount;
            stats.CancelledCount = _cancelledCount;
            return stats;
        }
    }
}