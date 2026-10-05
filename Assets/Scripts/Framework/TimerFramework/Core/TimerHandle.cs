using System;

namespace GameFramework.Timer
{
    /// <summary>
    /// 计时器句柄。取消和有效性判断都挂在它身上，拿到手就不用再回头找调度器。
    /// default(TimerHandle) 是无效句柄，可以放心当「空」用（TimerHandle.Invalid 就是它）。
    /// </summary>
    public readonly struct TimerHandle : IEquatable<TimerHandle>
    {
        /// <summary>无效句柄：什么都不做。</summary>
        public static readonly TimerHandle Invalid = default(TimerHandle);

        private readonly TimerScheduler scheduler;
        private readonly int id;

        internal TimerHandle(TimerScheduler scheduler, int id)
        {
            this.scheduler = scheduler;
            this.id = id;
        }

        /// <summary>计时器编号；0 表示无效句柄。</summary>
        internal int Id { get { return id; } }

        /// <summary>计时器是否还在跑。一次性计时器触发过之后、任何计时器被取消之后都变 false。</summary>
        public bool IsValid
        {
            get { return scheduler != null && scheduler.IsAlive(id); }
        }

        /// <summary>取消。已经触发过或已经取消过的再调一次也没事。</summary>
        public void Cancel()
        {
            if (scheduler != null)
                scheduler.Cancel(id);
        }

        public bool Equals(TimerHandle other)
        {
            return ReferenceEquals(scheduler, other.scheduler) && id == other.id;
        }

        public override bool Equals(object obj)
        {
            return obj is TimerHandle && Equals((TimerHandle)obj);
        }

        public override int GetHashCode()
        {
            return (scheduler != null ? scheduler.GetHashCode() : 0) * 397 ^ id;
        }

        public override string ToString()
        {
            return IsValid ? "Timer#" + id : "Timer(已结束)";
        }
    }
}