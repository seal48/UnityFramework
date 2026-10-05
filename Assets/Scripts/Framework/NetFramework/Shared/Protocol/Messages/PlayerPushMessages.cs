using System;
using System.Collections.Generic;

namespace GameFramework.Net.Protocol
{
    /// <summary>
    /// 服务端主动推送：玩家全量快照（等级 / 经验 / 金币 / 属性 / 背包种类数）。
    /// 登录成功后先推一次当"初始同步"，之后服务端改了数据也可以再推一次全量。
    /// </summary>
    [Serializable]
    public class PlayerInfoPush
    {
        public string PlayerId;
        public string PlayerName;
        public int Level;
        public long Exp;
        public long Gold;

        /// <summary>属性表（力量 / 敏捷 / 智力…）。用 List 而不是 Dictionary，日志里更好读。</summary>
        public List<PlayerAttribute> Attributes = new List<PlayerAttribute>();

        /// <summary>背包里的道具种类数。背包具体内容由 BagChangedPush 增量同步。</summary>
        public int BagKinds;

        /// <summary>服务端数据版本号，每推一次 +1；客户端可以用它判断"这是不是新数据"。</summary>
        public long Version;

        public long ServerTimeMs;
    }

    /// <summary>一条属性。</summary>
    [Serializable]
    public class PlayerAttribute
    {
        public string Name;
        public long Value;
    }

    /// <summary>
    /// 服务端主动推送：背包变化（增量，只带变化的那一格）。
    /// Delta 是本次增减量（正数=获得，负数=消耗），Count 是变化之后的持有数量。
    /// Added=true 表示这一格是新出现的，Count=0 表示这一格被删掉了。
    /// </summary>
    [Serializable]
    public class BagChangedPush
    {
        public string ItemId;
        public int Count;
        public int Delta;
        public bool Added;

        /// <summary>背包里的道具种类数（变化之后）。</summary>
        public int BagKinds;

        public long Version;

        public long ServerTimeMs;
    }
}
