using System;
using System.Collections.Generic;
using GameFramework.Core;
using GameFramework.Log;
using GameFramework.Resource;
using UnityEngine;

namespace GameFramework.Audio
{
    /// <summary>
    /// 音频管理器：一棵常驻的 [Audio] 根节点 + 1 个 BGM 通道 + N 个音效通道。
    ///
    /// 不做 AudioMixer：总音量直接写 AudioListener.volume（等于全局静音），
    /// BGM / 音效音量按「分组音量 × 单次系数」乘到各自 AudioSource.volume 上，手机小游戏够用了。
    /// 以后要做总线滤波 / 混音，再把这一层接到 Mixer 即可。
    ///
    /// 同一个地址的 AudioClip 只加载一次，用引用计数复用：BGM 在播期间、音效在响期间各占一次引用，
    /// 播完自动释放，没人用了才真正卸载。
    ///
    /// 由 GameController 在 Awake 里创建（new）、ProcedureInitAudio 初始化，之后每帧 Tick。
    /// 业务用 GameController.Instance.Audio。
    /// </summary>
    public sealed class AudioManager : IGameModule, ITickable
    {
        /// <summary>一条音频地址的缓存：加载中排队、加载完复用、没人用了卸载。</summary>
        private sealed class ClipEntry
        {
            public AudioClip Clip;
            public ResourceAsset<AudioClip> Asset;
            public int Refs;
            public bool Loading;
            public string[] Candidates;
            public int CandidateIndex;
            public readonly List<Action<AudioClip>> Waiters = new List<Action<AudioClip>>();
        }

        /// <summary>淡出结束之后要接上的那首曲子。</summary>
        private sealed class PendingBgm
        {
            public string Location;
            public AudioClip Clip;
            public bool Loop;
            public float Fade;
        }

        private IResourceService resource;
        private AudioInitOptions options;

        private GameObject root;
        private AudioSource bgmSource;

        private readonly List<AudioVoice> voices = new List<AudioVoice>();
        private readonly Dictionary<string, ClipEntry> clips = new Dictionary<string, ClipEntry>(StringComparer.Ordinal);

        private static readonly string[] DefaultExtensions = new string[] { ".wav", ".ogg", ".mp3", ".aiff" };

        private int nextVersion = 1;
        private int orderCounter;

        private int bgmRequest;
        private string bgmLocation;
        private bool bgmFading;
        private bool bgmStopping;
        private float fadeFrom;
        private float fadeTo;
        private float fadeElapsed;
        private float fadeDuration;
        private float bgmVolumeScale = 1f;
        private PendingBgm pendingBgm;

        private float masterVolume = 1f;
        private float bgmVolume = 1f;
        private float sfxVolume = 1f;
        private bool muteAll;

        /// <summary>音量被业务改过（不是淡入淡出引起的那种）。GameController 靠它把音量写回存档。</summary>
        public event Action VolumeChanged;

        /// <summary>是否已经初始化。</summary>
        public bool IsInitialized { get { return root != null; } }

        /// <summary>当前正在播的 BGM 地址，没在播时为空。</summary>
        public string CurrentBgm
        {
            get { return bgmSource != null && bgmSource.isPlaying ? bgmLocation : null; }
        }

        /// <summary>BGM 是否在播。</summary>
        public bool IsBgmPlaying { get { return bgmSource != null && bgmSource.isPlaying; } }

        /// <summary>当前活着的音效通道数量（含循环音效）。</summary>
        public int ActiveSfxCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < voices.Count; i++)
                {
                    if (voices[i].Source != null && voices[i].Source.isPlaying)
                        count++;
                }

                return count;
            }
        }

        #region 音量

        /// <summary>总音量（0~1）。写 AudioListener.volume，等于全局静音开关。</summary>
        public float MasterVolume
        {
            get { return masterVolume; }
            set { SetMasterVolume(value); }
        }

        /// <summary>BGM 音量（0~1）。</summary>
        public float BgmVolume
        {
            get { return bgmVolume; }
            set { SetBgmVolume(value); }
        }

        /// <summary>音效音量（0~1）。</summary>
        public float SfxVolume
        {
            get { return sfxVolume; }
            set { SetSfxVolume(value); }
        }

        /// <summary>静音开关。跟总音量为 0 的区别是：它不影响存档里的音量值。</summary>
        public bool MuteAll
        {
            get { return muteAll; }
            set { SetMuteAll(value); }
        }

        public void SetMasterVolume(float value)
        {
            masterVolume = Mathf.Clamp01(value);
            ApplyVolumes();
            RaiseVolumeChanged();
        }

        public void SetBgmVolume(float value)
        {
            bgmVolume = Mathf.Clamp01(value);
            ApplyBgmVolume();
            RaiseVolumeChanged();
        }

        public void SetSfxVolume(float value)
        {
            sfxVolume = Mathf.Clamp01(value);
            for (int i = 0; i < voices.Count; i++)
                ApplyVoiceVolume(voices[i]);

            RaiseVolumeChanged();
        }

        public void SetMuteAll(bool value)
        {
            muteAll = value;
            ApplyVolumes();
            RaiseVolumeChanged();
        }

        /// <summary>一次性套用一组音量（读存档时用），不触发 VolumeChanged，避免和存档来回写。</summary>
        public void ApplyVolumes(float master, float bgm, float sfx, bool mute)
        {
            masterVolume = Mathf.Clamp01(master);
            bgmVolume = Mathf.Clamp01(bgm);
            sfxVolume = Mathf.Clamp01(sfx);
            muteAll = mute;
            ApplyVolumes();
        }

        private void RaiseVolumeChanged()
        {
            Action handler = VolumeChanged;
            if (handler != null)
                handler();
        }

        private void ApplyVolumes()
        {
            AudioListener.volume = muteAll ? 0f : masterVolume;
            ApplyBgmVolume();

            for (int i = 0; i < voices.Count; i++)
                ApplyVoiceVolume(voices[i]);
        }

        private void ApplyBgmVolume()
        {
            if (bgmSource != null)
                bgmSource.volume = bgmVolume * bgmVolumeScale;
        }

        /// <summary>把音效音量重新算到这个通道上（AudioHandle.SetVolumeScale 也会调它）。</summary>
        internal void ApplyVoiceVolume(AudioVoice voice)
        {
            if (voice == null || voice.Source == null)
                return;

            voice.Source.volume = sfxVolume * voice.VolumeScale;
        }

        #endregion

        #region 生命周期

        /// <summary>初始化。没有异步步骤：建根节点 + 铺好通道，直接回调成功。</summary>
        public void Init(IResourceService resourceService, AudioInitOptions initOptions, Action<bool, string> onComplete)
        {
            if (root != null)
            {
                if (onComplete != null) onComplete(true, "音频已经初始化过了");
                return;
            }

            if (resourceService == null)
            {
                if (onComplete != null) onComplete(false, "资源服务为空，无法初始化音频");
                return;
            }

            resource = resourceService;
            options = initOptions != null ? initOptions : new AudioInitOptions();

            // 登记给 AudioKit，让框架层的快捷入口不用去碰 GameController（保持 Game -> Framework 单向依赖）
            AudioKit.Registered = this;

            masterVolume = Mathf.Clamp01(options.MasterVolume);
            bgmVolume = Mathf.Clamp01(options.BgmVolume);
            sfxVolume = Mathf.Clamp01(options.SfxVolume);

            root = new GameObject(options.RootName);
            if (options.DontDestroyOnLoad)
                UnityEngine.Object.DontDestroyOnLoad(root);

            bgmSource = CreateSource("BGM", true);

            int voiceCount = Mathf.Max(1, options.MaxSfxVoices);
            for (int i = 0; i < voiceCount; i++)
            {
                AudioVoice voice = new AudioVoice();
                voice.Manager = this;
                voice.Source = CreateSource("SFX_" + i, false);
                voices.Add(voice);
            }

            ApplyVolumes();

            if (options.LogOnInit)
            {
                GameLog.Info(LogTag.Audio, "音频就绪：BGM 通道 1，音效通道 " + voiceCount +
                                          "，总音量 " + masterVolume + "，BGM " + bgmVolume + "，音效 " + sfxVolume);
            }

            if (onComplete != null) onComplete(true, "音频管理器就绪");
        }

        /// <summary>关闭：停掉所有声音，释放所有加载过的音频资源，销毁根节点。</summary>
        public void Shutdown()
        {
            for (int i = 0; i < voices.Count; i++)
            {
                AudioVoice voice = voices[i];
                if (voice.Source != null)
                {
                    voice.Source.Stop();
                    voice.Source.clip = null;
                }

                voice.Version++;
                ReleaseVoiceClip(voice);
            }

            voices.Clear();

            if (bgmSource != null)
            {
                bgmSource.Stop();
                bgmSource.clip = null;
                bgmSource = null;
            }

            bgmFading = false;
            bgmStopping = false;
            bgmVolumeScale = 1f;
            DropPendingBgm();
            ReleaseBgmClip();

            foreach (KeyValuePair<string, ClipEntry> pair in clips)
            {
                if (pair.Value.Asset != null)
                    pair.Value.Asset.Dispose();
            }

            clips.Clear();

            if (root != null)
            {
                UnityEngine.Object.Destroy(root);
                root = null;
            }

            resource = null;

            // 注销 AudioKit 的登记
            if (AudioKit.Registered == this)
                AudioKit.Registered = null;
            options = null;
        }

        /// <summary>每帧调用，由 GameController 转发。参数用 Time.unscaledDeltaTime：暂停时淡入淡出照走。</summary>
        public void Tick(float deltaTime, float unscaledDeltaTime)
        {
            if (root == null)
                return;

            TickBgmFade(unscaledDeltaTime);
            TickVoices();
        }

        private AudioSource CreateSource(string name, bool bgm)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(root.transform, false);

            AudioSource source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = bgm;
            source.spatialBlend = 0f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = options.SfxMinDistance;
            source.maxDistance = options.SfxMaxDistance;
            source.dopplerLevel = 0f;
            return source;
        }

        #endregion

        #region BGM

        /// <summary>
        /// 播放背景音乐。淡入时长默认用 options.DefaultFade；正在放别的曲子时会先淡出旧的再淡入新的。
        /// 同一个地址重复调用等于「确保这首在放」，不会重头开始。
        /// </summary>
        public void PlayBgm(string location, bool loop = true, float fade = -1f)
        {
            if (root == null || string.IsNullOrEmpty(location))
                return;

            if (fade < 0f)
                fade = options.DefaultFade;

            int request = ++bgmRequest;
            DropPendingBgm();

            if (bgmLocation == location && bgmSource.isPlaying)
                return;

            AcquireClip(location, true, clip =>
            {
                if (request != bgmRequest)
                {
                    if (clip != null)
                        ReleaseClip(location);
                    return;
                }

                if (clip == null)
                {
                    GameLog.Warn(LogTag.Audio, "BGM 加载失败：" + location);
                    return;
                }

                if (bgmSource.isPlaying)
                {
                    pendingBgm = new PendingBgm { Location = location, Clip = clip, Loop = loop, Fade = fade };
                    bgmStopping = false;
                    StartBgmFade(bgmVolumeScale, 0f, Mathf.Max(0.01f, fade * 0.5f));
                    return;
                }

                ApplyBgm(location, clip, loop, fade);
            });
        }

        /// <summary>停止背景音乐（淡出）。</summary>
        public void StopBgm(float fade = -1f)
        {
            if (root == null)
                return;

            if (fade < 0f)
                fade = options.DefaultFade;

            bgmRequest++;
            DropPendingBgm();

            if (!bgmSource.isPlaying)
            {
                bgmSource.clip = null;
                bgmVolumeScale = 1f;
                ReleaseBgmClip();
                return;
            }

            bgmStopping = true;
            StartBgmFade(bgmVolumeScale, 0f, fade);
        }

        /// <summary>暂停背景音乐（保留进度，ResumeBgm 接着放）。</summary>
        public void PauseBgm()
        {
            if (root != null && bgmSource != null && bgmSource.isPlaying)
                bgmSource.Pause();
        }

        /// <summary>继续播放暂停的背景音乐。</summary>
        public void ResumeBgm()
        {
            if (root != null && bgmSource != null && !bgmSource.isPlaying && bgmSource.clip != null)
                bgmSource.UnPause();
        }

        private void ApplyBgm(string location, AudioClip clip, bool loop, float fade)
        {
            ReleaseBgmClip();

            bgmLocation = location;
            bgmSource.clip = clip;
            bgmSource.loop = loop;
            bgmSource.volume = 0f;
            bgmVolumeScale = 0f;
            bgmSource.Play();

            StartBgmFade(0f, 1f, fade);
        }

        private void StartBgmFade(float from, float to, float duration)
        {
            fadeFrom = from;
            fadeTo = to;
            fadeElapsed = 0f;
            fadeDuration = duration;
            bgmVolumeScale = from;
            bgmFading = duration > 0f;

            ApplyBgmVolume();

            if (!bgmFading)
            {
                bgmVolumeScale = to;
                ApplyBgmVolume();
                OnBgmFadeDone();
            }
        }

        private void TickBgmFade(float deltaTime)
        {
            if (!bgmFading)
                return;

            fadeElapsed += deltaTime;

            float t = fadeDuration <= 0f ? 1f : Mathf.Clamp01(fadeElapsed / fadeDuration);
            bgmVolumeScale = Mathf.Lerp(fadeFrom, fadeTo, t);
            ApplyBgmVolume();

            if (t >= 1f)
            {
                bgmFading = false;
                OnBgmFadeDone();
            }
        }

        private void OnBgmFadeDone()
        {
            if (pendingBgm != null && fadeTo <= 0f)
            {
                PendingBgm next = pendingBgm;
                pendingBgm = null;
                ApplyBgm(next.Location, next.Clip, next.Loop, next.Fade);
                return;
            }

            if (bgmStopping && fadeTo <= 0f)
            {
                bgmStopping = false;

                if (bgmSource != null)
                {
                    bgmSource.Stop();
                    bgmSource.clip = null;
                }

                bgmVolumeScale = 1f;
                ReleaseBgmClip();
            }
        }

        private void DropPendingBgm()
        {
            if (pendingBgm == null)
                return;

            PendingBgm pending = pendingBgm;
            pendingBgm = null;
            ReleaseClip(pending.Location);
        }

        private void ReleaseBgmClip()
        {
            if (bgmLocation == null)
                return;

            ReleaseClip(bgmLocation);
            bgmLocation = null;
        }

        #endregion

        #region 音效

        /// <summary>播一个 2D 音效（界面点击、系统提示这类不跟位置的）。</summary>
        public AudioHandle PlaySfx(string location, float volumeScale = 1f)
        {
            return PlaySfxInternal(location, null, Vector3.zero, false, volumeScale);
        }

        /// <summary>在世界坐标上播一个 3D 音效。</summary>
        public AudioHandle PlaySfxAt(string location, Vector3 position, float volumeScale = 1f)
        {
            return PlaySfxInternal(location, null, position, true, volumeScale);
        }

        /// <summary>跟着某个物体播 3D 音效（引擎声这种要跟着走的）。</summary>
        public AudioHandle PlaySfxOn(string location, Transform follow, float volumeScale = 1f)
        {
            if (follow == null)
                return PlaySfxInternal(location, null, Vector3.zero, false, volumeScale);

            return PlaySfxInternal(location, follow, Vector3.zero, true, volumeScale);
        }

        /// <summary>停掉所有音效（循环音效在内，比如切场景时收尾）。</summary>
        public void StopAllSfx()
        {
            for (int i = 0; i < voices.Count; i++)
                StopVoice(voices[i]);
        }

        private AudioHandle PlaySfxInternal(string location, Transform follow, Vector3 position, bool spatial, float volumeScale)
        {
            if (root == null || string.IsNullOrEmpty(location))
                return AudioHandle.Invalid;

            AudioVoice voice = RentVoice();
            if (voice == null)
            {
                GameLog.Warn(LogTag.Audio, "没有空闲音效通道，丢弃：" + location);
                return AudioHandle.Invalid;
            }

            int version = voice.Version;
            AudioSource source = voice.Source;

            voice.VolumeScale = Mathf.Clamp01(volumeScale);
            source.spatialBlend = spatial ? 1f : 0f;
            source.loop = false;

            if (spatial)
            {
                if (follow != null)
                {
                    source.transform.SetParent(follow, false);
                    source.transform.localPosition = Vector3.zero;
                }
                else
                {
                    source.transform.SetParent(root.transform, false);
                    source.transform.position = position;
                }
            }
            else
            {
                source.transform.SetParent(root.transform, false);
                source.transform.localPosition = Vector3.zero;
            }

            AcquireClip(location, false, clip =>
            {
                // 通道可能已经被别人抢走了，这时候不能再动它
                if (voice.Version != version)
                {
                    if (clip != null)
                        ReleaseClip(location);
                    return;
                }

                if (clip == null)
                {
                    GameLog.Warn(LogTag.Audio, "音效加载失败：" + location);
                    return;
                }

                voice.ClipLocation = location;
                source.clip = clip;
                ApplyVoiceVolume(voice);
                source.Play();
            });

            return new AudioHandle(voice, version);
        }

        /// <summary>拿一个空闲通道；满了就抢最早开始的那个。</summary>
        private AudioVoice RentVoice()
        {
            AudioVoice free = null;
            for (int i = 0; i < voices.Count; i++)
            {
                AudioSource source = voices[i].Source;
                if (source != null && !source.isPlaying)
                {
                    free = voices[i];
                    break;
                }
            }

            if (free == null && voices.Count > 0)
            {
                AudioVoice oldest = voices[0];
                for (int i = 1; i < voices.Count; i++)
                {
                    if (voices[i].Order < oldest.Order)
                        oldest = voices[i];
                }

                free = oldest;
                if (free.Source != null && free.Source.isPlaying)
                {
                    GameLog.Warn(LogTag.Audio, "音效通道用满（" + voices.Count + "），抢占最早开始的通道。");
                    StopVoice(free);
                }
            }

            if (free == null)
                return null;

            free.Version = nextVersion++;
            free.Order = ++orderCounter;
            free.VolumeScale = 1f;
            free.Source.Stop();
            free.Source.clip = null;
            free.Source.loop = false;
            return free;
        }

        private void StopVoice(AudioVoice voice)
        {
            if (voice == null)
                return;

            if (voice.Source != null)
            {
                voice.Source.Stop();
                voice.Source.clip = null;
            }

            voice.Version++;
            ReleaseVoiceClip(voice);
        }

        private void TickVoices()
        {
            for (int i = 0; i < voices.Count; i++)
            {
                AudioVoice voice = voices[i];
                if (voice.ClipLocation == null)
                    continue;

                if (voice.Source != null && voice.Source.isPlaying)
                    continue;

                // 播完了：把通道还回去，释放它占的那次资源引用
                voice.Source.clip = null;
                ReleaseVoiceClip(voice);
            }
        }

        private void ReleaseVoiceClip(AudioVoice voice)
        {
            if (voice.ClipLocation == null)
                return;

            ReleaseClip(voice.ClipLocation);
            voice.ClipLocation = null;
        }

        #endregion

        #region 资源引用计数

        private void AcquireClip(string location, bool bgm, Action<AudioClip> onReady)
        {
            ClipEntry entry;
            if (!clips.TryGetValue(location, out entry))
            {
                entry = new ClipEntry();
                clips.Add(location, entry);
            }

            entry.Refs++;

            if (entry.Clip != null)
            {
                if (onReady != null)
                    onReady(entry.Clip);
                return;
            }

            entry.Waiters.Add(onReady);

            if (entry.Loading)
                return;

            entry.Loading = true;
            entry.Candidates = BuildCandidates(location, bgm);
            entry.CandidateIndex = 0;
            TryLoadCandidate(location, entry);
        }

        /// <summary>短名可能对应几个候选路径（各后缀），依次试，第一个加载成功的就用它。</summary>
        private void TryLoadCandidate(string key, ClipEntry entry)
        {
            if (entry.CandidateIndex >= entry.Candidates.Length)
            {
                entry.Loading = false;
                clips.Remove(key);
                GameLog.Error(LogTag.Audio, "音频加载失败，已试过：" + string.Join("、", entry.Candidates));
                FlushWaiters(entry, null);
                return;
            }

            string location = entry.Candidates[entry.CandidateIndex];
            entry.CandidateIndex++;

            resource.LoadAssetAsync<AudioClip>(location, handle =>
            {
                // 加载期间这条记录可能已经被释放掉了，那就别再往缓存里塞
                ClipEntry current;
                if (!clips.TryGetValue(key, out current) || current != entry)
                {
                    if (handle != null)
                        handle.Dispose();
                    return;
                }

                AudioClip clip = handle != null ? handle.Asset : null;
                if (clip == null)
                {
                    if (handle != null)
                        handle.Dispose();

                    TryLoadCandidate(key, entry);
                    return;
                }

                entry.Clip = clip;
                entry.Asset = handle;
                entry.Loading = false;
                FlushWaiters(entry, clip);
            });
        }

        /// <summary>把短名补成工程里的真实地址；带 / 的地址原样使用。</summary>
        private string[] BuildCandidates(string location, bool bgm)
        {
            if (location.IndexOf('/') >= 0 || location.IndexOf('\\') >= 0)
                return new string[] { location };

            string folder = bgm ? options.BgmFolder : options.SfxFolder;
            if (string.IsNullOrEmpty(folder))
                return new string[] { location };

            if (folder[folder.Length - 1] != '/')
                folder = folder + "/";

            string bare = location;
            string ext = null;
            int dot = bare.LastIndexOf('.');
            if (dot > 0)
            {
                ext = bare.Substring(dot);
                bare = bare.Substring(0, dot);
            }

            string[] exts = options.SearchExtensions;
            if (exts == null || exts.Length == 0)
                exts = DefaultExtensions;

            // 明确写了后缀就把它排在最前面
            int offset = string.IsNullOrEmpty(ext) ? 0 : 1;
            string[] result = new string[offset + exts.Length];

            if (offset == 1)
                result[0] = folder + bare + ext;

            for (int i = 0; i < exts.Length; i++)
                result[offset + i] = folder + bare + exts[i];

            return result;
        }

        private static void FlushWaiters(ClipEntry entry, AudioClip clip)
        {
            List<Action<AudioClip>> waiters = new List<Action<AudioClip>>(entry.Waiters);
            entry.Waiters.Clear();

            for (int i = 0; i < waiters.Count; i++)
            {
                if (waiters[i] != null)
                    waiters[i](clip);
            }
        }
        private void ReleaseClip(string location)
        {
            ClipEntry entry;
            if (!clips.TryGetValue(location, out entry))
                return;

            entry.Refs--;

            if (entry.Refs > 0)
                return;

            if (entry.Asset != null)
            {
                entry.Asset.Dispose();
                entry.Asset = null;
            }

            clips.Remove(location);
        }

        #endregion
    }
}