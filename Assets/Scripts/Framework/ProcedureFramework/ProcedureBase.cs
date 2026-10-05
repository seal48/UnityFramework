namespace GameFramework.Procedure
{
    /// <summary>
    /// 流程基类：一个流程就是启动过程中的一个阶段（资源初始化、登录、进大厅…）。
    /// 由 ProcedureManager 统一管理，同一时刻只有一个流程在跑。
    ///
    /// 约定：切流程用 ChangeState&lt;T&gt;()，可以在 OnEnter 里直接调，也可以在异步回调里调。
    /// </summary>
    public abstract class ProcedureBase
    {
        /// <summary>所属管理器。</summary>
        public ProcedureManager Manager { get; internal set; }

        /// <summary>当前是否在运行。</summary>
        public bool IsRunning { get; internal set; }

        /// <summary>进入本流程。from 是上一个流程，首次启动时为 null。</summary>
        public virtual void OnEnter(ProcedureBase from) { }

        /// <summary>离开本流程。to 是下一个流程。</summary>
        public virtual void OnLeave(ProcedureBase to) { }

        /// <summary>每帧调用，只有当前流程会收到。</summary>
        public virtual void OnUpdate(float deltaTime, float unscaledDeltaTime) { }

        /// <summary>切到下一个流程。</summary>
        protected void ChangeState<T>() where T : ProcedureBase
        {
            if (Manager != null)
                Manager.ChangeState<T>();
        }

        /// <summary>取另一个已登记的流程实例（跨流程共享数据时用）。</summary>
        protected T Get<T>() where T : ProcedureBase
        {
            return Manager != null ? Manager.Get<T>() : null;
        }
    }
}
