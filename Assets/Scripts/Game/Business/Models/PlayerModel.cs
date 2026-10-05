using System.Collections.Generic;
using GameFramework.Net.Protocol;

namespace GameFramework.Business
{
    /// <summary>
    /// 客户端的玩家数据。只由各个 System 写，界面只读。
    /// 这样"数据从哪来"（网络推送 / 本地计算）和"数据怎么显示"就彻底分开了。
    /// </summary>
    public sealed class PlayerModel
    {
        private readonly Dictionary<string, long> attributes = new Dictionary<string, long>();
        private readonly Dictionary<string, int> bag = new Dictionary<string, int>();

        public string PlayerId { get; private set; }

        public string PlayerName { get; private set; }

        public int Level { get; private set; } = 1;

        public long Exp { get; private set; }

        public long Gold { get; private set; }

        /// <summary>服务端数据版本号（服务端每推一次 +1）。</summary>
        public long Version { get; private set; }

        /// <summary>累计收到多少次推送（调试 / 自检用）。</summary>
        public long PushCount { get; private set; }

        public IReadOnlyDictionary<string, long> Attributes { get { return attributes; } }

        public IReadOnlyDictionary<string, int> Bag { get { return bag; } }

        public int BagKinds { get { return bag.Count; } }

        /// <summary>用全量快照覆盖本地数据。</summary>
        public void ApplyInfo(PlayerInfoPush push)
        {
            if (push == null) return;

            PushCount++;
            PlayerId = push.PlayerId;
            PlayerName = push.PlayerName;
            Level = push.Level;
            Exp = push.Exp;
            Gold = push.Gold;
            Version = push.Version;

            attributes.Clear();
            if (push.Attributes != null)
            {
                for (int i = 0; i < push.Attributes.Count; i++)
                {
                    var attribute = push.Attributes[i];
                    if (attribute == null || string.IsNullOrEmpty(attribute.Name)) continue;
                    attributes[attribute.Name] = attribute.Value;
                }
            }

            // 快照里只带背包种类数，具体内容靠增量推
            if (push.BagKinds == 0) bag.Clear();
        }

        /// <summary>应用一条背包增量。</summary>
        public void ApplyBag(BagChangedPush push)
        {
            if (push == null || string.IsNullOrEmpty(push.ItemId)) return;

            PushCount++;
            Version = push.Version;

            if (push.Count <= 0) bag.Remove(push.ItemId);
            else bag[push.ItemId] = push.Count;
        }

        /// <summary>清空（掉线 / 重新登录）。</summary>
        public void Reset()
        {
            PlayerId = null;
            PlayerName = null;
            Level = 1;
            Exp = 0;
            Gold = 0;
            Version = 0;
            PushCount = 0;
            attributes.Clear();
            bag.Clear();
        }
    }
}
