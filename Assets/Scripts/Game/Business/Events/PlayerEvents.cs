using GameFramework.Event;

namespace GameFramework.Business
{
    /// <summary>
    /// 玩家数据变了（登录同步 / 服务端推送之后都会发）。
    /// 界面只要 Listen 这个事件就够了 —— 它不需要知道是哪条协议、什么时候推下来的。
    /// 用 struct：派发不产生 GC，而且事件天生是快照，订阅方改不到原件。
    /// </summary>
    public struct PlayerInfoChangedEvent : IGameEvent
    {
        public string PlayerId;
        public string PlayerName;
        public int Level;
        public long Exp;
        public long Gold;
        public int BagKinds;
        public long Version;
    }

    /// <summary>背包里某一格变了（增量）。</summary>
    public struct BagChangedEvent : IGameEvent
    {
        public string ItemId;
        public int Count;

        /// <summary>本次增减量（正数=获得，负数=消耗）。</summary>
        public int Delta;

        /// <summary>是不是新出现的一格。</summary>
        public bool Added;

        /// <summary>变化之后的道具种类数。</summary>
        public int BagKinds;

        public long Version;
    }

    /// <summary>掉线了：业务数据已经作废，界面可以据此回到登录态。</summary>
    public struct PlayerOfflineEvent : IGameEvent
    {
        public string Reason;
    }
}
