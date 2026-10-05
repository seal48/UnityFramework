using System;
using UnityEngine;

namespace GameFramework.Platform
{
    /// <summary>
    /// Android 平台适配层的配置，由 GameController 在 Awake 里赋值（Inspector 可改）。
    /// </summary>
    [Serializable]
    public sealed class PlatformOptions
    {
        /// <summary>是否开启安全区 / 刘海屏适配：把 UI 根 Canvas 收缩到系统安全区内。</summary>
        [Tooltip("是否开启安全区 / 刘海屏适配")]
        public bool ApplySafeArea = true;

        /// <summary>是否响应系统返回键（Android Back / 编辑器下用 Escape 模拟）。</summary>
        [Tooltip("是否响应系统返回键")]
        public bool TrackBackKey = true;

        /// <summary>没有面板可关、也没有拦截器处理时，按返回键的行为。</summary>
        [Tooltip("根界面按返回键的行为")]
        public BackRootBehavior BackOnRoot = BackRootBehavior.MinimizeApp;

        /// <summary>回前台后连接断了是否自动重连。</summary>
        [Tooltip("回前台后是否自动重连")]
        public bool ReconnectOnResume = true;

        /// <summary>重连最大尝试次数（每次之间间隔 ReconnectRetryDelay 秒）。</summary>
        [Tooltip("重连最大尝试次数")]
        public int ReconnectMaxAttempts = 3;

        /// <summary>重连失败后的重试间隔（秒）。</summary>
        [Tooltip("重连失败后的重试间隔（秒）")]
        public float ReconnectRetryDelay = 2f;

        /// <summary>
        /// 重连用的账号密码提供者。默认用「存档里的上次账号 + 空密码」，
        /// 因为密码没有持久化。需要真正静默重连时，由游戏层在 InitPlatform 之后覆盖这个委托，
        /// 返回真实密码（例如本地安全保存 / 记住密码）。返回空密码时不会自动重连，
        /// 改为发 <see cref="PlatformSessionLostEvent"/> 让游戏引导重新登录。
        /// </summary>
        [NonSerialized]
        public Func<AccountCredentials> CredentialProvider;

        /// <summary>
        /// 视为「真正切到后台」的最小暂停时长（秒）。权限弹窗等系统对话框也会触发
        /// OnApplicationPause，但通常不到 1 秒；超过这个阈值才当作离开了 App，
        /// 才会在回前台时做断线处理。
        /// </summary>
        [Tooltip("切后台判定阈值（秒）：权限弹窗之类的短暂 pause 不算切后台")]
        public float BackgroundThresholdSeconds = 1.5f;
    }

    /// <summary>根界面时按返回键的处理方式。</summary>
    public enum BackRootBehavior
    {
        /// <summary>把 App 退到后台（Android moveTaskToBack；其它平台什么都不做）。</summary>
        MinimizeApp,

        /// <summary>什么都不做，只发事件让游戏自己决定。</summary>
        DoNothing,
    }

    /// <summary>重连用的账号密码。</summary>
    public struct AccountCredentials
    {
        public string Account;
        public string Password;
    }

    /// <summary>断线重连状态。</summary>
    public enum PlatformReconnectState
    {
        /// <summary>没有重连在进行。</summary>
        Idle,

        /// <summary>正在尝试重连。</summary>
        Connecting,

        /// <summary>重连成功（已重新登录）。</summary>
        Reconnected,

        /// <summary>重连失败（尝试次数用尽），会话已丢失。</summary>
        Failed,
    }
}
