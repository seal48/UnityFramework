using GameFramework.Log;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameFramework.Procedure
{
    /// <summary>
    /// 流程管理器（轻量状态机）：登记一批 Procedure，同一时刻只跑一个。
    /// 由 GameController 创建、用 Update 驱动；流程自己决定什么时候切到下一个。
    ///
    /// 这样启动顺序就是「看得见的一段段代码」，而不是散落在各个回调里。
    /// </summary>
    public sealed class ProcedureManager
    {
        private readonly Dictionary<Type, ProcedureBase> procedures = new Dictionary<Type, ProcedureBase>();
        private Type pending;
        private bool changing;
        private float elapsed;

        /// <summary>当前流程；还没启动时为 null。</summary>
        public ProcedureBase Current { get; private set; }

        /// <summary>当前流程已经跑了多久（未缩放秒数）。</summary>
        public float CurrentElapsed { get { return elapsed; } }

        public ProcedureManager(params ProcedureBase[] list)
        {
            if (list == null) return;

            for (var i = 0; i < list.Length; i++)
                Add(list[i]);
        }

        /// <summary>登记一个流程（同一类型只登记一次）。</summary>
        public void Add(ProcedureBase procedure)
        {
            if (procedure == null) return;

            var type = procedure.GetType();
            if (procedures.ContainsKey(type))
            {
                GameLog.ErrorFormat(LogTag.Procedure, "重复登记流程：{0}", type.Name);
                return;
            }

            procedure.Manager = this;
            procedures.Add(type, procedure);
        }

        public T Get<T>() where T : ProcedureBase
        {
            ProcedureBase procedure;
            return procedures.TryGetValue(typeof(T), out procedure) ? (T)procedure : null;
        }

        public ProcedureBase Get(Type type)
        {
            if (type == null) return null;

            ProcedureBase procedure;
            return procedures.TryGetValue(type, out procedure) ? procedure : null;
        }

        /// <summary>启动第一个流程。</summary>
        public void Start<T>() where T : ProcedureBase
        {
            ChangeState(typeof(T));
        }

        /// <summary>切到下一个流程。</summary>
        public void ChangeState<T>() where T : ProcedureBase
        {
            ChangeState(typeof(T));
        }

        public void ChangeState(Type type)
        {
            if (type == null) return;

            // 切换过程中又被请求切换（典型场景：OnEnter 里直接切下一个）→ 排队，本次切换完接着做
            pending = type;
            if (changing) return;

            changing = true;
            try
            {
                while (pending != null)
                {
                    var target = pending;
                    pending = null;
                    Apply(target);
                }
            }
            finally
            {
                changing = false;
                pending = null;
            }
        }

        private void Apply(Type type)
        {
            ProcedureBase next;
            if (!procedures.TryGetValue(type, out next))
            {
                GameLog.ErrorFormat(LogTag.Procedure, "没有登记流程：{0}", type.Name);
                return;
            }

            var previous = Current;
            if (previous == next) return;

            if (previous != null)
            {
                previous.IsRunning = false;
                previous.OnLeave(next);
            }

            Current = next;
            elapsed = 0f;
            next.IsRunning = true;

            GameLog.InfoFormat(LogTag.Procedure, "{0} → {1}", previous == null ? "(启动)" : previous.GetType().Name, next.GetType().Name);
            next.OnEnter(previous);
        }

        /// <summary>由 GameController.Update 驱动。</summary>
        public void Tick(float deltaTime, float unscaledDeltaTime)
        {
            if (Current == null) return;

            elapsed += unscaledDeltaTime;
            Current.OnUpdate(deltaTime, unscaledDeltaTime);
        }
    }
}
