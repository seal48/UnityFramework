using GameFramework.Event;

namespace GameFramework.ServerSelect
{
    /// <summary>
    /// 选中的服务器变了（选服弹窗里点了一个服）。
    /// 登录界面等关心"当前服显示"的地方 Listen 它刷新就行，不用轮询。
    /// </summary>
    public struct ServerChangedEvent : IGameEvent
    {
        /// <summary>新的服 ID。</summary>
        public int ServerId;

        /// <summary>新的服名。</summary>
        public string ServerName;

        /// <summary>新的服地址。</summary>
        public string Host;

        public int Port;
    }
}
