using System;

namespace GameFramework.Event
{
    /// <summary>
    /// 事件总线：模块之间只认「事件类型」，互相不认识，方便拆模块和复用。
    ///
    /// 只允许在主线程使用。网络线程收到的消息请先经 IDispatcher 回到主线程，再发事件
    /// （GameClientBehaviour 已经这么做了，业务侧直接写在回调里即可）。
    ///
    /// 用法：
    ///   // 订阅（返回的句柄 Dispose 就退订）
    ///   var sub = bus.Subscribe&lt;PlayerLevelUpEvent&gt;(OnLevelUp, priority: 10);
    ///
    ///   // 发布
    ///   bus.Publish(new PlayerLevelUpEvent { PlayerId = 1, Level = 5 });   // 立即派发
    ///   bus.Post(new PlayerLevelUpEvent { PlayerId = 1, Level = 5 });      // 入队，下一帧派发
    ///
    ///   sub.Dispose();   // 退订
    /// </summary>
    public interface IEventBus
    {
        /// <summary>当前订阅总数（所有事件类型加起来）。调试 / 查泄漏用。</summary>
        int SubscriberCount { get; }

        /// <summary>
        /// 订阅事件。返回的句柄 Dispose 即退订；也可以直接调 Unsubscribe。
        /// priority 越大越先收到（同一事件类型内排序，相等则按订阅先后）。
        /// </summary>
        IDisposable Subscribe<T>(Action<T> handler, int priority = 0) where T : IGameEvent;

        /// <summary>只收一次，触发后自动退订。返回的句柄同样可以提前 Dispose。</summary>
        IDisposable SubscribeOnce<T>(Action<T> handler, int priority = 0) where T : IGameEvent;

        /// <summary>退订一个 handler。找不到（或已经退过）返回 false，不报错。</summary>
        bool Unsubscribe<T>(Action<T> handler) where T : IGameEvent;

        /// <summary>退掉某个事件类型的全部订阅。</summary>
        void UnsubscribeAll<T>() where T : IGameEvent;

        /// <summary>立即派发（同步）。订阅方按优先级依次执行。</summary>
        void Publish<T>(T evt) where T : IGameEvent;

        /// <summary>入队，等下一次 Tick 派发。高频事件、或者在事件处理里又要发事件时用它，避免递归。</summary>
        void Post<T>(T evt) where T : IGameEvent;

        /// <summary>派发 Post 入队的事件。由 GameController.Update 驱动，业务不要自己调。</summary>
        void Tick(float deltaTime, float unscaledDeltaTime);

        /// <summary>清空所有订阅和未派发的事件。</summary>
        void Clear();

        /// <summary>导出当前所有订阅，查「谁还在听」用。</summary>
        string Dump();
    }
}
