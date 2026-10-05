using System;
using UnityEngine;

namespace GameFramework.Storage
{
    /// <summary>
    /// 本地设置（settings.json）。这些是「设备级」的：换账号、换个区都不该跟着变，
    /// 所以不放服务器。改了之后调 GameController.Instance.Storage.NotifySettingsChanged()。
    ///
    /// 结构改了就加字段 + 把 LocalStorageManager.SettingsVersion +1，并补一条迁移，别直接改字段含义。
    /// </summary>
    [Serializable]
    public sealed class LocalSettings
    {
        // ---- 音频（BGM / 音效音量由以后的音频系统读取，框架只负责存）----

        public float VolumeMaster = 1f;
        public float VolumeBgm = 1f;
        public float VolumeSfx = 1f;
        public bool MuteAll = false;

        // ---- 画面 ----

        [Tooltip("-1 = 用 QualitySettings 里当前设置的值，>= 0 = 强制这一档")]
        public int QualityLevel = -1;

        [Tooltip("目标帧率。<= 0 = 不动引擎设置")]
        public int TargetFrameRate = 60;

        // ---- 手感 / 表现 ----

        public bool Vibration = true;

        // ---- 其它 ----

        public string LanguageCode = "zh-CN";

        /// <summary>下次启动用缓存里的 token 自动登录（token 失效了会自动退回手动登录）。</summary>
        public bool AutoLogin = true;

        /// <summary>
        /// 记住密码：勾上之后密码以混淆形式存在本地，回前台断线可自动重连、登录界面可预填。
        /// 关闭则只在本次运行内有效（重连需要重新输密码）。
        /// </summary>
        public bool RememberPassword = true;
    }
}