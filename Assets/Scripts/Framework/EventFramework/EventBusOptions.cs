using System;
using UnityEngine;

namespace GameFramework.Event
{
    /// <summary>
    /// 事件总线参数。由 GameController 填好后传给 EventBus，和其它框架的参数放在一起。
    /// </summary>
    [Serializable]
    public sealed class EventBusOptions
    {
        [Tooltip("把每次派发的事件打进 Console（开发期看事件流用，正式版关掉）")]
        public bool LogPublish = false;

        [Tooltip("一次 Tick 里单个事件类型最多派发多少条延迟事件，超过就判定死循环并中断（保护机制）")]
        public int MaxEventsPerFlush = 1000;
    }
}
