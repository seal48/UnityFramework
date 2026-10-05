using UnityEngine;

namespace Game
{
    /// <summary>
    /// 热更程序集的统一入口（由 AOT 的 <c>HybridCLRBoot</c> 反射调用）。
    ///
    /// 之所以用反射而不是直接引用：热更程序集可以被 AOT 代码引用，
    /// 但 AOT 代码**不能静态引用**热更程序集（否则就编进包、失去热更意义了），
    /// 所以入口一律走反射。
    /// </summary>
    public static class HotEntry
    {
        /// <summary>启动游戏：创建 GameController 根节点，走既有启动流程。</summary>
        public static void Boot()
        {
            Debug.Log("[HotEntry] 热更入口 Boot() 被调用");
            GameController.CreateRoot();
        }
    }
}
