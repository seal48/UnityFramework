using System;
using GameFramework.Log;
using UnityEngine.SceneManagement;
using YooAsset;

namespace GameFramework.Resource
{
    /// <summary>把 YooAsset 的 SceneHandle 包装成 ISceneLoadOperation。</summary>
    internal sealed class YooSceneLoadOperation : ISceneLoadOperation
    {
        private SceneHandle handle;
        private bool completed;

        public event Action<ISceneLoadOperation> Completed;

        internal YooSceneLoadOperation(string location, SceneHandle sceneHandle)
        {
            Location = location;
            handle = sceneHandle;

            if (handle != null)
                handle.Completed += OnHandleCompleted;
        }

        public string Location { get; private set; }

        public string SceneName
        {
            get { return HasHandle ? handle.SceneName : string.Empty; }
        }

        public bool IsDone
        {
            get { return !HasHandle || handle.IsDone; }
        }

        public float Progress
        {
            get { return HasHandle ? handle.Progress : 1f; }
        }

        public Scene Scene
        {
            get { return HasHandle ? handle.SceneObject : default(Scene); }
        }

        public string LastError
        {
            get
            {
                if (!HasHandle)
                    return null;

                return handle.Status == EOperationStatus.Failed ? handle.LastError : null;
            }
        }

        /// <summary>
        /// 句柄还能不能用。YooAsset 加载新的 Single 场景时会自己回收上一个场景句柄，
        /// 这时候再去摸 Completed / Dispose 会直接抛 "SceneHandle is invalid"，所以每次访问前都得判一下。
        /// </summary>
        private bool HasHandle
        {
            get { return handle != null && handle.IsValid; }
        }

        public bool Activate()
        {
            return HasHandle && handle.ActivateScene();
        }

        public void UnloadAsync(Action onComplete)
        {
            SceneHandle h = handle;
            handle = null;

            if (h == null || h.IsValid == false)
            {
                if (onComplete != null) onComplete();
                return;
            }

            h.Completed -= OnHandleCompleted;

            // 卸载成功会自己释放句柄引用，这里不能再 Dispose
            UnloadSceneOperation operation = h.UnloadAsync();
            if (operation == null)
            {
                if (onComplete != null) onComplete();
                return;
            }

            operation.Completed += delegate { if (onComplete != null) onComplete(); };
        }

        public void Release()
        {
            SceneHandle h = handle;
            handle = null;

            if (h == null || h.IsValid == false)
                return;

            try
            {
                h.Completed -= OnHandleCompleted;
                h.Dispose();
            }
            catch (Exception e)
            {
                // 句柄可能在释放途中被资源系统回收，这里只兜底，不让它把调用方的流程打断
                GameLog.Warn(LogTag.Resource, "释放场景句柄时出错（多半是句柄已被回收）：" + Location + " → " + e.Message);
            }
        }

        private void OnHandleCompleted(SceneHandle h)
        {
            if (completed)
                return;

            completed = true;

            Action<ISceneLoadOperation> handler = Completed;
            if (handler != null)
                handler(this);
        }
    }
}