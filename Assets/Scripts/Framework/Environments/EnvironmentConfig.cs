using System;
using GameFramework.Log;
using GameFramework.Resource;
using GameFramework.Storage;
using UnityEngine;

namespace GameFramework.Environments
{
    /// <summary>环境。数组下标与枚举值一一对应，顺序不要改。</summary>
    public enum GameEnvironment
    {
        /// <summary>开发：本地服务器 + 编辑器模拟资源 + Debug 日志。</summary>
        Development = 0,

        /// <summary>测试：测试服 + 远端热更 + Info 日志。</summary>
        Test = 1,

        /// <summary>正式：正式服 + CDN 热更 + 只留警告以上。</summary>
        Production = 2,
    }

    /// <summary>
    /// 一套环境的具体参数。改这里就够了 —— 服务器地址 / 资源地址 / 日志级别不再散落在场景里。
    /// </summary>
    [Serializable]
    public sealed class EnvironmentEntry
    {
        [Tooltip("显示名（菜单和日志里用）")]
        public string DisplayName = "开发";

        [Header("游戏服务器")]
        public string ServerHost = "127.0.0.1";
        public int ServerPort = 7777;

        [Header("资源（YooAsset）")]
        [Tooltip("运行时资源模式：编辑器迭代用 EditorSimulate；测试 / 正式用 Host")]
        public ResourcePlayMode ResourceMode = ResourcePlayMode.EditorSimulate;

        [Tooltip("资源主站地址。EditorSimulate / Offline 模式下留空")]
        public string ResourceHostUrl = string.Empty;

        [Tooltip("备用资源站，可留空")]
        public string ResourceFallbackUrl = string.Empty;

        [Tooltip("Bundle 完整性校验级别：1=Low 2=Middle 3=High(CRC)。正式环境建议 3")]
        public int FileVerifyLevel = 3;

        [Header("日志")]
        [Tooltip("最低日志级别。开发用 Debug，测试 Info，正式 Warn")]
        public LogLevel LogLevel = LogLevel.Info;

        [Tooltip("把每条服务端推送打进 Console（联调用，正式关掉）")]
        public bool LogPushMessages = false;

        [Header("本地存储")]
        [Tooltip("存档放游戏目录旁边（PC / 编辑器方便翻看）。移动端会自动回退到平台目录")]
        public bool StorageInGameFolder = false;
    }

    /// <summary>
    /// 多环境配置。**唯一数据源** —— 服务器地址 / 资源地址 / 日志级别都以这里为准，
    /// GameController 在 Awake 里读完覆盖掉场景上的默认值。
    ///
    /// 资产放在 <c>Assets/Resources/EnvironmentConfig.asset</c>（进包），运行时用
    /// <see cref="Instance"/> 取。切换环境：
    ///   · 编辑器：菜单 <c>Tools/环境/切换到/...</c>
    ///   · 构建：菜单 <c>Tools/环境/构建/...</c>，或命令行 <c>-executeMethod ...EnvironmentBuilder.BuildFromCommandLine -environment Production</c>
    ///
    /// 构建时把 <see cref="Active"/> 烤进包，所以「构建时切换」是可靠的：
    /// 正式包不会意外连到开发服。
    /// </summary>
    [CreateAssetMenu(fileName = "EnvironmentConfig", menuName = "GameFramework/环境配置")]
    public sealed class EnvironmentConfig : ScriptableObject
    {
        /// <summary>Resources 下的资产名（不带扩展名）。</summary>
        public const string ResourceName = "EnvironmentConfig";

        [Tooltip("当前环境。构建时会烤进包")]
        [SerializeField]
        private GameEnvironment active = GameEnvironment.Development;

        [Tooltip("三套环境参数，下标 = GameEnvironment 枚举值（0 开发 / 1 测试 / 2 正式）")]
        [SerializeField]
        private EnvironmentEntry[] environments = new EnvironmentEntry[3];

        /// <summary>环境数量（固定 3 套）。</summary>
        public const int Count = 3;

        /// <summary>当前环境。</summary>
        public GameEnvironment Active
        {
            get { return active; }
            set { active = value; }
        }

        /// <summary>当前环境的参数（不会返回 null，缺数据时给一套默认值）。</summary>
        public EnvironmentEntry Current { get { return Get(active); } }

        /// <summary>取某个环境的参数。</summary>
        public EnvironmentEntry Get(GameEnvironment env)
        {
            int index = (int)env;
            if (environments == null || index < 0 || index >= environments.Length || environments[index] == null)
            {
                Debug.LogWarning("[Environment] 环境配置缺少 " + env + " 的数据，先用默认值顶着。请在 EnvironmentConfig.asset 里补齐。");
                return new EnvironmentEntry { DisplayName = env.ToString() };
            }
            return environments[index];
        }

        /// <summary>当前环境的中文名（日志用）。</summary>
        public string ActiveName { get { return Current.DisplayName; } }

        // ---------------- 运行时访问 ----------------

        private static EnvironmentConfig instance;
        private static bool loaded;

        /// <summary>
        /// 全局唯一实例（从 Assets/Resources/EnvironmentConfig.asset 加载，只加载一次）。
        /// 资产不存在时返回 null —— GameController 会退回场景里的默认值并打警告。
        /// </summary>
        public static EnvironmentConfig Instance
        {
            get
            {
                if (!loaded)
                {
                    loaded = true;
                    instance = Resources.Load<EnvironmentConfig>(ResourceName);

                    if (instance == null)
                        Debug.LogWarning("[Environment] 找不到 Assets/Resources/" + ResourceName +
                                         ".asset，本次运行用场景里的默认配置。菜单 Tools/环境/创建配置资产 可以创建。");
                }
                return instance;
            }
        }

        /// <summary>当前环境的参数；没有配置资产时返回 null。</summary>
        public static EnvironmentEntry CurrentEntry
        {
            get
            {
                EnvironmentConfig cfg = Instance;
                return cfg != null ? cfg.Current : null;
            }
        }

        /// <summary>编辑器里改了资产之后清缓存（构建脚本 / 切换环境时用）。</summary>
        public static void ResetCache()
        {
            instance = null;
            loaded = false;
        }
    }
}
