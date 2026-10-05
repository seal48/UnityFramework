using System;
using GameFramework.Audio;
using GameFramework.Business;
using GameFramework.Config;
using GameFramework.Core;
using GameFramework.Environments;
using GameFramework.Event;
using GameFramework.Log;
using GameFramework.Localization;
using GameFramework.Net.Client.Unity;
using GameFramework.Platform;
using GameFramework.Pool;
using GameFramework.Procedure;
using GameFramework.Resource;
using GameFramework.Scenes;
using GameFramework.ServerSelect;
using GameFramework.Startup;
using GameFramework.Storage;
using GameFramework.Timer;
using GameFramework.UI;
using UnityEngine;

/// <summary>
/// 全局入口：所有框架 / 管理器都在这里统一初始化。
/// 约定：Awake 只负责「创建实例 + 登记流程」，启动顺序全部写在 Assets/Scripts/Game/Startup 下的各个 Procedure 里，
/// 由 ProcedureManager 依次驱动，而不是散落在各种回调里。
/// </summary>
public class GameController : MonoBehaviour
{
    private static GameController instance;

    [Header("日志")]
    [SerializeField]
    private LogOptions logOptions = new LogOptions();

    [Header("网络框架")]
    public GameClientBehaviour gameClient;

    [Header("资源框架")]
    [SerializeField]
    private ResourceInitOptions resourceOptions = new ResourceInitOptions();

    [Header("UI框架")]
    [SerializeField]
    private UIInitOptions uiOptions = new UIInitOptions();

    [Header("事件框架")]
    [SerializeField]
    private EventBusOptions eventOptions = new EventBusOptions();

    [Header("对象池")]
    [SerializeField]
    private PoolInitOptions poolOptions = new PoolInitOptions();

    [Header("本地存储")]
    [SerializeField]
    private StorageOptions storageOptions = new StorageOptions
    {
        // 调试期存档放游戏目录（工程根/LocalData），方便直接翻看；装到手机上会自动回退到平台目录
        RootMode = StorageRootMode.GameFolder,
    };

    [Header("计时器")]
    [SerializeField]
    private TimerInitOptions timerOptions = new TimerInitOptions();

    [Header("音频")]
    [SerializeField]
    private AudioInitOptions audioOptions = new AudioInitOptions();

    [Header("场景")]
    [SerializeField]
    private SceneInitOptions sceneOptions = new SceneInitOptions();

    [Header("平台适配（Android）")]
    [SerializeField]
    private PlatformOptions platformOptions = new PlatformOptions();

    [Header("本地化")]
    [SerializeField]
    private LocalizationOptions localizationOptions = new LocalizationOptions();

    [Header("业务层")]
    [Tooltip("把每条收到的游戏推送打进 Console（联调时打开，正式版关掉）")]
    [SerializeField]
    private bool logPushMessages = true;

    private EventBus events;
    private IResourceService resource;

    /// <summary>当前环境的参数（来自 EnvironmentConfig）；没有配置资产时为 null，用场景里的默认值。</summary>
    private EnvironmentEntry environment;
    private UIManager ui;
    private SpriteAtlasManager spriteAtlases;
    private ProcedureManager procedures;
    private BusinessManager business;
    private ConfigManager config;
    private ObjectPoolManager pool;
    private LocalStorageManager storage;
    private TimerManager timer;
    private AudioManager audioMgr;
    private SceneLoader scene;
    private PlatformManager platform;
    private LocalizationManager localization;
    private ServerSelectManager serverSelect;

    public static GameController Instance
    {
        get { return instance; }
    }

    /// <summary>
    /// 创建游戏根节点（HybridCLR 热更入口调用）。
    /// 热更模式下场景里没有 GameController 组件（场景脚本必须是 AOT），
    /// 由 AOT 的 HybridCLRBoot 加载热更程序集后反射调用本方法，运行时挂组件。
    /// </summary>
    public static GameController CreateRoot()
    {
        if (instance != null)
            return instance;

        var go = new GameObject("GameController");
        UnityEngine.Object.DontDestroyOnLoad(go);
        return go.AddComponent<GameController>();
    }

    /// <summary>资源服务。业务代码只依赖这个接口，不直接碰 YooAsset。</summary>
    public IResourceService Resource
    {
        get { return resource; }
    }

    /// <summary>UI 服务。业务代码通过 GameController.Instance.UI 打开 / 关闭界面。</summary>
    public UIManager UI
    {
        get { return ui; }
    }

    /// <summary>UI 图集服务。按名字取图：GameController.Instance.SpriteAtlases.GetSprite("Icon", "sword")。</summary>
    public SpriteAtlasManager SpriteAtlases
    {
        get { return spriteAtlases; }
    }

    /// <summary>流程管理器（启动流程 / 状态机）。</summary>
    public ProcedureManager Procedures
    {
        get { return procedures; }
    }

    /// <summary>事件总线。模块之间用它收发事件，不互相引用。</summary>
    public IEventBus Events
    {
        get { return events; }
    }

    /// <summary>业务层：服务端推送 → 各系统处理 → 逻辑事件 → 界面。</summary>
    public BusinessManager Business
    {
        get { return business; }
    }

    /// <summary>配置表：GameController.Instance.Config.Database.Item.Get(id)。</summary>
    public ConfigManager Config
    {
        get { return config; }
    }

    /// <summary>对象池：GameController.Instance.Pool.Spawn(prefab) / Despawn(go)。</summary>
    public ObjectPoolManager Pool
    {
        get { return pool; }
    }

    /// <summary>本地存储：GameController.Instance.Storage.Settings / Account / Cache。</summary>
    public LocalStorageManager Storage
    {
        get { return storage; }
    }

    /// <summary>计时器 / 调度器：GameController.Instance.Timer.Delay(1f, () => ...)。</summary>
    public TimerManager Timer
    {
        get { return timer; }
    }

    /// <summary>音频：GameController.Instance.Audio.PlayBgm(...) / PlaySfx(...)。</summary>
    public AudioManager Audio
    {
        get { return audioMgr; }
    }

    /// <summary>场景管理：GameController.Instance.Scene.LoadAsync("Lobby")。</summary>
    public SceneLoader Scene
    {
        get { return scene; }
    }

    /// <summary>平台适配：生命周期 / 返回键 / 安全区 / 权限 / 断线重连。</summary>
    public PlatformManager Platform
    {
        get { return platform; }
    }

    /// <summary>本地化：语言 / 文本 / 图片地址 / 字体。</summary>
    public LocalizationManager Localization
    {
        get { return localization; }
    }

    /// <summary>区服列表 / 选服：GameController.Instance.ServerSelect.Current / Select(id)。</summary>
    public ServerSelectManager ServerSelect
    {
        get { return serverSelect; }
    }

    private void Awake()
    {
        // 常驻单例：已经有一个 GameController 活着时（比如误加载了带 GameController 的场景）销毁自己，
        // 否则会出现两个管理器同时跑，流程、连接、UI 根都会被切坏
        if (instance != null && instance != this)
        {
            GameLog.Warn(LogTag.Startup, "检测到重复的 GameController，已销毁多余的实例：" + gameObject.scene.name);
            Destroy(gameObject);
            return;
        }

        instance = this;

        // 启动配置：GameController 是热更程序集，场景里不能序列化它的字段，
        // 所以这些启动选项统一放在 AOT 的 GameOptions 资产里（场景值已迁移过去）
        LoadOptionsFromGameOptions();

        // 多环境配置最先应用：服务器地址 / 资源地址 / 日志级别以 EnvironmentConfig 为准，
        // 覆盖场景里序列化的默认值（必须在 InitLog 之前，日志级别要先生效）
        ApplyEnvironment();

        // 日志最先起来：后面所有初始化过程都要用它
        InitLog();

        // 切场景不销毁：网络连接、UI 根节点、流程状态都要跨场景活着
        DontDestroyOnLoad(gameObject);

        // 只创建实例，不做初始化
        gameClient = GetComponent<GameClientBehaviour>();
        if (gameClient == null)
            gameClient = gameObject.AddComponent<GameClientBehaviour>();

        // 服务器地址也来自环境配置（覆盖场景里 GameClientBehaviour 上填的值）
        if (environment != null)
            gameClient.SetServer(environment.ServerHost, environment.ServerPort);

        // 事件总线最底层，先建出来，后面初始化的东西都能用
        events = new EventBus();
        events.Options = eventOptions;
        EventBus.Global = events;

        // 计时器：纯逻辑、没有外部依赖，尽早起来，后面的流程和界面都能用
        timer = new TimerManager();
        timer.Init(timerOptions);

        // 业务层：把网络推送按系统拆开处理，处理完再发逻辑事件给界面。
        // 必须建在事件总线之后（系统就是往总线上发事件的）
        business = new BusinessManager(gameClient, events, new UnityNetLogger("HUB"));
        business.Hub.LogPush = logPushMessages;

        config = new ConfigManager();
        resource = new YooAssetResourceService();
        pool = new ObjectPoolManager();
        storage = new LocalStorageManager();
        ui = new UIManager();
        spriteAtlases = new SpriteAtlasManager();
        audioMgr = new AudioManager();
        scene = new SceneLoader();
        platform = new PlatformManager(platformOptions, events, ui, timer, gameClient);
        localization = new LocalizationManager(localizationOptions, config, resource, events, storage);
        serverSelect = new ServerSelectManager(config, storage, events);

        // 启动流程：顺序就是依赖顺序，每一步做完自己切下一步
        procedures = new ProcedureManager(
            new ProcedureLaunch(),
            new ProcedureInitStorage(),
            new ProcedureInitResource(),
            new ProcedureInitConfig(),
            new ProcedureInitLocalization(),
            new ProcedureInitPool(),
            new ProcedureInitUI(),
            new ProcedureInitScene(),
            new ProcedureInitAudio(),
            new ProcedureInitPlatform(),
            new ProcedureLogin(),
            new ProcedureEnterLobby(),
            new ProcedureLobby());
    }

    /// <summary>
    /// 从 AOT 的 <see cref="GameFramework.Boot.GameOptions"/> 加载启动配置。
    /// GameController 在热更程序集里，场景序列化不了它的字段，所以选项统一放这个资产。
    /// 资产不存在时字段保持代码默认值（GameOptions.Instance 内部会打警告）。
    /// </summary>
    private void LoadOptionsFromGameOptions()
    {
        GameFramework.Boot.GameOptions o = GameFramework.Boot.GameOptions.Instance;
        if (o == null)
            return;

        logOptions = o.logOptions;
        resourceOptions = o.resourceOptions;
        uiOptions = o.uiOptions;
        eventOptions = o.eventOptions;
        poolOptions = o.poolOptions;
        storageOptions = o.storageOptions;
        timerOptions = o.timerOptions;
        audioOptions = o.audioOptions;
        sceneOptions = o.sceneOptions;
        platformOptions = o.platformOptions;
        localizationOptions = o.localizationOptions;
        logPushMessages = o.logPushMessages;
    }

    /// <summary>
    /// 应用多环境配置：服务器地址 / 资源地址与模式 / 日志级别 / 存档目录。
    /// <see cref="EnvironmentConfig"/> 是**唯一数据源**，场景里那些序列化字段只作为「没有配置资产时的兜底」。
    /// 改环境请用菜单 Tools/环境/切换到/...（或构建时的环境参数），不要改场景。
    /// </summary>
    private void ApplyEnvironment()
    {
        EnvironmentConfig cfg = EnvironmentConfig.Instance;
        if (cfg == null)
            return;   // 没有配置资产：保持场景里的默认值，EnvironmentConfig 内部已经打过警告

        environment = cfg.Current;

        // 日志：环境配置说了算（开发=Debug、测试=Info、正式=Warn）。
        // MinLevelInDevelopment 也一起设成同一个值 —— 否则「编辑器里更啰嗦」和「环境级别」两套规则会打架。
        logOptions.MinLevel = environment.LogLevel;
        logOptions.MinLevelInDevelopment = environment.LogLevel;
        logPushMessages = environment.LogPushMessages;

        // 资源：模式 / 主站 / 备用站 / 校验级别
        resourceOptions.PlayMode = environment.ResourceMode;
        resourceOptions.HostServerURL = environment.ResourceHostUrl;
        resourceOptions.FallbackHostServerURL = environment.ResourceFallbackUrl;
        resourceOptions.FileVerifyLevel = environment.FileVerifyLevel;

        // 本地存储：开发期放工程目录方便翻看，测试 / 正式走平台标准目录
        storageOptions.RootMode = environment.StorageInGameFolder
            ? StorageRootMode.GameFolder
            : StorageRootMode.PersistentData;
    }

    /// <summary>日志系统初始化。编辑器下把日志放到工程根目录，方便直接翻看。</summary>
    private void InitLog()
    {
#if UNITY_EDITOR
        logOptions.RootPathOverride = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Application.dataPath), "GameLogs");
#endif
        GameLog.Init(logOptions);
        GameLog.Info(LogTag.Game, "日志系统就绪：最低级别=" + GameLog.MinLevel + "，目录=" + GameLog.FileRoot);

        if (environment != null)
        {
            GameLog.Info(LogTag.Game, "运行环境：" + environment.DisplayName +
                "（服务器 " + environment.ServerHost + ":" + environment.ServerPort +
                "，资源 " + environment.ResourceMode + "）");
        }
    }

    private void Start()
    {
        procedures.Start<ProcedureLaunch>();
    }

    private void Update()
    {
        // 把文件缓冲按间隔落盘
        GameLog.Tick(Time.deltaTime, Time.unscaledDeltaTime);

        // 驱动计时器 / 调度器
        if (timer != null)
            timer.Tick(Time.deltaTime, Time.unscaledDeltaTime);

        if (procedures != null)
            procedures.Tick(Time.deltaTime, Time.unscaledDeltaTime);

        // 驱动各个面板的 OnUpdate
        if (ui != null)
            ui.Tick(Time.deltaTime, Time.unscaledDeltaTime);

        // 平台适配：返回键 / 安全区 / 回前台断线处理
        if (platform != null)
            platform.Tick(Time.deltaTime, Time.unscaledDeltaTime);

        // 处理延迟归还 + 空闲回收
        if (pool != null)
            pool.Tick(Time.deltaTime, Time.unscaledDeltaTime);

        // 驱动场景加载（进度条 / 排队 / 收尾）
        if (scene != null)
            scene.Tick(Time.deltaTime, Time.unscaledDeltaTime);

        // 驱动 BGM 淡入淡出 + 回收播完的音效通道
        if (audioMgr != null)
            audioMgr.Tick(Time.deltaTime, Time.unscaledDeltaTime);

        // 把攒着的本地数据落盘
        if (storage != null)
            storage.Tick(Time.deltaTime, Time.unscaledDeltaTime);

        // 派发 Post 入队的事件
        if (events != null)
            events.Tick(Time.deltaTime, Time.unscaledDeltaTime);
    }

    private void OnDestroy()
    {
        // 只有当前常驻实例才允许拆框架：重复实例（Awake 里被销毁的那份）如果也拆，
        // 会把真正在跑的那份日志 / UI / 资源全关掉
        if (instance != this)
            return;

        instance = null;

        if (business != null)
        {
            business.Shutdown();
            business = null;
        }

        if (pool != null)
        {
            pool.Shutdown();
            pool = null;
        }

        if (config != null)
        {
            config.Shutdown();
            config = null;
        }

        if (serverSelect != null)
        {
            serverSelect.Shutdown();
            serverSelect = null;
        }

        if (ui != null)
        {
            ui.Shutdown();
            ui = null;
        }

        if (spriteAtlases != null)
        {
            spriteAtlases.Shutdown();
            spriteAtlases = null;
        }

        if (audioMgr != null)
        {
            audioMgr.VolumeChanged -= OnAudioVolumeChanged;
            audioMgr.Shutdown();
            audioMgr = null;
        }

        if (scene != null)
        {
            scene.Shutdown();
            scene = null;
        }

        if (platform != null)
        {
            platform.Shutdown();
            platform = null;
        }

        if (localization != null)
        {
            localization.Shutdown();
            localization = null;
        }

        if (timer != null)
        {
            timer.Shutdown();
            timer = null;
        }

        if (resource != null)
        {
            resource.Shutdown();
            resource = null;
        }

        if (events != null)
        {
            events.Shutdown();

            if (EventBus.Global == events)
                EventBus.Global = null;

            events = null;
        }

        // 存储最后关：别的模块在 Shutdown 过程中可能还要往里面写点东西
        if (storage != null)
        {
            storage.Shutdown();
            storage = null;
        }

        // 日志最后关：别的模块在关闭过程中可能还要写几行
        GameLog.Shutdown();
    }

    /// <summary>切后台 / 失去焦点：立刻落盘 + 通知平台层。移动端系统随时可能杀进程。</summary>
    private void OnApplicationPause(bool paused)
    {
        if (storage != null)
            storage.OnApplicationPause(paused);

        if (platform != null)
            platform.OnApplicationPause(paused);
    }

    /// <summary>获得 / 失去焦点：通知平台层（权限弹窗等系统对话框也会触发）。</summary>
    private void OnApplicationFocus(bool focused)
    {
        if (platform != null)
            platform.OnApplicationFocus(focused);
    }

    /// <summary>初始化资源（异步）。由 ProcedureInitResource 调用，完成后回调。</summary>
    public void InitResource(Action<bool, string> onComplete)
    {
        if (resource == null)
        {
            if (onComplete != null) onComplete(false, "资源服务还没创建");
            return;
        }

        if (resource.IsInitialized)
        {
            if (onComplete != null) onComplete(true, "资源系统已初始化");
            return;
        }

        resource.DownloadProgressChanged += OnResourceDownloadProgress;
        resource.Init(resourceOptions, onComplete);
    }

    /// <summary>初始化 UI（异步，依赖资源系统）。由 ProcedureInitUI 调用。</summary>
    public void InitUI(Action<bool, string> onComplete)
    {
        if (ui == null)
        {
            if (onComplete != null) onComplete(false, "UI 管理器还没创建");
            return;
        }

        if (ui.IsInitialized)
        {
            if (onComplete != null) onComplete(true, "UI 已初始化");
            return;
        }

        // 图集管理器跟着 UI 一起初始化（依赖资源服务）
        if (spriteAtlases != null && !spriteAtlases.IsInitialized)
            spriteAtlases.Init(resource);

        ui.Init(resource, uiOptions, events, timer, onComplete);
    }

    /// <summary>初始化对象池（异步，预热要加载预制体）。由 ProcedureInitPool 调用。</summary>
    public void InitPool(Action<bool, string> onComplete)
    {
        if (pool == null)
        {
            if (onComplete != null) onComplete(false, "对象池管理器还没创建");
            return;
        }

        if (pool.IsInitialized)
        {
            if (onComplete != null) onComplete(true, "对象池已初始化");
            return;
        }

        pool.Init(resource, poolOptions, onComplete);
    }

    /// <summary>初始化本地存储（同步读盘）。由 ProcedureInitStorage 调用。</summary>
    public void InitStorage(Action<bool, string> onComplete)
    {
        if (storage == null)
        {
            if (onComplete != null) onComplete(false, "本地存储管理器还没创建");
            return;
        }

        if (storage.IsInitialized)
        {
            if (onComplete != null) onComplete(true, "本地存储已初始化");
            return;
        }

        storage.Init(storageOptions, onComplete);
    }

    /// <summary>初始化场景管理（依赖资源系统 + UI）。由 ProcedureInitScene 调用。</summary>
    public void InitScene(Action<bool, string> onComplete)
    {
        if (scene == null)
        {
            if (onComplete != null) onComplete(false, "场景管理器还没创建");
            return;
        }

        if (scene.IsInitialized)
        {
            if (onComplete != null) onComplete(true, "场景管理已初始化");
            return;
        }

        scene.Init(resource, sceneOptions, ui, onComplete);
    }

    /// <summary>初始化音频（依赖资源系统）。由 ProcedureInitAudio 调用。</summary>
    public void InitAudio(Action<bool, string> onComplete)
    {
        if (audioMgr == null)
        {
            if (onComplete != null) onComplete(false, "音频管理器还没创建");
            return;
        }

        if (audioMgr.IsInitialized)
        {
            if (onComplete != null) onComplete(true, "音频已初始化");
            return;
        }

        audioMgr.VolumeChanged += OnAudioVolumeChanged;

        audioMgr.Init(resource, audioOptions, (success, message) =>
        {
            if (success)
                ApplyAudioVolumeFromStorage();

            if (onComplete != null) onComplete(success, message);
        });
    }

    /// <summary>初始化平台适配层（依赖 UI / 存储 / 网络）。由 ProcedureInitPlatform 调用。</summary>
    public void InitPlatform(Action<bool, string> onComplete)
    {
        if (platform == null)
        {
            if (onComplete != null) onComplete(false, "平台管理器还没创建");
            return;
        }

        if (platform.IsInitialized)
        {
            if (onComplete != null) onComplete(true, "平台适配已初始化");
            return;
        }

        // 重连凭据默认读存档里的上次账号 + 记住的密码（LocalSecret 混淆存储，见 StorageFramework README）；
        // 业务也可以在登录成功后覆盖 CredentialProvider 换别的来源
        if (platformOptions.CredentialProvider == null)
        {
            platformOptions.CredentialProvider = () => new AccountCredentials
            {
                Account = storage != null && storage.IsInitialized ? storage.Account.LastAccount : null,
                Password = storage != null && storage.IsInitialized ? storage.Account.Password : null,
            };
        }

        platform.Init();
        if (onComplete != null) onComplete(true, "平台适配已初始化");
    }

    /// <summary>把存档里的音量套到音频系统上（启动时 / 存档被改过之后）。</summary>
    private void ApplyAudioVolumeFromStorage()
    {
        if (audioMgr == null || storage == null || !storage.IsInitialized)
            return;

        LocalSettings settings = storage.Settings;
        audioMgr.ApplyVolumes(settings.VolumeMaster, settings.VolumeBgm, settings.VolumeSfx, settings.MuteAll);
    }

    /// <summary>业务改了音量：写回存档（沿用存储框架自己的落盘节奏）。</summary>
    private void OnAudioVolumeChanged()
    {
        if (audioMgr == null || storage == null || !storage.IsInitialized)
            return;

        LocalSettings settings = storage.Settings;
        settings.VolumeMaster = audioMgr.MasterVolume;
        settings.VolumeBgm = audioMgr.BgmVolume;
        settings.VolumeSfx = audioMgr.SfxVolume;
        settings.MuteAll = audioMgr.MuteAll;
        storage.NotifySettingsChanged();
    }

    /// <summary>初始化配置表（异步，依赖资源系统）。由 ProcedureInitConfig 调用。</summary>
    public void InitConfig(Action<bool, string> onComplete)
    {
        if (config == null)
        {
            if (onComplete != null) onComplete(false, "配置表管理器还没创建");
            return;
        }

        config.Init(resource, (ok, message) =>
        {
            // 配置表就绪后初始化区服选择（读区服表 + 恢复上次选的服）。
            // 即使失败也不阻断流程，选服会退化成"无区服表"状态。
            if (ok && serverSelect != null)
                serverSelect.Init();

            if (onComplete != null) onComplete(ok, message);
        });
    }

    /// <summary>初始化本地化（依赖配置表 / 资源 / 存储）。由 ProcedureInitLocalization 调用。</summary>
    public void InitLocalization(Action<bool, string> onComplete)
    {
        if (localization == null)
        {
            if (onComplete != null) onComplete(false, "本地化管理器还没创建");
            return;
        }

        localization.Init();
        if (onComplete != null) onComplete(true, "本地化已初始化");
    }

    private void OnResourceDownloadProgress(ResourceDownloadReport report)
    {
        // 热更进度：以后接进度条界面
        // Debug.LogFormat("[GameController] 资源下载 {0}/{1}（{2:P0}）", report.CurrentCount, report.TotalCount, report.Progress);
    }
}
