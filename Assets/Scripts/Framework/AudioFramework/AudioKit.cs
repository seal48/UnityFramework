using GameFramework.Log;
using UnityEngine;

namespace GameFramework.Audio
{
    /// <summary>
    /// 音频的全局快捷入口，省得到处写一长串：
    ///
    ///     AudioKit.PlayBgm("lobby");
    ///     AudioKit.PlaySfx("click");
    ///     AudioKit.PlaySfxOn("engine", car.transform);
    ///     AudioKit.BgmVolume = 0.5f;
    ///
    /// 传短名（不含 /）时会按 AudioInitOptions 里的 SfxFolder / BgmFolder + 后缀去找文件，
    /// 例如 "click" -> Assets/Audio/Sfx/click.wav。
    ///
    /// 还没初始化（ProcedureInitAudio 之前）时只打一条警告，不抛异常。
    ///
    /// 依赖方向：这里**不引用 GameController** —— 由 <see cref="AudioManager"/> 初始化时把自己登记进来，
    /// 框架层因此保持对业务层零依赖。
    /// </summary>
    public static class AudioKit
    {
        private static bool warned;

        /// <summary>由 AudioManager 在 Init / Shutdown 里登记和注销。业务代码不要直接改它。</summary>
        internal static AudioManager Registered;

        /// <summary>底层管理器。没起来时是 null。</summary>
        public static AudioManager Manager
        {
            get { return Registered; }
        }

        /// <summary>音频是否已经初始化。</summary>
        public static bool IsReady
        {
            get
            {
                AudioManager manager = Manager;
                return manager != null && manager.IsInitialized;
            }
        }

        /// <summary>BGM 是否在播。</summary>
        public static bool IsBgmPlaying { get { return IsReady && Manager.IsBgmPlaying; } }

        /// <summary>当前 BGM 的地址，没在播时是 null。</summary>
        public static string CurrentBgm { get { return IsReady ? Manager.CurrentBgm : null; } }

        #region 音量

        /// <summary>总音量（0~1），等于全局静音开关。</summary>
        public static float MasterVolume
        {
            get { return IsReady ? Manager.MasterVolume : 1f; }
            set { if (Ready()) Manager.MasterVolume = value; }
        }

        /// <summary>BGM 音量（0~1）。</summary>
        public static float BgmVolume
        {
            get { return IsReady ? Manager.BgmVolume : 1f; }
            set { if (Ready()) Manager.BgmVolume = value; }
        }

        /// <summary>音效音量（0~1）。</summary>
        public static float SfxVolume
        {
            get { return IsReady ? Manager.SfxVolume : 1f; }
            set { if (Ready()) Manager.SfxVolume = value; }
        }

        /// <summary>静音开关（不改上面三个的值）。</summary>
        public static bool MuteAll
        {
            get { return IsReady && Manager.MuteAll; }
            set { if (Ready()) Manager.MuteAll = value; }
        }

        #endregion

        #region BGM

        /// <summary>播放背景音乐。已经在放同一首时什么都不做。</summary>
        public static void PlayBgm(string location, bool loop = true, float fade = -1f)
        {
            if (Ready()) Manager.PlayBgm(location, loop, fade);
        }

        /// <summary>停止背景音乐（淡出）。</summary>
        public static void StopBgm(float fade = -1f)
        {
            if (Ready()) Manager.StopBgm(fade);
        }

        /// <summary>暂停背景音乐。</summary>
        public static void PauseBgm()
        {
            if (Ready()) Manager.PauseBgm();
        }

        /// <summary>继续播放暂停的背景音乐。</summary>
        public static void ResumeBgm()
        {
            if (Ready()) Manager.ResumeBgm();
        }

        #endregion

        #region 音效

        /// <summary>播一个 2D 音效（界面点击、系统提示）。</summary>
        public static AudioHandle PlaySfx(string location, float volumeScale = 1f)
        {
            return Ready() ? Manager.PlaySfx(location, volumeScale) : AudioHandle.Invalid;
        }

        /// <summary>在世界坐标上播一个 3D 音效。</summary>
        public static AudioHandle PlaySfxAt(string location, Vector3 position, float volumeScale = 1f)
        {
            return Ready() ? Manager.PlaySfxAt(location, position, volumeScale) : AudioHandle.Invalid;
        }

        /// <summary>跟着某个物体播 3D 音效（引擎声这种）。</summary>
        public static AudioHandle PlaySfxOn(string location, Transform follow, float volumeScale = 1f)
        {
            return Ready() ? Manager.PlaySfxOn(location, follow, volumeScale) : AudioHandle.Invalid;
        }

        /// <summary>停掉所有音效。</summary>
        public static void StopAllSfx()
        {
            if (Ready()) Manager.StopAllSfx();
        }

        #endregion

        private static bool Ready()
        {
            if (IsReady)
                return true;

            if (!warned)
            {
                warned = true;
                GameLog.Warn(LogTag.Audio, "音频还没初始化就在调了（是不是在 ProcedureInitAudio 之前？）。");
            }

            return false;
        }
    }
}