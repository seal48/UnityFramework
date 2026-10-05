using System.Collections.Generic;
using GameFramework.Config;
using GameFramework.Core;
using GameFramework.Event;
using GameFramework.Log;
using GameFramework.Storage;

namespace GameFramework.ServerSelect
{
    /// <summary>
    /// 区服列表 / 选服管理（客户端内置区服表方案）。
    ///
    /// 数据源：Config.Database.ServerList（Excel 导出的区服表，纯客户端）。
    /// 状态：当前选中的服 + 记忆（存 Storage.Account.LastServerId，设备级）。
    ///
    /// 用法：
    ///   var servers = ServerSelect.Current.All;      // 所有服（按 SortOrder 排序）
    ///   ServerSelect.Current.Select(serverId);       // 选中某服
    ///   var s = ServerSelect.Current.Current;        // 当前服（null = 区服表空/没选）
    ///   gameClient.SetServer(s.Host, s.Port);        // 登录前把目标服务器指过去
    /// </summary>
    public sealed class ServerSelectManager : IGameModule
    {
        private readonly ConfigManager config;
        private readonly LocalStorageManager storage;
        private readonly IEventBus events;

        /// <summary>当前选中的服。null = 区服表空（理论上不会，表里总有至少一个）。</summary>
        public ServerListConfig Current { get; private set; }

        /// <summary>是否已经初始化完成。</summary>
        public bool IsInitialized { get; private set; }

        public ServerSelectManager(ConfigManager config, LocalStorageManager storage, IEventBus events = null)
        {
            this.config = config;
            this.storage = storage;
            this.events = events;
        }

        /// <summary>关闭：丢开当前选中的服。幂等。</summary>
        public void Shutdown()
        {
            IsInitialized = false;
            Current = null;
        }

        /// <summary>初始化：恢复上次选的服（或推荐服）。区服表 / 存档不可用时回退到第一个。</summary>
        public void Init()
        {
            IsInitialized = true;

            var table = Table;
            if (table == null || table.Count == 0)
            {
                GameLog.Warn(LogTag.Net, "区服表为空，选服功能不可用。");
                Current = null;
                return;
            }

            int remembered = storage != null ? storage.Account.LastServerId : 0;

            // 1) 上次选的服还在表里、且没在维护 → 用它
            if (remembered > 0)
            {
                var fromMemory = table.Get(remembered);
                if (fromMemory != null)
                {
                    if (fromMemory.Status != ServerStatus.Maintenance)
                    {
                        Current = fromMemory;
                        GameLog.Info(LogTag.Net, $"恢复上次选的服：{Describe(fromMemory)}");
                        return;
                    }

                    // 上次选的服进了维护：不默认停在维护服上（否则玩家一进登录界面就没法登），
                    // 改选一个正常服；玩家仍可以在选服弹窗里看到并手动选回它。
                    GameLog.Warn(LogTag.Net, $"上次选的服正在维护，自动改选正常服：{Describe(fromMemory)}");
                }
            }

            // 2) 推荐 + 正常（最优）
            foreach (var row in table.Rows)
            {
                if (row.IsRecommended && row.Status != ServerStatus.Maintenance)
                {
                    Current = row;
                    GameLog.Info(LogTag.Net, $"默认选中推荐服：{Describe(row)}");
                    return;
                }
            }

            // 3) 第一个正常服
            foreach (var row in table.Rows)
            {
                if (row.Status != ServerStatus.Maintenance)
                {
                    Current = row;
                    GameLog.Info(LogTag.Net, $"默认选中正常服：{Describe(row)}");
                    return;
                }
            }

            // 4) 全是维护服：只能选第一个（登录会被拦，见 LoginPanel）
            Current = table.Rows[0];
            GameLog.Warn(LogTag.Net, $"所有服都在维护，暂选：{Describe(Current)}");
        }

        /// <summary>区服表（null = 配置没加载好）。</summary>
        public TbServerList Table
        {
            get
            {
                if (config == null || config.Database == null) return null;
                return config.Database.ServerList;
            }
        }

        /// <summary>所有服，按 SortOrder 升序（稳定：同序按表顺序）。</summary>
        public IReadOnlyList<ServerListConfig> All
        {
            get
            {
                var table = Table;
                if (table == null) return new List<ServerListConfig>();
                var list = new List<ServerListConfig>(table.Rows);
                list.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
                return list;
            }
        }

        /// <summary>选中某服并记忆到存档。id 不存在时忽略并返回 false。</summary>
        public bool Select(int id)
        {
            var table = Table;
            if (table == null) return false;
            var row = table.Get(id);
            if (row == null)
            {
                GameLog.Warn(LogTag.Net, $"要选的服不存在：id={id}");
                return false;
            }

            Current = row;

            if (storage != null)
            {
                storage.Account.LastServerId = id;
                storage.Save(storage.AccountFile);
            }

            // 通知关心"当前服"的界面刷新（登录界面按钮文本等）
            if (events != null)
            {
                events.Publish(new ServerChangedEvent
                {
                    ServerId = row.Id,
                    ServerName = row.Name,
                    Host = row.Host,
                    Port = row.Port,
                });
            }

            GameLog.Info(LogTag.Net, $"已选服：{Describe(row)}");
            return true;
        }

        private static string Describe(ServerListConfig s)
        {
            return $"id={s.Id} '{s.Name}' {s.Host}:{s.Port} 状态={s.Status}";
        }
    }
}
