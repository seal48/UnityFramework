using GameFramework.Core;
using GameFramework.Log;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameFramework.Storage
{
    /// <summary>
    /// 本地存储（设置 / 账号 / 缓存）。由 GameController 初始化，是启动流程里最早跑的框架之一。
    ///
    /// 这里只放「设备级、会话级」的数据：设置、设备 ID、登录 token、协议同意、引导标记、玩家数据快照。
    /// **玩家养成数据不是本地存档** —— 那是服务端的事，本地存一份只是为了进游戏时先把界面渲染出来。
    ///
    /// 文件布局（{persistentDataPath}/{FolderName}/）：
    ///   settings.json  本地设置
    ///   account.json   设备 ID / 协议同意 / 上次账号 / token
    ///   cache.json     引导标记 / 弹窗已读 / 零散 KV / 玩家数据快照
    ///
    /// 每个文件都带结构版本号，改结构时加一条迁移即可，不用让玩家清档。
    /// 读盘在 Init 里同步做完（几 KB 的文件，微秒级），写盘平时攒着、改完隔 AutoFlushDelay 落一次，
    /// 切后台 / 退出时强制落盘。只在主线程访问。
    /// </summary>
    public sealed class LocalStorageManager : IGameModule, ITickable, IDisposable
    {
        public const int SettingsVersion = 2;
        public const int AccountVersion = 3;
        public const int CacheVersion = 1;

        private readonly List<StorageFileBase> files = new List<StorageFileBase>();

        private StorageFile<LocalSettings> settingsFile;
        private StorageFile<LocalAccount> accountFile;
        private StorageFile<LocalCache> cacheFile;

        private StorageOptions options;
        private bool initialized;
        private bool disposed;
        private bool canPersist = true;
        private float flushTimer;

        /// <summary>是否初始化完成。</summary>
        public bool IsInitialized { get { return initialized; } }

        /// <summary>存档根目录。</summary>
        public string RootPath { get; private set; }

        /// <summary>磁盘可用（目录建得出来）。false 表示这次运行是纯内存模式，关掉游戏数据就没了。</summary>
        public bool CanPersist { get { return canPersist; } }

        /// <summary>还没落盘的文件数。</summary>
        public int PendingFlushCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < files.Count; i++)
                {
                    if (files[i].IsDirty) count++;
                }
                return count;
            }
        }

        /// <summary>本地设置。</summary>
        public LocalSettings Settings { get { return settingsFile.Data; } }

        /// <summary>账号 / 设备信息。</summary>
        public LocalAccount Account { get { return accountFile.Data; } }

        /// <summary>本地缓存。</summary>
        public LocalCache Cache { get { return cacheFile.Data; } }

        public StorageFile<LocalSettings> SettingsFile { get { return settingsFile; } }
        public StorageFile<LocalAccount> AccountFile { get { return accountFile; } }
        public StorageFile<LocalCache> CacheFile { get { return cacheFile; } }

        /// <summary>设置变化后触发（音量 / 画质 / 帧率…）。界面、音频各自订阅，不互相引用。</summary>
        public event Action SettingsChanged;

        /// <summary>当前 Unix 秒。</summary>
        public static long NowUnixSeconds()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        #region 初始化 / 关闭

        /// <summary>
        /// 解析存档根目录。GameFolder 模式先试游戏目录，建不出来（安卓安装目录只读）就回退到平台标准目录。
        /// </summary>
        private static string ResolveRoot(StorageOptions o)
        {
            if (!string.IsNullOrEmpty(o.RootPathOverride)) return o.RootPathOverride;

            string folder = string.IsNullOrEmpty(o.FolderName) ? "LocalData" : o.FolderName;

            if (o.RootMode == StorageRootMode.GameFolder)
            {
                string gameDir = Path.GetDirectoryName(Application.dataPath);
                if (!string.IsNullOrEmpty(gameDir))
                {
                    string candidate = Path.Combine(gameDir, folder);
                    try
                    {
                        Directory.CreateDirectory(candidate);
                        return candidate;
                    }
                    catch (Exception ex)
                    {
                        if (o.LogOnInit) GameLog.Info(LogTag.Storage, "游戏目录不可写（" + ex.Message + "），回退到平台目录");
                    }
                }
            }

            return Path.Combine(Application.persistentDataPath, folder);
        }
        /// <summary>
        /// 初始化：建目录 → 读三个文件（同步）→ 补设备 ID。
        /// 目录建不出来时会降级成内存模式并回调 false —— 但**调用方不该因此卡住启动**，
        /// 存储只是体验问题，不是硬依赖。
        /// </summary>
        public void Init(StorageOptions initOptions, Action<bool, string> onComplete)
        {
            if (disposed) throw new ObjectDisposedException(GetType().Name);

            if (initialized)
            {
                if (onComplete != null) onComplete(true, "本地存储已经初始化过了");
                return;
            }

            options = initOptions != null ? initOptions : new StorageOptions();
            flushTimer = 0f;
            canPersist = true;

            string root = ResolveRoot(options);


            RootPath = root;

            try
            {
                Directory.CreateDirectory(root);
            }
            catch (Exception ex)
            {
                canPersist = false;
                GameLog.Error(LogTag.Storage, "存档目录建不出来（" + ex.Message + "），本次运行降级为内存模式，关掉游戏数据就没了：" + root);
            }

            settingsFile = new StorageFile<LocalSettings>("settings.json", SettingsVersion);
            accountFile = new StorageFile<LocalAccount>("account.json", AccountVersion);
            cacheFile = new StorageFile<LocalCache>("cache.json", CacheVersion);

            // v1 → v2：LocalSettings 新增 RememberPassword、LocalAccount 新增 PasswordCipher（记住密码）。
            // 老存档没有这两个字段，迁移只补默认值，不需要搬数据。
            settingsFile.AddMigration(1, d => { });
            accountFile.AddMigration(1, d => { });
            // v2 → v3：LocalAccount 新增 LastServerId（选服记忆）。默认 0 即可，无需搬数据。
            accountFile.AddMigration(2, d => { });

            files.Clear();
            files.Add(settingsFile);
            files.Add(accountFile);
            files.Add(cacheFile);

            for (int i = 0; i < files.Count; i++)
                files[i].LoadFrom(Path.Combine(root, files[i].FileName), options);

            EnsureDeviceId();

            initialized = true;

            // 首次启动 / 重建过的文件在这里先落一次盘，别等玩家下次退出才写出来
            Flush();

            string message = Describe();

            if (options.LogOnInit)
                GameLog.Info(LogTag.Storage, "就绪：" + message);

            if (onComplete != null)
                onComplete(canPersist, canPersist ? message : "存档目录不可写，已降级为内存模式：" + root);
        }

        /// <summary>
        /// 注册一个自己创建的存档文件（额外的大块数据、活动进度之类）。
        /// 注册后会立刻读盘，之后它的落盘 / 清档就交给管理器统一处理。
        /// 想给新文件加版本迁移，就在 Register 之前调 file.AddMigration。
        /// </summary>
        public void Register(StorageFileBase file)
        {
            if (!initialized || disposed || file == null) return;

            if (files.Contains(file)) return;

            if (!file.IsLoaded)
                file.LoadFrom(Path.Combine(RootPath, file.FileName), options);

            files.Add(file);

            if (options.AutoFlushDelay <= 0f)
                Flush();
        }

        /// <summary>整体关闭：把没落盘的写下去。幂等。</summary>
        public void Shutdown()
        {
            if (disposed) return;

            if (initialized)
            {
                Flush();

                // 落盘失败时不改 IsDirty，所以这里再尝试一次，避免最后一笔数据丢在内存里
                Flush();
            }

            disposed = true;
            initialized = false;
            SettingsChanged = null;
            files.Clear();

            settingsFile = null;
            accountFile = null;
            cacheFile = null;
            options = null;
        }

        /// <summary>IDisposable 转发到 <see cref="Shutdown"/>，两种写法行为一致。</summary>
        public void Dispose() { Shutdown(); }

        #endregion

        #region 保存

        /// <summary>标记某个文件需要落盘；AutoFlushDelay &lt;= 0 时立刻写。</summary>
        public void Save(StorageFileBase file)
        {
            if (!initialized || disposed || file == null) return;

            file.IsDirty = true;

            if (options.AutoFlushDelay <= 0f)
                Flush();
        }

        /// <summary>
        /// 设置改完了调这个：标记 settings 需要落盘 → 应用到引擎（帧率 / 画质 / 总音量）→ 通知订阅者。
        /// 例：Storage.Settings.VolumeBgm = 0.3f; Storage.NotifySettingsChanged();
        /// </summary>
        public void NotifySettingsChanged()
        {
            if (!initialized || disposed) return;

            settingsFile.IsDirty = true;
            ApplySettings();

            Action handler = SettingsChanged;
            if (handler != null)
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    GameLog.Error(LogTag.Storage, ex.Message, ex);
                }
            }

            if (options.AutoFlushDelay <= 0f)
                Flush();
        }

        /// <summary>把内存里的设置应用到引擎（再叠一层引擎侧的限制时不受影响）。</summary>
        public void ApplySettings()
        {
            if (settingsFile == null) return;

            LocalSettings settings = settingsFile.Data;

            if (settings.TargetFrameRate > 0)
                Application.targetFrameRate = settings.TargetFrameRate;

            if (settings.QualityLevel >= 0)
                QualitySettings.SetQualityLevel(settings.QualityLevel, true);

            // 总音量先由 AudioListener 兜底；以后有了音频系统，BGM / 音效音量交给它读 Settings 处理
            AudioListener.volume = settings.MuteAll ? 0f : Mathf.Clamp01(settings.VolumeMaster);
        }

        /// <summary>把所有没落盘的文件写下去。切后台 / 退出前调它。</summary>
        public void Flush()
        {
            if (!initialized || disposed || !canPersist) return;

            for (int i = 0; i < files.Count; i++)
            {
                StorageFileBase file = files[i];
                if (!file.IsDirty) continue;

                try
                {
                    file.WriteTo(Path.Combine(RootPath, file.FileName), options);
                }
                catch (Exception ex)
                {
                    GameLog.Error(LogTag.Storage, "存档 " + file.FileName + " 写盘失败：" + ex.Message);
                }
            }
        }

        /// <summary>每帧驱动：攒够 AutoFlushDelay 就把改动落盘。由 GameController.Update 调用。</summary>
        public void Tick(float deltaTime, float unscaledDeltaTime)
        {
            if (!initialized || disposed) return;

            if (PendingFlushCount == 0)
            {
                flushTimer = 0f;
                return;
            }

            flushTimer += Time.unscaledDeltaTime;

            if (flushTimer < options.AutoFlushDelay) return;

            flushTimer = 0f;
            Flush();
        }

        /// <summary>切后台 / 失去焦点。移动端系统随时可能杀进程，所以要立刻落盘。由 GameController 转发。</summary>
        public void OnApplicationPause(bool paused)
        {
            if (!initialized || disposed || options == null) return;
            if (!options.FlushOnPause) return;
            if (!paused) return;

            Flush();
        }

        /// <summary>退出。由 GameController 转发。</summary>
        public void OnApplicationQuit()
        {
            if (!initialized || disposed || options == null) return;
            if (!options.FlushOnQuit) return;

            Flush();
        }

        #endregion

        #region 清档

        /// <summary>
        /// 清档：删掉所有存档文件并重建默认值。调试 / 「重置游戏」用。
        /// keepDeviceId = true（默认）保留安装 ID，免得埋点把同一个设备当成新用户。
        /// </summary>
        public void DeleteAll(bool keepDeviceId = true)
        {
            if (!initialized || disposed) return;

            string deviceId = keepDeviceId ? accountFile.Data.DeviceId : string.Empty;
            long firstLaunch = keepDeviceId ? accountFile.Data.FirstLaunchUnixSeconds : 0;

            for (int i = 0; i < files.Count; i++)
            {
                string path = Path.Combine(RootPath, files[i].FileName);

                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex)
                {
                    GameLog.Error(LogTag.Storage, "删除存档失败：" + path + "（" + ex.Message + "）");
                }

                files[i].ResetToDefault();
            }

            accountFile.Data.DeviceId = deviceId;
            accountFile.Data.FirstLaunchUnixSeconds = firstLaunch;

            EnsureDeviceId();
            Flush();

            GameLog.Info(LogTag.Storage, "已清档：" + RootPath);
        }

        #endregion

        #region 零散 KV

        /// <summary>
        /// 结构还没定下来的零散数据先塞这儿，别为一个小开关就动 settings.json 的结构。
        /// 定下来之后提升成正式字段，并加一条迁移把老数据搬过去。
        /// </summary>
        public void SetPref(string key, string value)
        {
            if (!initialized || disposed || string.IsNullOrEmpty(key)) return;

            Dictionary<string, string> prefs = cacheFile.Data.Prefs;

            string current;
            if (prefs.TryGetValue(key, out current) && current == value)
                return;

            prefs[key] = value;
            cacheFile.MarkDirty();

            if (options.AutoFlushDelay <= 0f)
                Flush();
        }

        public string GetPref(string key, string defaultValue = "")
        {
            if (cacheFile == null || string.IsNullOrEmpty(key)) return defaultValue;

            string value;
            return cacheFile.Data.Prefs.TryGetValue(key, out value) ? value : defaultValue;
        }

        public bool RemovePref(string key)
        {
            if (!initialized || disposed || string.IsNullOrEmpty(key)) return false;

            if (!cacheFile.Data.Prefs.Remove(key)) return false;

            cacheFile.MarkDirty();

            if (options.AutoFlushDelay <= 0f)
                Flush();

            return true;
        }

        public void SetPrefInt(string key, int value)
        {
            SetPref(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public int GetPrefInt(string key, int defaultValue = 0)
        {
            int value;
            return int.TryParse(GetPref(key, string.Empty), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value) ? value : defaultValue;
        }

        public void SetPrefBool(string key, bool value)
        {
            SetPref(key, value ? "1" : "0");
        }

        public bool GetPrefBool(string key, bool defaultValue = false)
        {
            string value = GetPref(key, string.Empty);

            if (value == "1") return true;
            if (value == "0") return false;

            return defaultValue;
        }

        #endregion

        #region 内部

        private void EnsureDeviceId()
        {
            LocalAccount account = accountFile.Data;

            if (!string.IsNullOrEmpty(account.DeviceId)) return;

            account.DeviceId = Guid.NewGuid().ToString("N");
            account.FirstLaunchUnixSeconds = NowUnixSeconds();
            accountFile.MarkDirty();
        }

        private string Describe()
        {
            return "3 个文件（设置 / 账号 / 缓存），目录 " + RootPath + "，待落盘 " + PendingFlushCount + " 个";
        }

        public override string ToString()
        {
            return Describe();
        }

        #endregion
    }
}