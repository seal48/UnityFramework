using System;
using GameFramework.Core;
using GameFramework.Log;

namespace GameFramework.Timer
{
    /// <summary>
    /// Unity 侧的计时器门面：给两端共用的 TimerScheduler 套一层「时间轴 + 日志 + Inspector 配置」。
    ///
    /// 真正的调度逻辑在 TimerFramework/Core 里（纯 C#，服务端也在用同一份），
    /// 这里只负责两件事：
    /// 1. 把 Scaled / Unscaled 拆成两条时间轴：
    ///    Scaled   —— 虚拟时间轴，每帧 Advance(Time.deltaTime)，timeScale = 0 时自然停住；
    ///    Unscaled —— 真实时间轴（单调时钟 + UTC 锚点），暂停、切后台照走；
    /// 2. 把日志接到 GameLog、把上限 / 告警阈值从 Inspector 配置映射过去。
    ///
    /// 由 GameController 在 Awake 里创建并初始化，之后每帧 Tick。
    /// 业务用 GameController.Instance.Timer；面板里用 Panel.Timers，面板关闭时会自动取消它注册的计时器。
    /// </summary>
    public sealed class TimerManager : IGameModule, ITickable
    {
        private readonly VirtualTimeBase virtualTime = new VirtualTimeBase();
        private readonly RealTimeBase realTime = new RealTimeBase();

        private TimerScheduler scaled;
        private TimerScheduler unscaled;
        private TimerInitOptions options;

        /// <summary>是否已经初始化。</summary>
        public bool IsInitialized { get { return scaled != null; } }

        /// <summary>当前活着的计时器数量（两条时间轴合计）。</summary>
        public int ActiveCount { get { return Count(scaled) + Count(unscaled); } }

        /// <summary>初始化时用的参数。</summary>
        public TimerInitOptions Options { get { return options; } }

        /// <summary>
        /// 真实时间轴（单调时钟 + UTC 锚点）。
        /// 以后做校时的时候：收到服务器 ServerTimeMs 就调 TimeBase.Anchor(ServerTimeMs)，
        /// 之后所有 DelayUntil / ServerNowMs 就都按服务器时间走。
        /// </summary>
        public RealTimeBase TimeBase { get { return realTime; } }

        /// <summary>当前（锚定后的）服务器 UTC 毫秒。</summary>
        public long ServerNowMs { get { return realTime.NowUtcMs; } }

        #region 生命周期

        /// <summary>初始化。没有异步步骤，GameController 在 Awake 里直接调。</summary>
        public void Init(TimerInitOptions initOptions)
        {
            options = initOptions != null ? initOptions : new TimerInitOptions();
            virtualTime.Reset();

            TimerSchedulerOptions shared = new TimerSchedulerOptions();
            shared.MaxTimers = options.MaxTimers;
            shared.WarnFiresPerTick = options.WarnFiresPerFrame;
            shared.WarnSlowCallbackMs = options.WarnSlowCallbackMs;
            shared.LogOnInit = false;

            // 两条时间轴各一个调度器，共用同一份配置和同一个日志出口
            scaled = new TimerScheduler(virtualTime, new UnityTimerLogger(LogTag.Timer), shared);
            unscaled = new TimerScheduler(realTime, new UnityTimerLogger(LogTag.Timer), shared);

            if (options.LogOnInit)
                GameLog.Info(LogTag.Timer, "计时器就绪：" + GetStatistics() + "，服务器时间 " + ServerNowMs + " ms");
        }

        /// <summary>关闭：丢掉所有计时器。之后再注册会被拒绝。</summary>
        public void Shutdown()
        {
            if (scaled != null)
            {
                scaled.Shutdown();
                scaled = null;
            }

            if (unscaled != null)
            {
                unscaled.Shutdown();
                unscaled = null;
            }

            options = null;
        }

        /// <summary>每帧调用，由 GameController 转发。</summary>
        public void Tick(float deltaTime, float unscaledDeltaTime)
        {
            if (scaled == null)
                return;

            // 虚拟时间轴先走：timeScale = 0 时 deltaTime = 0，Scaled 的计时器就停在原地
            virtualTime.Advance(deltaTime);

            scaled.Tick();
            unscaled.Tick();
        }

        #endregion

        #region 注册

        /// <summary>延迟 seconds 秒后执行一次（游戏时间）。</summary>
        public TimerHandle Delay(float seconds, Action callback)
        {
            return Delay(null, TimerMode.Scaled, seconds, callback);
        }

        /// <summary>延迟 seconds 秒后执行一次（游戏时间），并归属到 owner。</summary>
        public TimerHandle Delay(object owner, float seconds, Action callback)
        {
            return Delay(owner, TimerMode.Scaled, seconds, callback);
        }

        /// <summary>延迟 seconds 秒后执行一次（指定时间轴）。</summary>
        public TimerHandle Delay(TimerMode mode, float seconds, Action callback)
        {
            return Delay(null, mode, seconds, callback);
        }

        /// <summary>延迟 seconds 秒后执行一次（指定时间轴），并归属到 owner。</summary>
        public TimerHandle Delay(object owner, TimerMode mode, float seconds, Action callback)
        {
            TimerScheduler scheduler = Resolve(mode);
            return scheduler != null ? scheduler.Delay(owner, seconds, callback) : TimerHandle.Invalid;
        }

        /// <summary>到绝对时刻（服务器 UTC 毫秒）执行一次。走真实时间轴，暂停也照走。</summary>
        public TimerHandle DelayUntil(long utcMs, Action callback)
        {
            return DelayUntil(null, utcMs, callback);
        }

        /// <summary>到绝对时刻（服务器 UTC 毫秒）执行一次，并归属到 owner。</summary>
        public TimerHandle DelayUntil(object owner, long utcMs, Action callback)
        {
            TimerScheduler scheduler = Resolve(TimerMode.Unscaled);
            return scheduler != null ? scheduler.DelayUntil(owner, utcMs, callback) : TimerHandle.Invalid;
        }

        /// <summary>每隔 interval 秒执行一次（游戏时间）。times &lt;= 0 表示无限循环，1 等价于 Delay。</summary>
        public TimerHandle Repeat(float interval, Action callback, long times = -1L)
        {
            return Repeat(null, TimerMode.Scaled, interval, callback, times);
        }

        /// <summary>每隔 interval 秒执行一次（游戏时间），并归属到 owner。</summary>
        public TimerHandle Repeat(object owner, float interval, Action callback, long times = -1L)
        {
            return Repeat(owner, TimerMode.Scaled, interval, callback, times);
        }

        /// <summary>
        /// 每隔 interval 秒执行一次（指定时间轴）。
        /// 这个重载必须存在：否则 Repeat(TimerMode.X, 1f, cb, 3) 会悄悄匹配到「owner」那个重载，
        /// 把枚举当成归属对象，时间轴也就跟着错了。
        /// </summary>
        public TimerHandle Repeat(TimerMode mode, float interval, Action callback, long times = -1L)
        {
            return Repeat(null, mode, interval, callback, times);
        }

        /// <summary>每隔 interval 秒执行一次（指定时间轴），并归属到 owner。</summary>
        public TimerHandle Repeat(object owner, TimerMode mode, float interval, Action callback, long times = -1L)
        {
            TimerScheduler scheduler = Resolve(mode);
            return scheduler != null ? scheduler.Repeat(owner, interval, callback, times) : TimerHandle.Invalid;
        }

        /// <summary>下一帧执行一次（无视 timeScale）。</summary>
        public TimerHandle NextFrame(Action callback)
        {
            return NextFrame(null, callback);
        }

        /// <summary>下一帧执行一次（无视 timeScale），并归属到 owner。</summary>
        public TimerHandle NextFrame(object owner, Action callback)
        {
            TimerScheduler scheduler = Resolve(TimerMode.Unscaled);
            return scheduler != null ? scheduler.NextTick(owner, callback) : TimerHandle.Invalid;
        }

        /// <summary>
        /// 等条件成立再执行一次，每帧检查一次（无视 timeScale）。
        /// timeout &gt; 0 时超时直接放弃，不会触发回调；&lt;= 0 表示一直等。
        /// </summary>
        public TimerHandle WaitUntil(Func<bool> condition, Action callback, float timeout = 0f)
        {
            return WaitUntil(null, condition, callback, timeout);
        }

        /// <summary>等条件成立再执行一次，并归属到 owner。</summary>
        public TimerHandle WaitUntil(object owner, Func<bool> condition, Action callback, float timeout = 0f)
        {
            TimerScheduler scheduler = Resolve(TimerMode.Unscaled);
            return scheduler != null ? scheduler.WaitUntil(owner, condition, callback, timeout) : TimerHandle.Invalid;
        }

        #endregion

        #region 取消

        /// <summary>取消一个计时器。返回它是否本来就还在跑。</summary>
        public bool Cancel(TimerHandle handle)
        {
            if (!handle.IsValid)
                return false;

            handle.Cancel();
            return true;
        }

        /// <summary>取消某个归属对象注册的所有计时器（两条时间轴一起），返回取消了几个。</summary>
        public int CancelOwner(object owner)
        {
            int count = 0;
            if (scaled != null) count += scaled.CancelOwner(owner);
            if (unscaled != null) count += unscaled.CancelOwner(owner);
            return count;
        }

        /// <summary>取消所有计时器（切场景 / 重登用）。</summary>
        public void CancelAll()
        {
            if (scaled != null) scaled.CancelAll();
            if (unscaled != null) unscaled.CancelAll();
        }

        #endregion

        /// <summary>统计快照（两条时间轴合计）。</summary>
        public TimerStatistics GetStatistics()
        {
            TimerStatistics stats = new TimerStatistics();
            if (scaled != null) Accumulate(ref stats, scaled.GetStatistics());
            if (unscaled != null) Accumulate(ref stats, unscaled.GetStatistics());
            return stats;
        }

        private static void Accumulate(ref TimerStatistics total, TimerStatistics part)
        {
            total.ActiveCount += part.ActiveCount;
            total.CreatedCount += part.CreatedCount;
            total.FiredCount += part.FiredCount;
            total.CancelledCount += part.CancelledCount;
        }

        private static int Count(TimerScheduler scheduler)
        {
            return scheduler != null ? scheduler.ActiveCount : 0;
        }

        private TimerScheduler Resolve(TimerMode mode)
        {
            TimerScheduler scheduler = mode == TimerMode.Scaled ? scaled : unscaled;
            if (scheduler == null)
            {
                GameLog.Error(LogTag.Timer, "计时器还没初始化就有人注册，已忽略。");
                return null;
            }

            return scheduler;
        }
    }
}