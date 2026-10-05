using System;
using System.Diagnostics;

namespace GameFramework.Timer
{
    /// <summary>
    /// 时间轴。调度器只认它，不认 UnityEngine 也不认 DateTime，
    /// 这样客户端和服务端能共用同一套调度逻辑。
    /// 实现必须是单调的：只会前进，不会因为用户改系统时间而回退。
    /// </summary>
    public interface ITimeBase
    {
        /// <summary>本时间轴当前的秒数（单调）。</summary>
        double NowSeconds { get; }

        /// <summary>能不能把 UTC 时间戳换算到本时间轴。虚拟时间轴（Unity 的 timeScale）为 false。</summary>
        bool SupportsUtc { get; }

        /// <summary>当前 UTC 毫秒；不支持时为 0。</summary>
        long NowUtcMs { get; }

        /// <summary>把 UTC 毫秒换算成本时间轴的秒数；不支持时返回 -1。</summary>
        double FromUtcMs(long utcMs);
    }

    /// <summary>
    /// 真实时间轴：单调秒 + 可锚定的 UTC。
    ///
    /// 单调部分用 Stopwatch，进程内一直前进，不受系统时间调整影响；
    /// UTC 部分靠一次观测「锚定」：Anchor(服务器给的 ServerTimeMs) 之后，
    /// NowUtcMs 就是服务器时间——客户端校时用的就是这一步。
    /// 服务端自己就是权威，用 AnchorUtcNow() 锚本机时间即可。
    /// </summary>
    public sealed class RealTimeBase : ITimeBase
    {
        private readonly double _originSeconds;
        private double _anchorSeconds;
        private long _anchorUtcMs;

        public RealTimeBase()
        {
            _originSeconds = StopwatchSeconds();
            AnchorUtcNow();
        }

        public double NowSeconds { get { return StopwatchSeconds() - _originSeconds; } }

        public bool SupportsUtc { get { return true; } }

        public long NowUtcMs
        {
            get
            {
                double deltaMs = (StopwatchSeconds() - _anchorSeconds) * 1000.0;
                return _anchorUtcMs + (long)deltaMs;
            }
        }

        public double FromUtcMs(long utcMs)
        {
            return (utcMs - _anchorUtcMs) / 1000.0 + (_anchorSeconds - _originSeconds);
        }

        /// <summary>用一次权威 UTC 时间重新锚定（客户端收到 ServerTimeMs 时调它）。</summary>
        public void Anchor(long utcMs)
        {
            _anchorSeconds = StopwatchSeconds();
            _anchorUtcMs = utcMs;
        }

        /// <summary>用本机 UTC 重新锚定（服务端，以及还没校时的客户端）。</summary>
        public void AnchorUtcNow()
        {
            Anchor(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        private static double StopwatchSeconds()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }
    }

    /// <summary>
    /// 虚拟时间轴：自己往前走，只用来表达「按游戏时间」的定时。
    /// Unity 的 Scaled 模式就是拿 Time.deltaTime 一直 Advance：
    /// timeScale = 0 时不再 Advance，计时器自然停在原地。
    /// </summary>
    public sealed class VirtualTimeBase : ITimeBase
    {
        private double _seconds;

        public double NowSeconds { get { return _seconds; } }
        public bool SupportsUtc { get { return false; } }
        public long NowUtcMs { get { return 0L; } }
        public double FromUtcMs(long utcMs) { return -1.0; }

        /// <summary>推进虚拟时间（Unity 侧每帧传 Time.deltaTime）。</summary>
        public void Advance(double deltaSeconds)
        {
            if (deltaSeconds > 0.0)
                _seconds += deltaSeconds;
        }

        /// <summary>归零（重登 / 回主菜单重新计时用）。</summary>
        public void Reset()
        {
            _seconds = 0.0;
        }
    }
}