using System;
using System.Collections.Generic;
using GameFramework.Core;
using GameFramework.Event;
using GameFramework.Log;
using GameFramework.Net.Client.Unity;
using GameFramework.Timer;
using GameFramework.UI;
using UnityEngine;

namespace GameFramework.Platform
{
    /// <summary>
    /// Android 平台适配层：App 生命周期（pause / focus）、系统返回键、安全区 / 刘海屏、
    /// 运行时权限申请、回前台断线自动重连。
    ///
    /// 由 GameController 创建并转发 Unity 生命周期（OnApplicationPause / OnApplicationFocus），
    /// 业务代码通过 GameController.Instance.Platform 使用。
    /// 公共方法只允许在主线程调用；重连的登录在后台线程跑，结果会回到主线程。
    /// </summary>
    public sealed class PlatformManager : IGameModule, ITickable
    {
        private readonly PlatformOptions _options;
        private readonly IEventBus _events;
        private readonly UIManager _ui;
        private readonly TimerManager _timers;
        private readonly GameClientBehaviour _gameClient;

        private readonly List<string> _openPanels = new List<string>();
        private readonly List<Func<bool>> _backHandlers = new List<Func<bool>>();

        private Rect _safeArea;
        private bool _initialized;
        private bool _isPaused;
        private bool _hasFocus = true;
        private float _pauseStartedAt;
        private bool _wasLoggedInBeforePause;
        private bool _pendingResumeCheck;
        private bool _reconnecting;
        private int _reconnectAttempt;
        private AccountCredentials _pendingCredentials;

        public PlatformManager(PlatformOptions options, IEventBus events, UIManager ui,
            TimerManager timers, GameClientBehaviour gameClient)
        {
            _options = options ?? new PlatformOptions();
            _events = events;
            _ui = ui;
            _timers = timers;
            _gameClient = gameClient;
        }

        public PlatformOptions Options { get { return _options; } }

        public bool IsInitialized { get { return _initialized; } }

        /// <summary>是否处于后台（OnApplicationPause(true) 之后、false 之前）。</summary>
        public bool IsPaused { get { return _isPaused; } }

        /// <summary>是否持有焦点。Android 上权限弹窗等系统对话框也会让焦点短暂丢失。</summary>
        public bool HasFocus { get { return _hasFocus; } }

        /// <summary>当前安全区（像素）。未开启适配时是 default(Rect)。</summary>
        public Rect SafeArea { get { return _safeArea; } }

        /// <summary>断线重连状态。</summary>
        public PlatformReconnectState ReconnectState { get; private set; }

        /// <summary>每次按下返回键都触发（即使默认逻辑已处理），可以拿来做音效 / 统计。</summary>
        public event Action BackPressed;

        /// <summary>返回键落到「根界面」（没有面板可关、也没有拦截器处理）时触发。</summary>
        public event Action BackOnRoot;

        /// <summary>
        /// 初始化：订阅 UI 开关事件、应用安全区。UI 就绪后（ProcedureInitPlatform）调用。
        /// </summary>
        public void Init()
        {
            if (_initialized)
                return;

            if (_ui != null)
            {
                _ui.PanelOpened += OnPanelOpened;
                _ui.PanelClosed += OnPanelClosed;
            }

            if (_options.ApplySafeArea)
                ApplySafeArea();

            _initialized = true;
        }

        /// <summary>每帧驱动，由 GameController.Update 调用。</summary>
        public void Tick(float deltaTime, float unscaledDeltaTime)
        {
            TickBackKey();
            TickSafeArea();
            TickPendingResume();
        }

        /// <summary>App 生命周期：切后台 / 回前台，由 GameController.OnApplicationPause 转发。</summary>
        public void OnApplicationPause(bool paused)
        {
            if (_isPaused == paused)
                return;

            _isPaused = paused;

            if (paused)
            {
                // 切后台：记住登录状态、停掉正在跑的重连计时器；存档落盘由 GameController 负责
                _pauseStartedAt = Time.unscaledTime;
                _wasLoggedInBeforePause = _gameClient != null && _gameClient.IsLoggedIn;
                if (_timers != null)
                    _timers.CancelOwner(this);
            }
            else
            {
                // 回前台：下一帧再处理，避免在生命周期回调里做网络操作
                _pendingResumeCheck = true;
            }

            Publish(new PlatformApplicationPausedEvent { Paused = paused });
        }

        /// <summary>App 焦点变化，由 GameController.OnApplicationFocus 转发。</summary>
        public void OnApplicationFocus(bool focused)
        {
            if (_hasFocus == focused)
                return;

            _hasFocus = focused;
            Publish(new PlatformApplicationFocusedEvent { Focused = focused });
        }

        /// <summary>关闭：退订 UI 事件、停掉重连计时器。</summary>
        public void Shutdown()
        {
            if (_ui != null)
            {
                _ui.PanelOpened -= OnPanelOpened;
                _ui.PanelClosed -= OnPanelClosed;
            }

            if (_timers != null)
                _timers.CancelOwner(this);

            _initialized = false;
        }

        #region 返回键

        /// <summary>
        /// 注册返回键拦截器。按注册顺序询问（后注册的先问），返回 true 表示已消费、
        /// 不再走默认逻辑（关顶层面板 / 退后台）。
        /// </summary>
        public void AddBackHandler(Func<bool> handler)
        {
            if (handler != null)
                _backHandlers.Add(handler);
        }

        public void RemoveBackHandler(Func<bool> handler)
        {
            _backHandlers.Remove(handler);
        }

        private void TickBackKey()
        {
            if (!_options.TrackBackKey)
                return;

#if UNITY_ANDROID || UNITY_EDITOR
            if (Input.GetKeyDown(KeyCode.Escape))
                HandleBack();
#endif
        }

        private void HandleBack()
        {
            BackPressed?.Invoke();
            Publish(new PlatformBackPressedEvent());

            // 先问拦截器
            for (int i = _backHandlers.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (_backHandlers[i]())
                        return;
                }
                catch (Exception ex)
                {
                    GameLog.Error(LogTag.Platform, "返回键拦截器异常：" + ex);
                }
            }

            // 默认：关掉最上层的 Popup / Top 面板
            if (TryCloseTopOverlay())
                return;

            // 根界面
            BackOnRoot?.Invoke();
            if (_options.BackOnRoot == BackRootBehavior.MinimizeApp)
                MinimizeApp();
        }

        /// <summary>关掉最上层还显示着的 Popup / Top 面板。有可关的返回 true。</summary>
        private bool TryCloseTopOverlay()
        {
            if (_ui == null)
                return false;

            for (int i = _openPanels.Count - 1; i >= 0; i--)
            {
                string name = _openPanels[i];
                Panel panel = _ui.Get(name);
                if (panel == null || !panel.IsShown)
                    continue;

                if (panel.Layer == UILayer.Popup || panel.Layer == UILayer.Top)
                {
                    _ui.Close(name);
                    return true;
                }
            }

            return false;
        }

        private void MinimizeApp()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    if (activity != null)
                        activity.Call<bool>("moveTaskToBack", true);
                }
            }
            catch (Exception ex)
            {
                GameLog.Warn(LogTag.Platform, "退回桌面失败：" + ex.Message);
            }
#endif
        }

        private void OnPanelOpened(string name)
        {
            if (!_openPanels.Contains(name))
                _openPanels.Add(name);
        }

        private void OnPanelClosed(string name)
        {
            _openPanels.Remove(name);
        }

        #endregion

        #region 安全区 / 刘海屏

        private void TickSafeArea()
        {
            if (!_options.ApplySafeArea)
                return;

            Rect current = Screen.safeArea;
            if (current == _safeArea)
                return;

            _safeArea = current;
            ApplySafeArea();
            Publish(new PlatformSafeAreaChangedEvent { SafeArea = current });
        }

        /// <summary>把 UI 根 Canvas 收缩到系统安全区内（处理刘海屏 / 圆角屏）。可手动重调。</summary>
        public void ApplySafeArea()
        {
            if (_ui == null)
                return;

            Canvas canvas = _ui.Canvas;
            if (canvas == null)
                return;

            RectTransform root = canvas.transform as RectTransform;
            if (root == null)
                return;

            Rect safe = Screen.safeArea;
            float w = Screen.width;
            float h = Screen.height;
            if (w <= 0 || h <= 0)
                return;

            root.anchorMin = new Vector2(safe.xMin / w, safe.yMin / h);
            root.anchorMax = new Vector2(safe.xMax / w, safe.yMax / h);
            root.offsetMin = Vector2.zero;
            root.offsetMax = Vector2.zero;
            root.anchoredPosition = Vector2.zero;
            root.pivot = new Vector2(0.5f, 0.5f);
        }

        #endregion

        #region 断线重连

        private void TickPendingResume()
        {
            if (!_pendingResumeCheck)
                return;

            _pendingResumeCheck = false;
            HandleResume();
        }

        private void HandleResume()
        {
            float pausedFor = Time.unscaledTime - _pauseStartedAt;
            if (pausedFor < _options.BackgroundThresholdSeconds)
            {
                GameLog.InfoFormat(LogTag.Platform, "回到前台：暂停 {0:F1}s，判定为系统对话框，不做断线处理。", pausedFor);
                return;
            }

            GameLog.InfoFormat(LogTag.Platform, "回到前台：暂停 {0:F1}s。", pausedFor);

            if (_gameClient == null || !_options.ReconnectOnResume)
                return;

            if (_gameClient.IsConnected)
            {
                GameLog.Info(LogTag.Platform, "连接仍然存活，无需重连。");
                return;
            }

            if (!_wasLoggedInBeforePause)
            {
                GameLog.Info(LogTag.Platform, "切后台时未登录，跳过自动重连。");
                return;
            }

            AccountCredentials credentials = _options.CredentialProvider != null
                ? _options.CredentialProvider()
                : new AccountCredentials { Account = _gameClient.LastLoginAccount, Password = string.Empty };

            if (string.IsNullOrEmpty(credentials.Account) || string.IsNullOrEmpty(credentials.Password))
            {
                GameLog.Warn(LogTag.Platform, "没有可用的重连凭据，会话已丢失，请重新登录。");
                Publish(new PlatformSessionLostEvent());
                return;
            }

            StartReconnect(credentials);
        }

        private void StartReconnect(AccountCredentials credentials)
        {
            if (_reconnecting)
                return;

            _reconnecting = true;
            _pendingCredentials = credentials;
            _reconnectAttempt = 0;
            SetReconnectState(PlatformReconnectState.Connecting);
            DoReconnectAttempt();
        }

        private void DoReconnectAttempt()
        {
            _reconnectAttempt++;
            GameLog.InfoFormat(LogTag.Platform, "断线重连：第 {0} 次…", _reconnectAttempt);

            _gameClient.LoginAsync(_pendingCredentials.Account, _pendingCredentials.Password, ok =>
            {
                if (ok)
                {
                    _reconnecting = false;
                    SetReconnectState(PlatformReconnectState.Reconnected);
                    GameLog.Info(LogTag.Platform, "重连成功。");
                    return;
                }

                if (_reconnectAttempt >= _options.ReconnectMaxAttempts)
                {
                    _reconnecting = false;
                    SetReconnectState(PlatformReconnectState.Failed);
                    GameLog.Warn(LogTag.Platform, "重连失败，尝试次数用尽，会话已丢失。");
                    Publish(new PlatformSessionLostEvent());
                    return;
                }

                if (_timers == null)
                    return;

                _timers.Delay(this, _options.ReconnectRetryDelay, DoReconnectAttempt);
            });
        }

        private void SetReconnectState(PlatformReconnectState state)
        {
            if (ReconnectState == state)
                return;

            ReconnectState = state;
            Publish(new PlatformReconnectStateChangedEvent { State = state });
        }

        #endregion

        private void Publish<T>(T evt) where T : IGameEvent
        {
            if (_events != null)
                _events.Publish(evt);
        }
    }
}
