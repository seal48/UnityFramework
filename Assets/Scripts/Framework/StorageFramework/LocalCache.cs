using System;
using System.Collections.Generic;

namespace GameFramework.Storage
{
    /// <summary>
    /// 本地缓存（cache.json）：引导标记、弹窗是否读过、随手存的 KV、以及一份「玩家数据快照」。
    ///
    /// 关于快照：它**不是存档**。养成数据、背包、货币的权威永远在服务端，
    /// 这份快照只用来进游戏时先把界面渲染出来，避免白屏等网络；
    /// 收到服务端数据后立刻覆盖，任何写操作都必须走服务端。
    /// </summary>
    [Serializable]
    public sealed class LocalCache
    {
        // ---- 一次性标记 ----

        /// <summary>新手引导是否走完了。</summary>
        public bool GuidanceDone = false;

        /// <summary>已经看过、不用再弹的公告 / 弹窗 ID。</summary>
        public List<string> ShownPopupIds = new List<string>();

        // ---- 随手存的 KV（结构还没定下来的零散数据放这儿，定下来就提升成正式字段）----

        public Dictionary<string, string> Prefs = new Dictionary<string, string>();

        // ---- 玩家数据快照（只用于展示，收到服务端数据后立刻覆盖）----

        /// <summary>快照对应的账号，换账号时用来判断该不该丢掉快照。</summary>
        public string SnapshotAccount = string.Empty;

        public int SnapshotLevel = 0;
        public long SnapshotGold = 0;
        public long SnapshotSyncedUnixSeconds = 0;

        /// <summary>项目自己的零散字段：SnapshotFields["Exp"] = "12345"。</summary>
        public Dictionary<string, string> SnapshotFields = new Dictionary<string, string>();

        /// <summary>快照是不是这个账号的（换账号了就得丢掉，免得看到上一个号的等级）。</summary>
        public bool SnapshotBelongsTo(string account)
        {
            return !string.IsNullOrEmpty(SnapshotAccount)
                && string.Equals(SnapshotAccount, account, StringComparison.Ordinal);
        }

        public void ClearSnapshot()
        {
            SnapshotAccount = string.Empty;
            SnapshotLevel = 0;
            SnapshotGold = 0;
            SnapshotSyncedUnixSeconds = 0;
            SnapshotFields.Clear();
        }
    }
}