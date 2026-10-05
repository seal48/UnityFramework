using GameFramework.Audio;
using GameFramework.Event;
using GameFramework.Localization;
using GameFramework.Log;
using GameFramework.Platform;
using GameFramework.Pool;
using GameFramework.Resource;
using GameFramework.Scenes;
using GameFramework.Storage;
using GameFramework.Timer;
using GameFramework.UI;
using UnityEngine;

namespace GameFramework.Boot
{
    /// <summary>
    /// 游戏启动配置（AOT ScriptableObject）。
    ///
    /// 为什么存在：GameController 是**热更程序集**（业务层要能热更），但热更代码不能挂场景组件、
    /// 也不能被场景序列化 —— 而这些启动选项（UI 根节点、音频目录、场景目录、平台行为等）
    /// 又必须有人拿着。所以抽成这个 AOT 配置资产，热更的 GameController 在 Awake 时
    /// 从 Resources 加载它。
    ///
    /// 资产位置：Assets/Resources/GameOptions.asset（用菜单 GameFramework/创建启动配置 生成）。
    /// 环境相关的（服务器/资源地址/日志级别/存档目录）仍由 EnvironmentConfig 管，这里的会被覆盖。
    /// </summary>
    [CreateAssetMenu(fileName = "GameOptions", menuName = "GameFramework/创建启动配置")]
    public class GameOptions : ScriptableObject
    {
        public const string ResourceName = "GameOptions";

        [Header("日志")] public LogOptions logOptions = new LogOptions();

        [Header("资源")] public ResourceInitOptions resourceOptions = new ResourceInitOptions();

        [Header("UI")] public UIInitOptions uiOptions = new UIInitOptions();

        [Header("事件")] public EventBusOptions eventOptions = new EventBusOptions();

        [Header("对象池")] public PoolInitOptions poolOptions = new PoolInitOptions();

        [Header("本地存储")] public StorageOptions storageOptions = new StorageOptions
        {
            RootMode = StorageRootMode.GameFolder,   // 开发期放工程目录，装手机自动回退平台目录
        };

        [Header("计时器")] public TimerInitOptions timerOptions = new TimerInitOptions();

        [Header("音频")] public AudioInitOptions audioOptions = new AudioInitOptions();

        [Header("场景")] public SceneInitOptions sceneOptions = new SceneInitOptions();

        [Header("平台适配")] public PlatformOptions platformOptions = new PlatformOptions();

        [Header("本地化")] public LocalizationOptions localizationOptions = new LocalizationOptions();

        [Header("业务")] public bool logPushMessages = true;

        /// <summary>运行时加载；资产不存在时给一套默认值（并打警告）。</summary>
        public static GameOptions Instance
        {
            get
            {
                var so = Resources.Load<GameOptions>(ResourceName);
                if (so == null)
                {
                    Debug.LogWarning("[GameOptions] 找不到 Assets/Resources/" + ResourceName +
                                     ".asset，用代码默认值启动。菜单 GameFramework/创建启动配置 可生成。");
                    return CreateInstance<GameOptions>();
                }
                return so;
            }
        }
    }
}
