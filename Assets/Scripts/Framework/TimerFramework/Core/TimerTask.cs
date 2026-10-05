using System;

namespace GameFramework.Timer
{
    /// <summary>
    /// 计时器内部条目。外部只见 TimerHandle，拿不到这个对象。
    /// 存的是「到期时刻」（时间轴上的秒数），不是「还剩多少秒」，
    /// 所以宿主 Tick 抖多少下都不会影响到期点。
    /// </summary>
    internal sealed class TimerTask
    {
        /// <summary>唯一编号，TimerHandle 靠它对上号。</summary>
        internal int Id;

        /// <summary>归属对象，用来按组取消。</summary>
        internal object Owner;

        /// <summary>间隔（秒）。0 表示一次性任务。</summary>
        internal double Interval;

        /// <summary>到期时刻（时间轴上的秒数）。</summary>
        internal double Deadline;

        /// <summary>还要触发几次；-1 = 无限循环。</summary>
        internal long RepeatCount;

        /// <summary>触发时调用。</summary>
        internal Action Callback;

        /// <summary>条件型计时器：不为 null 时每次 Tick 问一次，返回 true 才触发。</summary>
        internal Func<bool> Condition;

        /// <summary>条件型计时器的超时时刻，&lt;= 0 表示不超时。</summary>
        internal double TimeoutDeadline;

        /// <summary>最早在第几次 Tick 之后才能触发（NextTick 用）。</summary>
        internal int MinTick;

        /// <summary>已取消：本轮结束时从列表里摘掉。</summary>
        internal bool Cancelled;

        /// <summary>已跑完：同上。</summary>
        internal bool Done;

        /// <summary>日志里用的可读名字。</summary>
        internal string Describe()
        {
            string owner = Owner != null ? Owner.GetType().Name : "无归属";

            if (Callback != null)
            {
                Type type = Callback.Method.DeclaringType;
                string method = type != null ? type.Name + "." + Callback.Method.Name : Callback.Method.Name;
                return "#" + Id + " " + owner + " → " + method;
            }

            return "#" + Id + " " + owner + " → 条件判断";
        }
    }
}