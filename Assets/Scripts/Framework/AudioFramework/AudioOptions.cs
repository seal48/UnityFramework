using System;
using UnityEngine;

namespace GameFramework.Audio
{
    /// <summary>音频框架的初始化参数，由 GameController 填好后传给 AudioManager.Init。</summary>
    [Serializable]
    public sealed class AudioInitOptions
    {
        [Tooltip("音频根节点的名字，所有 AudioSource 都挂在它下面")]
        public string RootName = "[Audio]";

        [Tooltip("切场景时保留音频根节点（BGM 不该因为切场景就断）")]
        public bool DontDestroyOnLoad = true;

        [Tooltip("同时能播多少个音效。超了会抢最早开始的那个（不做混音优先级，够用就行）")]
        public int MaxSfxVoices = 16;

        [Tooltip("总音量（0~1）。实际生效在 AudioListener 上，等于全局静音开关")]
        public float MasterVolume = 1f;

        [Tooltip("背景音乐音量（0~1）")]
        public float BgmVolume = 1f;

        [Tooltip("音效音量（0~1）")]
        public float SfxVolume = 1f;

        [Tooltip("3D 音效的衰减起点（米）")]
        public float SfxMinDistance = 1f;

        [Tooltip("3D 音效的衰减终点（米）")]
        public float SfxMaxDistance = 30f;

        [Tooltip("BGM 默认淡入 / 淡出时长（秒）")]
        public float DefaultFade = 0.4f;

        [Tooltip("音效目录。代码里传短名（不含 /）时，按「目录 + 短名 + 后缀」去找")]
        public string SfxFolder = "Assets/Audio/Sfx/";

        [Tooltip("BGM 目录。同上")]
        public string BgmFolder = "Assets/Audio/Bgm/";

        [Tooltip("短名找文件时依次尝试的后缀")]
        public string[] SearchExtensions = new string[] { ".wav", ".ogg", ".mp3", ".aiff" };

        [Tooltip("初始化完成后打一条日志")]
        public bool LogOnInit = true;
    }

    /// <summary>一次音效播放的句柄。短音 / 循环音效（引擎声）用它停。</summary>
    public readonly struct AudioHandle
    {
        /// <summary>无效句柄：什么都没在播。</summary>
        public static readonly AudioHandle Invalid = default(AudioHandle);

        private readonly AudioVoice voice;
        private readonly int version;

        internal AudioHandle(AudioVoice voice, int version)
        {
            this.voice = voice;
            this.version = version;
        }

        /// <summary>是否还在播。这个通道被复用给别人之后会变 false。</summary>
        public bool IsPlaying
        {
            get { return voice != null && voice.Version == version && voice.Source != null && voice.Source.isPlaying; }
        }

        /// <summary>停掉（音效不做淡出，直接停）。</summary>
        public void Stop()
        {
            if (IsPlaying)
                voice.Source.Stop();
        }

        /// <summary>音量系数（0~1），相对 SfxVolume。</summary>
        public void SetVolumeScale(float scale)
        {
            if (!IsPlaying)
                return;

            voice.VolumeScale = Mathf.Clamp01(scale);

            if (voice.Manager != null)
                voice.Manager.ApplyVoiceVolume(voice);
        }

        public override string ToString()
        {
            return IsPlaying ? "AudioVoice#" + voice.Version : "Audio(已结束)";
        }
    }

    /// <summary>一个音效通道（内部用，外面只看 AudioHandle）。</summary>
    internal sealed class AudioVoice
    {
        internal AudioManager Manager;
        internal AudioSource Source;
        internal int Version;
        internal int Order;
        internal float VolumeScale = 1f;
        internal string ClipLocation;
    }
}