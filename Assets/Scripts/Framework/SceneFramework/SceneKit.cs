using System;
using UnityEngine.SceneManagement;

namespace GameFramework.Scenes
{
    /// <summary>
    /// 场景的全局快捷入口：
    ///
    ///     SceneKit.Load("Lobby");                 // Assets/Scenes/Lobby.unity
    ///     SceneKit.LoadAsync("Battle", (ok, err) => { });
    ///     SceneKit.LoadAdditive("BattleUI");
    ///     SceneKit.Unload("BattleUI");
    ///
    /// 还没初始化（ProcedureInitScene 之前）时只打一条警告。
    ///
    /// 依赖方向：这里**不引用 GameController** —— 由 <see cref="SceneLoader"/> 初始化时把自己登记进来，
    /// 框架层因此保持对业务层零依赖。
    /// </summary>
    public static class SceneKit
    {
        /// <summary>由 SceneLoader 在 Init / Shutdown 里登记和注销。业务代码不要直接改它。</summary>
        internal static SceneLoader Registered;

        /// <summary>底层管理器。没起来时是 null。</summary>
        public static SceneLoader Manager
        {
            get { return Registered; }
        }

        /// <summary>场景管理是否已经初始化。</summary>
        public static bool IsReady
        {
            get
            {
                SceneLoader manager = Manager;
                return manager != null && manager.IsInitialized;
            }
        }

        /// <summary>是否正在加载。</summary>
        public static bool IsLoading { get { return IsReady && Manager.IsLoading; } }

        /// <summary>当前加载进度 0~1。</summary>
        public static float Progress { get { return IsReady ? Manager.Progress : 0f; } }

        /// <summary>当前场景名。</summary>
        public static string CurrentSceneName
        {
            get
            {
                if (IsReady)
                    return Manager.CurrentSceneName;

                Scene active = SceneManager.GetActiveScene();
                return active.IsValid() ? active.name : string.Empty;
            }
        }

        /// <summary>切主场景（Single）。</summary>
        public static void Load(string location, Action<bool, string> onComplete = null)
        {
            if (Ready()) Manager.LoadAsync(location, onComplete);
        }

        /// <summary>切主场景，带参数。</summary>
        public static void Load(string location, SceneLoadOptions loadOptions, Action<bool, string> onComplete = null)
        {
            if (Ready()) Manager.LoadAsync(location, loadOptions, onComplete);
        }

        /// <summary>叠加场景（Additive）。</summary>
        public static void LoadAdditive(string location, Action<bool, string> onComplete = null)
        {
            if (Ready()) Manager.LoadAdditiveAsync(location, onComplete);
        }

        /// <summary>叠加场景，带参数。</summary>
        public static void LoadAdditive(string location, SceneLoadOptions loadOptions,
            Action<bool, string> onComplete = null)
        {
            if (Ready()) Manager.LoadAdditiveAsync(location, loadOptions, onComplete);
        }

        /// <summary>卸载一个叠加场景。</summary>
        public static void Unload(string location, Action<bool, string> onComplete = null)
        {
            if (Ready()) Manager.UnloadAsync(location, onComplete);
        }

        private static bool Ready()
        {
            if (IsReady)
                return true;

            GameFramework.Log.GameLog.Warn(GameFramework.Log.LogTag.Scene,
                "场景管理还没初始化就在调了（是不是在 ProcedureInitScene 之前？）。");
            return false;
        }
    }
}