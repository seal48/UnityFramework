using System;
using UnityEngine.SceneManagement;

namespace GameFramework.Resource
{
    /// <summary>
    /// 一次场景加载的句柄。加载过程中每帧读 Progress（给加载界面用），完成后触发 Completed。
    /// 具体实现放在各自的资源后端里（YooAsset 那份在 YooSceneLoadOperation.cs）。
    /// </summary>
    public interface ISceneLoadOperation
    {
        /// <summary>加载时用的地址。</summary>
        string Location { get; }

        /// <summary>场景名（加载完成前是空字符串）。</summary>
        string SceneName { get; }

        /// <summary>是否加载完成（挂起加载也算完成）。</summary>
        bool IsDone { get; }

        /// <summary>进度 0~1。</summary>
        float Progress { get; }

        /// <summary>场景对象，加载完成后有效。</summary>
        Scene Scene { get; }

        /// <summary>失败原因，成功时为 null。</summary>
        string LastError { get; }

        /// <summary>加载完成（不管成功失败）。加之前已经完成的话会立刻回调。</summary>
        event Action<ISceneLoadOperation> Completed;

        /// <summary>把场景设为激活场景（suspendLoad = true 时用）。</summary>
        bool Activate();

        /// <summary>卸载场景，卸载完回调。卸载会自动释放句柄引用，之后不要再 Release。</summary>
        void UnloadAsync(Action onComplete);

        /// <summary>只释放句柄引用、不卸载场景（切换下一个场景前调用）。</summary>
        void Release();
    }
}