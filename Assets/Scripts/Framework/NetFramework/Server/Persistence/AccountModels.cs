using System;
using System.Collections.Generic;

namespace GameFramework.Net.Server.Persistence
{
    /// <summary>账号记录：登录用到的部分 + 该账号的玩家数据。</summary>
    [Serializable]
    public sealed class AccountRecord
    {
        /// <summary>账号（主键，唯一）。</summary>
        public string Account;

        /// <summary>密码哈希（pbkdf2$sha256$迭代次数$盐$哈希），绝不存明文。</summary>
        public string PasswordHash;

        /// <summary>游戏内玩家 id（可以和账号不同，方便以后支持改名/多角色）。</summary>
        public string PlayerId;

        public long CreatedAtMs;
        public long LastLoginMs;

        /// <summary>封禁标记，登录时会被拒绝。</summary>
        public bool Banned;

        /// <summary>该账号的玩家数据（等级/属性/金币/道具）。</summary>
        public PlayerData Player = new PlayerData();
    }

    /// <summary>一个玩家的游戏数据。</summary>
    [Serializable]
    public sealed class PlayerData
    {
        public int Level = 1;

        public long Exp;

        public long Gold;

        /// <summary>属性表：力量/敏捷/智力… 用字典方便你随便加属性。</summary>
        public Dictionary<string, long> Attributes = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>背包里的道具。</summary>
        public List<ItemStack> Items = new List<ItemStack>();
    }

    /// <summary>一格道具：道具 id + 数量 + 自定义附加信息（附魔/耐久等）。</summary>
    [Serializable]
    public sealed class ItemStack
    {
        public string ItemId;
        public int Count;

        /// <summary>附加数据（JSON 字符串，随意塞耐久、附魔、过期时间）。没有就是 null。</summary>
        public string ExtraJson;

        public long UpdatedAtMs;
    }
}
