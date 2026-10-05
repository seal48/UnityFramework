using GameFramework.Event;
using UnityEngine;

namespace GameFramework.Platform
{
    /// <summary>App 进入 / 退出后台。Paused = true 切后台，false 回前台。</summary>
    public struct PlatformApplicationPausedEvent : IGameEvent
    {
        public bool Paused;
    }

    /// <summary>App 获得 / 失去焦点。Android 上权限弹窗等系统对话框也会让焦点短暂丢失。</summary>
    public struct PlatformApplicationFocusedEvent : IGameEvent
    {
        public bool Focused;
    }

    /// <summary>按下了系统返回键（在默认处理之前发出，界面可听它播音效 / 统计）。</summary>
    public struct PlatformBackPressedEvent : IGameEvent
    {
    }

    /// <summary>安全区变化（旋转 / 刘海屏尺寸变化），SafeArea 是新的安全区（像素）。</summary>
    public struct PlatformSafeAreaChangedEvent : IGameEvent
    {
        public Rect SafeArea;
    }

    /// <summary>一次运行时权限申请的结果。DontAskAgain = 用户勾了「不再询问」。</summary>
    public struct PlatformPermissionResult
    {
        public string Permission;
        public bool Granted;
        public bool DontAskAgain;
    }

    /// <summary>
    /// 会话丢失：回前台发现连接断了、又没有凭据可自动重连（或重连失败）。
    /// 游戏应该在这里引导玩家重新登录（打开登录界面 / 弹提示）。
    /// </summary>
    public struct PlatformSessionLostEvent : IGameEvent
    {
    }

    /// <summary>断线重连状态变化。</summary>
    public struct PlatformReconnectStateChangedEvent : IGameEvent
    {
        public PlatformReconnectState State;
    }
}
