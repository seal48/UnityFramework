using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace GameFramework.Net.Server.Persistence
{
    /// <summary>
    /// 单文件 JSON 存储：结构简单、可直接用记事本看和改，适合开发期 / 小规模服务器。
    /// 注意：每次写入都会重写整个文件，所以不适合频繁写或几千个账号以上；正式环境用 SqliteAccountStore。
    /// </summary>
    public sealed class JsonAccountStore : IAccountStore
    {
        private sealed class StoreFile
        {
            public int Version = 1;
            public Dictionary<string, AccountRecord> Accounts =
                new Dictionary<string, AccountRecord>(StringComparer.Ordinal);
        }

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly string _path;
        private readonly INetLogger _logger;
        private readonly object _lock = new object();
        private readonly Dictionary<string, AccountRecord> _accounts =
            new Dictionary<string, AccountRecord>(StringComparer.Ordinal);

        public JsonAccountStore(string path, INetLogger logger = null)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("存储路径不能为空", nameof(path));

            _path = path;
            _logger = logger ?? NullNetLogger.Instance;
            Load();
        }

        public string Kind => "json";

        public string Path => _path;

        // ---------- 账号 ----------

        public bool Exists(string account)
        {
            lock (_lock) return !string.IsNullOrEmpty(account) && _accounts.ContainsKey(account);
        }

        public bool TryGetAccount(string account, out AccountRecord record)
        {
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(account) && _accounts.TryGetValue(account, out var found))
                {
                    record = Clone(found);
                    return true;
                }
            }

            record = null;
            return false;
        }

        public IReadOnlyList<AccountRecord> ListAccounts(int limit = 100, string search = null)
        {
            var result = new List<AccountRecord>();
            lock (_lock)
            {
                foreach (var pair in _accounts)
                {
                    if (!string.IsNullOrEmpty(search)
                        && pair.Key.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    result.Add(Clone(pair.Value));
                    if (limit > 0 && result.Count >= limit) break;
                }
            }
            return result;
        }

        public bool CreateAccount(AccountRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Account)) return false;

            lock (_lock)
            {
                if (_accounts.ContainsKey(record.Account)) return false;

                record.CreatedAtMs = record.CreatedAtMs > 0 ? record.CreatedAtMs : NowMs();
                _accounts[record.Account] = Clone(record);
                Save();
                return true;
            }
        }

        public bool UpdateAccount(AccountRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Account)) return false;

            lock (_lock)
            {
                if (!_accounts.ContainsKey(record.Account)) return false;

                _accounts[record.Account] = Clone(record);
                Save();
                return true;
            }
        }

        public bool DeleteAccount(string account)
        {
            if (string.IsNullOrEmpty(account)) return false;

            lock (_lock)
            {
                if (!_accounts.Remove(account)) return false;
                Save();
                return true;
            }
        }

        // ---------- 玩家数据 ----------

        public bool TryGetPlayerData(string account, out PlayerData data)
        {
            if (TryGetAccount(account, out var record))
            {
                data = record.Player ?? new PlayerData();
                return true;
            }

            data = null;
            return false;
        }

        public bool SavePlayerData(string account, PlayerData data)
        {
            if (data == null) return false;

            lock (_lock)
            {
                if (string.IsNullOrEmpty(account) || !_accounts.TryGetValue(account, out var record)) return false;

                record.Player = Clone(data);
                Save();
                return true;
            }
        }

        // ---------- 道具 ----------

        public IReadOnlyList<ItemStack> GetItems(string account)
        {
            var items = new List<ItemStack>();
            lock (_lock)
            {
                if (string.IsNullOrEmpty(account) || !_accounts.TryGetValue(account, out var record)) return items;

                var source = record.Player?.Items;
                if (source != null)
                {
                    foreach (var item in source) items.Add(Clone(item));
                }
            }
            return items;
        }

        public bool AddItem(string account, string itemId, int count, string extraJson = null)
        {
            if (string.IsNullOrEmpty(itemId) || count <= 0) return false;

            lock (_lock)
            {
                if (!TryGetMutable(account, out var record)) return false;

                var items = record.Player.Items;
                var stack = items.Find(i => i != null && string.Equals(i.ItemId, itemId, StringComparison.Ordinal));
                if (stack == null)
                {
                    stack = new ItemStack { ItemId = itemId, Count = 0 };
                    items.Add(stack);
                }

                stack.Count += count;
                if (!string.IsNullOrEmpty(extraJson)) stack.ExtraJson = extraJson;
                stack.UpdatedAtMs = NowMs();

                Save();
                return true;
            }
        }

        public bool RemoveItem(string account, string itemId, int count)
        {
            if (string.IsNullOrEmpty(itemId) || count <= 0) return false;

            lock (_lock)
            {
                if (!TryGetMutable(account, out var record)) return false;

                var items = record.Player.Items;
                var stack = items.Find(i => i != null && string.Equals(i.ItemId, itemId, StringComparison.Ordinal));
                if (stack == null || stack.Count < count) return false;

                stack.Count -= count;
                stack.UpdatedAtMs = NowMs();
                if (stack.Count <= 0) items.Remove(stack);

                Save();
                return true;
            }
        }

        public bool SetItemCount(string account, string itemId, int count, string extraJson = null)
        {
            if (string.IsNullOrEmpty(itemId) || count < 0) return false;

            lock (_lock)
            {
                if (!TryGetMutable(account, out var record)) return false;

                var items = record.Player.Items;
                var stack = items.Find(i => i != null && string.Equals(i.ItemId, itemId, StringComparison.Ordinal));

                if (count == 0)
                {
                    if (stack == null) return true;   // 本来就是 0，算成功
                    items.Remove(stack);
                }
                else
                {
                    if (stack == null)
                    {
                        stack = new ItemStack { ItemId = itemId };
                        items.Add(stack);
                    }

                    stack.Count = count;
                    if (!string.IsNullOrEmpty(extraJson)) stack.ExtraJson = extraJson;
                    stack.UpdatedAtMs = NowMs();
                }

                Save();
                return true;
            }
        }

        // ---------- 内部 ----------

        private bool TryGetMutable(string account, out AccountRecord record)
        {
            record = null;
            if (string.IsNullOrEmpty(account)) return false;
            if (!_accounts.TryGetValue(account, out record)) return false;

            if (record.Player == null) record.Player = new PlayerData();
            if (record.Player.Items == null) record.Player.Items = new List<ItemStack>();
            if (record.Player.Attributes == null)
                record.Player.Attributes = new Dictionary<string, long>(StringComparer.Ordinal);

            return true;
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    _logger.Info($"账号存储（json）新建：{_path}");
                    return;
                }

                var json = File.ReadAllText(_path);
                var file = JsonConvert.DeserializeObject<StoreFile>(json, Settings);
                if (file?.Accounts != null)
                {
                    foreach (var pair in file.Accounts) _accounts[pair.Key] = pair.Value;
                }

                _logger.Info($"账号存储（json）已加载：{_path}，共 {_accounts.Count} 个账号");
            }
            catch (Exception ex)
            {
                _logger.Error($"加载账号文件失败：{_path}", ex);
            }
        }

        /// <summary>写临时文件再替换，避免写一半崩溃把存档弄坏。</summary>
        private void Save()
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                var file = new StoreFile { Accounts = _accounts };
                var json = JsonConvert.SerializeObject(file, Settings);

                var temp = _path + ".tmp";
                File.WriteAllText(temp, json);

                if (File.Exists(_path)) File.Replace(temp, _path, null);
                else File.Move(temp, _path);
            }
            catch (Exception ex)
            {
                _logger.Error($"保存账号文件失败：{_path}", ex);
            }
        }

        private static AccountRecord Clone(AccountRecord record)
            => record == null ? null : JsonConvert.DeserializeObject<AccountRecord>(
                JsonConvert.SerializeObject(record, Settings), Settings);

        private static ItemStack Clone(ItemStack item)
            => item == null ? null : JsonConvert.DeserializeObject<ItemStack>(
                JsonConvert.SerializeObject(item, Settings), Settings);

        private static PlayerData Clone(PlayerData data)
            => data == null ? null : JsonConvert.DeserializeObject<PlayerData>(
                JsonConvert.SerializeObject(data, Settings), Settings);

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public void Dispose()
        {
            lock (_lock) Save();
        }
    }
}
