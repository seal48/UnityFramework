using System;
using System.Collections.Generic;

namespace GameFramework.Net.Server.Persistence
{
    /// <summary>
    /// 账号与玩家数据的存储接口（增删改查）。
    /// 默认实现：
    ///   - JsonAccountStore  ：单文件 JSON，纯 .NET，Unity 编辑器内跑服务器也能用（适合开发/小规模）
    ///   - SqliteAccountStore：嵌入式数据库，单文件、有事务和索引（推荐正式使用，只能在独立后端进程里用）
    /// 换数据库（MySQL/PostgreSQL/Redis）只要再实现这个接口。
    /// </summary>
    public interface IAccountStore : IDisposable
    {
        /// <summary>存储类型名字，用于日志。</summary>
        string Kind { get; }

        // ---------- 账号：增删改查 ----------

        bool Exists(string account);

        bool TryGetAccount(string account, out AccountRecord record);

        /// <summary>查账号列表（search 为空表示全部；用于 GM 工具/排行榜等）。</summary>
        IReadOnlyList<AccountRecord> ListAccounts(int limit = 100, string search = null);

        /// <summary>增：账号已存在时返回 false。</summary>
        bool CreateAccount(AccountRecord record);

        /// <summary>改：整体覆盖账号记录（含玩家数据）。</summary>
        bool UpdateAccount(AccountRecord record);

        /// <summary>删。</summary>
        bool DeleteAccount(string account);

        // ---------- 玩家数据：等级/属性/金币 ----------

        bool TryGetPlayerData(string account, out PlayerData data);

        bool SavePlayerData(string account, PlayerData data);

        // ---------- 道具：增删改查 ----------

        IReadOnlyList<ItemStack> GetItems(string account);

        /// <summary>加道具（不存在就新建，存在就叠加数量）。</summary>
        bool AddItem(string account, string itemId, int count, string extraJson = null);

        /// <summary>扣道具（数量到 0 就删掉这一格）；数量不足返回 false。</summary>
        bool RemoveItem(string account, string itemId, int count);

        /// <summary>把某道具的数量设成指定值（0 表示删除）。</summary>
        bool SetItemCount(string account, string itemId, int count, string extraJson = null);
    }
}
