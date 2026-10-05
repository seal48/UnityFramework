using System;

namespace GameFramework.Core
{
    /// <summary>
    /// 游戏模块的统一生命周期契约。
    ///
    /// 约定（新模块照这个来，GameController 也按这个统一驱动 / 关闭）：
    ///
    ///   Init      —— 由 GameController 在启动流程里调（各模块参数不同，这是正常的：
    ///                依赖什么就传什么）。分两种：
    ///                  · 异步：<c>Init(..., Action&lt;bool, string&gt; onComplete)</c> —— 完成后回调；
    ///                  · 同步：<c>Init(...)</c>，没有回调，返回即就绪。
    ///   IsInitialized —— 初始化完成后为 true。没就绪时调用别的 API 行为不保证。
    ///   Tick      —— 每帧由 GameController 驱动（见 <see cref="ITickable"/>）。
    ///   Shutdown  —— 关闭并释放，**幂等**（重复调用无害）。由 GameController 在 OnDestroy 里
    ///                按依赖倒序调用（后创建的先关，存储 / 日志最后关）。
    ///
    /// 命名统一说明：
    ///   · 一律用 <c>Shutdown()</c> 表示「模块关闭」（语义是"有序收尾 + 释放"，比 Dispose 更贴合）；
    ///   · 同时实现了 <see cref="IDisposable"/> 的模块，<c>Dispose()</c> 转调 <c>Shutdown()</c>，
    ///     这样 <c>using</c> 也能用，两种写法不会出现两套行为。
    /// </summary>
    public interface IGameModule
    {
        /// <summary>是否已经初始化完成。</summary>
        bool IsInitialized { get; }

        /// <summary>关闭并释放。必须可以安全地重复调用。</summary>
        void Shutdown();
    }

    /// <summary>需要每帧驱动的模块（由 GameController.Update 调用）。</summary>
    public interface ITickable
    {
        /// <summary>
        /// 每帧驱动。
        /// <paramref name="deltaTime"/> = 游戏时间（受 timeScale 影响，暂停时为 0）；
        /// <paramref name="unscaledDeltaTime"/> = 真实时间（暂停 / 切后台照走）。
        /// 用不到时间的模块忽略参数即可，但签名统一 —— 这样驱动方不用记"每个模块该传什么"。
        /// </summary>
        void Tick(float deltaTime, float unscaledDeltaTime);
    }
}
