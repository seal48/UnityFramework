using System;
using UnityEngine;

namespace GameFramework.Timer
{
    /// <summary>计时器走哪条时间轴。</summary>
    public enum TimerMode
    {
        /// <summary>游戏时间（Time.deltaTime）：受 timeScale 影响，暂停时一起停。玩法逻辑用这个。</summary>
        Scaled = 0,

        /// <summary>真实时间（Time.unscaledDeltaTime）：不受暂停影响。界面、超时、重连用这个。</summary>
        Unscaled = 1,
    }

    /// <summary>计时器框架的初始化参数，由 GameController 填好后传给 TimerManager.Init。</summary>
    [Serializable]
    public sealed class TimerInitOptions
    {
        [Tooltip("同时存在的计时器上限，0 = 不限制。超过上限时新注册直接失败并报错，用来抓「只加不减」的泄漏")]
        public int MaxTimers = 0;

        [Tooltip("单帧触发超过多少个计时器就打一条警告，0 = 不检查。间隔为 0 的死循环会在这里现形")]
        public int WarnFiresPerFrame = 1000;

        [Tooltip("单个回调耗时超过多少毫秒就打警告，0 = 不检查")]
        public float WarnSlowCallbackMs = 20f;

        [Tooltip("初始化完成后打一条统计日志")]
        public bool LogOnInit = true;
    }
}
