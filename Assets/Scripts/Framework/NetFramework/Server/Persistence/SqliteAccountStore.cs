#if !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace GameFramework.Net.Server.Persistence
{
    /// <summary>
    /// SQLite 存储：单文件数据库，不需要额外装服务，支持事务和索引，是这套框架推荐的正式存储。
    ///
    /// 表结构：
    ///   accounts     ：账号 / 密码哈希 / playerId / 创建时间 / 最后登录 / 是否封禁
    ///   player_state ：等级 / 经验 / 金币 / 属性（JSON 列，属性名随便加）
    ///   player_items ：道具（account + item_id 唯一，含数量与附加 JSON）
    ///
    /// 只在后端独立进程里可用（Unity 里没有 SQLite 驱动，Unity 编辑器内跑服务器请用 JsonAccountStore）。
    /// </summary>
    public sealed class SqliteAccountStore : IAccountStore
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly string _path;
        private readonly INetLogger _logger;
        private readonly object _lock = new object();
        private readonly SqliteConnection _connection;

        public SqliteAccountStore(string path, INetLogger logger = null)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("数据库路径不能为空", nameof(path));

            _path = path;
            _logger = logger ?? NullNetLogger.Instance;

            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Default,
                Pooling = false
            };

            _connection = new SqliteConnection(builder.ToString());
            _connection.Open();

            Execute("PRAGMA journal_mode=WAL;");
            Execute("PRAGMA foreign_keys=ON;");
            CreateSchema();

            _logger.Info($"账号存储（sqlite）已就绪：{path}");
        }

        public string Kind => "sqlite";

        public string Path => _path;

        // ---------- 账号 ----------

        public bool Exists(string account)
        {
            if (string.IsNullOrEmpty(account)) return false;

            lock (_lock)
            {
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = "SELECT 1 FROM accounts WHERE account = $account LIMIT 1;";
                    command.Parameters.AddWithValue("$account", account);
                    return command.ExecuteScalar() != null;
                }
            }
        }

        public bool TryGetAccount(string account, out AccountRecord record)
        {
            record = null;
            if (string.IsNullOrEmpty(account)) return false;

            lock (_lock)
            {
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT account, password_hash, player_id, created_at_ms, last_login_ms, banned " +
                        "FROM accounts WHERE account = $account LIMIT 1;";
                    command.Parameters.AddWithValue("$account", account);

                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read()) return false;

                        record = new AccountRecord
                        {
                            Account = reader.GetString(0),
                            PasswordHash = reader.IsDBNull(1) ? null : reader.GetString(1),
                            PlayerId = reader.IsDBNull(2) ? null : reader.GetString(2),
                            CreatedAtMs = reader.GetInt64(3),
                            LastLoginMs = reader.GetInt64(4),
                            Banned = reader.GetInt64(5) != 0
                        };
                    }
                }

                record.Player = LoadPlayerData(account) ?? new PlayerData();
                record.Player.Items = LoadItems(account);
                return true;
            }
        }

        public IReadOnlyList<AccountRecord> ListAccounts(int limit = 100, string search = null)
        {
            var accounts = new List<string>();

            lock (_lock)
            {
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = string.IsNullOrEmpty(search)
                        ? "SELECT account FROM accounts ORDER BY account LIMIT $limit;"
                        : "SELECT account FROM accounts WHERE account LIKE $search ORDER BY account LIMIT $limit;";

                    command.Parameters.AddWithValue("$limit", limit > 0 ? limit : 100);
                    if (!string.IsNullOrEmpty(search)) command.Parameters.AddWithValue("$search", "%" + search + "%");

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read()) accounts.Add(reader.GetString(0));
                    }
                }
            }

            var result = new List<AccountRecord>(accounts.Count);
            foreach (var account in accounts)
            {
                if (TryGetAccount(account, out var record)) result.Add(record);
            }
            return result;
        }

        public bool CreateAccount(AccountRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Account)) return false;
            if (record.CreatedAtMs <= 0) record.CreatedAtMs = NowMs();

            lock (_lock)
            {
                using (var transaction = _connection.BeginTransaction())
                {
                    using (var command = _connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText =
                            "INSERT OR IGNORE INTO accounts (account, password_hash, player_id, created_at_ms, last_login_ms, banned) " +
                            "VALUES ($account, $hash, $playerId, $created, $lastLogin, $banned);";
                        command.Parameters.AddWithValue("$account", record.Account);
                        command.Parameters.AddWithValue("$hash", record.PasswordHash ?? string.Empty);
                        command.Parameters.AddWithValue("$playerId", record.PlayerId ?? record.Account);
                        command.Parameters.AddWithValue("$created", record.CreatedAtMs);
                        command.Parameters.AddWithValue("$lastLogin", record.LastLoginMs);
                        command.Parameters.AddWithValue("$banned", record.Banned ? 1 : 0);

                        if (command.ExecuteNonQuery() == 0) return false;   // 已存在
                    }

                    var data = record.Player ?? new PlayerData();
                    WritePlayerData(transaction, record.Account, data);
                    foreach (var item in data.Items ?? new List<ItemStack>())
                        WriteItem(transaction, record.Account, item.ItemId, item.Count, item.ExtraJson);

                    transaction.Commit();
                }

                return true;
            }
        }

        public bool UpdateAccount(AccountRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Account)) return false;

            lock (_lock)
            {
                using (var transaction = _connection.BeginTransaction())
                {
                    using (var command = _connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText =
                            "UPDATE accounts SET password_hash = $hash, player_id = $playerId, " +
                            "last_login_ms = $lastLogin, banned = $banned WHERE account = $account;";
                        command.Parameters.AddWithValue("$hash", record.PasswordHash ?? string.Empty);
                        command.Parameters.AddWithValue("$playerId", record.PlayerId ?? record.Account);
                        command.Parameters.AddWithValue("$lastLogin", record.LastLoginMs);
                        command.Parameters.AddWithValue("$banned", record.Banned ? 1 : 0);
                        command.Parameters.AddWithValue("$account", record.Account);

                        if (command.ExecuteNonQuery() == 0) return false;   // 不存在
                    }

                    var data = record.Player ?? new PlayerData();
                    WritePlayerData(transaction, record.Account, data);

                    using (var delete = _connection.CreateCommand())
                    {
                        delete.Transaction = transaction;
                        delete.CommandText = "DELETE FROM player_items WHERE account = $account;";
                        delete.Parameters.AddWithValue("$account", record.Account);
                        delete.ExecuteNonQuery();
                    }

                    foreach (var item in data.Items ?? new List<ItemStack>())
                        WriteItem(transaction, record.Account, item.ItemId, item.Count, item.ExtraJson);

                    transaction.Commit();
                }

                return true;
            }
        }

        public bool DeleteAccount(string account)
        {
            if (string.IsNullOrEmpty(account)) return false;

            lock (_lock)
            {
                using (var command = _connection.CreateCommand())
                {
                    // player_state / player_items 由外键 ON DELETE CASCADE 一起删掉
                    command.CommandText = "DELETE FROM accounts WHERE account = $account;";
                    command.Parameters.AddWithValue("$account", account);
                    return command.ExecuteNonQuery() > 0;
                }
            }
        }

        // ---------- 玩家数据 ----------

        public bool TryGetPlayerData(string account, out PlayerData data)
        {
            if (string.IsNullOrEmpty(account))
            {
                data = null;
                return false;
            }

            lock (_lock)
            {
                if (!Exists(account))
                {
                    data = null;
                    return false;
                }

                data = LoadPlayerData(account) ?? new PlayerData();
                data.Items = LoadItems(account);
                return true;
            }
        }

        public bool SavePlayerData(string account, PlayerData data)
        {
            if (string.IsNullOrEmpty(account) || data == null) return false;

            lock (_lock)
            {
                if (!Exists(account)) return false;

                using (var transaction = _connection.BeginTransaction())
                {
                    WritePlayerData(transaction, account, data);
                    transaction.Commit();
                }
                return true;
            }
        }

        // ---------- 道具 ----------

        public IReadOnlyList<ItemStack> GetItems(string account)
        {
            if (string.IsNullOrEmpty(account)) return new List<ItemStack>();

            lock (_lock) return LoadItems(account);
        }

        public bool AddItem(string account, string itemId, int count, string extraJson = null)
        {
            if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(itemId) || count <= 0) return false;

            lock (_lock)
            {
                if (!Exists(account)) return false;

                using (var command = _connection.CreateCommand())
                {
                    // 一条语句搞定"有就叠加、没有就插入"，天然是原子的
                    command.CommandText =
                        "INSERT INTO player_items (account, item_id, count, extra_json, updated_at_ms) " +
                        "VALUES ($account, $itemId, $count, $extra, $now) " +
                        "ON CONFLICT(account, item_id) DO UPDATE SET " +
                        "count = count + excluded.count, " +
                        "extra_json = COALESCE(excluded.extra_json, extra_json), " +
                        "updated_at_ms = excluded.updated_at_ms;";
                    command.Parameters.AddWithValue("$account", account);
                    command.Parameters.AddWithValue("$itemId", itemId);
                    command.Parameters.AddWithValue("$count", count);
                    command.Parameters.AddWithValue("$extra", (object)extraJson ?? DBNull.Value);
                    command.Parameters.AddWithValue("$now", NowMs());
                    return command.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool RemoveItem(string account, string itemId, int count)
        {
            if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(itemId) || count <= 0) return false;

            lock (_lock)
            {
                using (var transaction = _connection.BeginTransaction())
                {
                    long current;
                    using (var query = _connection.CreateCommand())
                    {
                        query.Transaction = transaction;
                        query.CommandText = "SELECT count FROM player_items WHERE account = $account AND item_id = $itemId;";
                        query.Parameters.AddWithValue("$account", account);
                        query.Parameters.AddWithValue("$itemId", itemId);

                        var value = query.ExecuteScalar();
                        if (value == null || value == DBNull.Value) return false;
                        current = Convert.ToInt64(value);
                    }

                    if (current < count) return false;

                    using (var command = _connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        if (current == count)
                        {
                            command.CommandText = "DELETE FROM player_items WHERE account = $account AND item_id = $itemId;";
                        }
                        else
                        {
                            command.CommandText =
                                "UPDATE player_items SET count = count - $count, updated_at_ms = $now " +
                                "WHERE account = $account AND item_id = $itemId;";
                            command.Parameters.AddWithValue("$count", count);
                            command.Parameters.AddWithValue("$now", NowMs());
                        }

                        command.Parameters.AddWithValue("$account", account);
                        command.Parameters.AddWithValue("$itemId", itemId);
                        command.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    return true;
                }
            }
        }

        public bool SetItemCount(string account, string itemId, int count, string extraJson = null)
        {
            if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(itemId) || count < 0) return false;

            lock (_lock)
            {
                if (!Exists(account)) return false;

                using (var command = _connection.CreateCommand())
                {
                    if (count == 0)
                    {
                        command.CommandText = "DELETE FROM player_items WHERE account = $account AND item_id = $itemId;";
                    }
                    else
                    {
                        command.CommandText =
                            "INSERT INTO player_items (account, item_id, count, extra_json, updated_at_ms) " +
                            "VALUES ($account, $itemId, $count, $extra, $now) " +
                            "ON CONFLICT(account, item_id) DO UPDATE SET " +
                            "count = excluded.count, " +
                            "extra_json = COALESCE(excluded.extra_json, extra_json), " +
                            "updated_at_ms = excluded.updated_at_ms;";
                        command.Parameters.AddWithValue("$count", count);
                        command.Parameters.AddWithValue("$extra", (object)extraJson ?? DBNull.Value);
                        command.Parameters.AddWithValue("$now", NowMs());
                    }

                    command.Parameters.AddWithValue("$account", account);
                    command.Parameters.AddWithValue("$itemId", itemId);
                    command.ExecuteNonQuery();
                    return true;
                }
            }
        }

        // ---------- 内部 ----------

        private void CreateSchema()
        {
            Execute(@"
CREATE TABLE IF NOT EXISTS accounts (
    account        TEXT PRIMARY KEY,
    password_hash  TEXT NOT NULL,
    player_id      TEXT NOT NULL,
    created_at_ms  INTEGER NOT NULL DEFAULT 0,
    last_login_ms  INTEGER NOT NULL DEFAULT 0,
    banned         INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS player_state (
    account         TEXT PRIMARY KEY,
    level           INTEGER NOT NULL DEFAULT 1,
    exp             INTEGER NOT NULL DEFAULT 0,
    gold            INTEGER NOT NULL DEFAULT 0,
    attributes_json TEXT NOT NULL DEFAULT '{}',
    FOREIGN KEY (account) REFERENCES accounts(account) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS player_items (
    account       TEXT NOT NULL,
    item_id       TEXT NOT NULL,
    count         INTEGER NOT NULL DEFAULT 1,
    extra_json    TEXT NULL,
    updated_at_ms INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (account, item_id),
    FOREIGN KEY (account) REFERENCES accounts(account) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_player_items_item ON player_items(item_id);
");
        }

        private PlayerData LoadPlayerData(string account)
        {
            using (var command = _connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT level, exp, gold, attributes_json FROM player_state WHERE account = $account LIMIT 1;";
                command.Parameters.AddWithValue("$account", account);

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return null;

                    var data = new PlayerData
                    {
                        Level = (int)reader.GetInt64(0),
                        Exp = reader.GetInt64(1),
                        Gold = reader.GetInt64(2)
                    };

                    if (!reader.IsDBNull(3))
                    {
                        var json = reader.GetString(3);
                        if (!string.IsNullOrEmpty(json))
                        {
                            data.Attributes = JsonConvert.DeserializeObject<Dictionary<string, long>>(json, JsonSettings)
                                              ?? new Dictionary<string, long>(StringComparer.Ordinal);
                        }
                    }

                    return data;
                }
            }
        }

        private List<ItemStack> LoadItems(string account)
        {
            var items = new List<ItemStack>();

            using (var command = _connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT item_id, count, extra_json, updated_at_ms FROM player_items " +
                    "WHERE account = $account ORDER BY item_id;";
                command.Parameters.AddWithValue("$account", account);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        items.Add(new ItemStack
                        {
                            ItemId = reader.GetString(0),
                            Count = (int)reader.GetInt64(1),
                            ExtraJson = reader.IsDBNull(2) ? null : reader.GetString(2),
                            UpdatedAtMs = reader.GetInt64(3)
                        });
                    }
                }
            }

            return items;
        }

        private void WritePlayerData(SqliteTransaction transaction, string account, PlayerData data)
        {
            var attributes = data.Attributes ?? new Dictionary<string, long>(StringComparer.Ordinal);
            var attributesJson = JsonConvert.SerializeObject(attributes, JsonSettings);

            using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO player_state (account, level, exp, gold, attributes_json) " +
                    "VALUES ($account, $level, $exp, $gold, $attributes) " +
                    "ON CONFLICT(account) DO UPDATE SET " +
                    "level = excluded.level, exp = excluded.exp, gold = excluded.gold, " +
                    "attributes_json = excluded.attributes_json;";
                command.Parameters.AddWithValue("$account", account);
                command.Parameters.AddWithValue("$level", data.Level);
                command.Parameters.AddWithValue("$exp", data.Exp);
                command.Parameters.AddWithValue("$gold", data.Gold);
                command.Parameters.AddWithValue("$attributes", attributesJson);
                command.ExecuteNonQuery();
            }
        }

        private void WriteItem(SqliteTransaction transaction, string account, string itemId, int count, string extraJson)
        {
            if (string.IsNullOrEmpty(itemId) || count <= 0) return;

            using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO player_items (account, item_id, count, extra_json, updated_at_ms) " +
                    "VALUES ($account, $itemId, $count, $extra, $now) " +
                    "ON CONFLICT(account, item_id) DO UPDATE SET " +
                    "count = excluded.count, extra_json = excluded.extra_json, updated_at_ms = excluded.updated_at_ms;";
                command.Parameters.AddWithValue("$account", account);
                command.Parameters.AddWithValue("$itemId", itemId);
                command.Parameters.AddWithValue("$count", count);
                command.Parameters.AddWithValue("$extra", (object)extraJson ?? DBNull.Value);
                command.Parameters.AddWithValue("$now", NowMs());
                command.ExecuteNonQuery();
            }
        }

        private void Execute(string sql)
        {
            lock (_lock)
            {
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.ExecuteNonQuery();
                }
            }
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public void Dispose()
        {
            lock (_lock)
            {
                try
                {
                    _connection.Close();
                    _connection.Dispose();
                }
                catch (Exception)
                {
                    // 关闭失败无所谓，进程退出会释放
                }
            }
        }
    }
}
#endif
