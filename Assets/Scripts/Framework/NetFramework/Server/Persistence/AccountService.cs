using System;
using System.Collections.Generic;
using GameFramework.Net.Protocol;

namespace GameFramework.Net.Server.Persistence
{
    /// <summary>注册结果（网络注册协议直接把它转成 register.response）。</summary>
    public sealed class RegisterResult
    {
        public bool Success;
        public string Reason;
        public string Message;
        public string PlayerId;

        public static RegisterResult Ok(string playerId)
            => new RegisterResult { Success = true, PlayerId = playerId };

        public static RegisterResult Fail(string reason, string message)
            => new RegisterResult { Success = false, Reason = reason, Message = message };
    }

    /// <summary>
    /// 账号业务层：注册、登录校验、改密码、封禁，以及账号内玩家数据（等级/属性/金币/道具）的增删改查。
    /// 它同时实现了 ILoginValidator，所以可以直接塞进 GameServerOptions.LoginValidator 用。
    ///
    /// 所有修改都会立刻落到 IAccountStore（JSON 文件或 SQLite）。
    /// </summary>
    public sealed class AccountService : ILoginValidator
    {
        private readonly IAccountStore _store;
        private readonly INetLogger _logger;
        private readonly int _passwordIterations;

        public AccountService(IAccountStore store, INetLogger logger = null, int passwordIterations = 100000)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? NullNetLogger.Instance;
            _passwordIterations = passwordIterations < 1000 ? 1000 : passwordIterations;
        }

        public IAccountStore Store => _store;

        /// <summary>账号不存在时自动用本次密码注册（只建议本地开发打开）。</summary>
        public bool AutoRegisterOnLogin { get; set; }

        // ---------------- 注册规则（可改）----------------

        public int MinAccountLength = 3;
        public int MaxAccountLength = 20;
        public int MinPasswordLength = 6;

        // ---------------- 账号增删改查 ----------------

        public bool Exists(string account) => _store.Exists(AccountNameRules.Normalize(account));

        public bool TryGet(string account, out AccountRecord record)
            => _store.TryGetAccount(AccountNameRules.Normalize(account), out record);

        public IReadOnlyList<AccountRecord> List(int limit = 100, string search = null)
            => _store.ListAccounts(limit, search);

        /// <summary>注册账号（走账号/密码规则，客户端注册协议用这个）。</summary>
        public RegisterResult Register(string account, string password, string playerId = null)
        {
            account = AccountNameRules.Normalize(account);

            var invalid = ValidateNewAccount(account, password);
            if (invalid != null) return invalid;

            return CreateInternal(account, password, playerId);
        }

        /// <summary>
        /// 直接建账号，不校验长度/强度（GM 工具、控制台、开发账号用）。
        /// 客户端注册请用 Register，那样才有规则保护。
        /// </summary>
        public RegisterResult Create(string account, string password, string playerId = null)
        {
            account = AccountNameRules.Normalize(account);

            if (string.IsNullOrWhiteSpace(account))
                return RegisterResult.Fail(RegisterReasons.InvalidAccount, "账号不能为空");

            if (_store.Exists(account))
                return RegisterResult.Fail(RegisterReasons.AccountExists, "账号已存在");

            return CreateInternal(account, password, playerId);
        }

        /// <summary>兼容旧调用：注册失败时把原因写进 error。</summary>
        public bool Register(string account, string password, out string error, string playerId = null)
        {
            var result = Register(account, password, playerId);
            error = result.Success ? null : result.Message;
            return result.Success;
        }

        private RegisterResult ValidateNewAccount(string account, string password)
        {
            if (string.IsNullOrWhiteSpace(account))
                return RegisterResult.Fail(RegisterReasons.InvalidAccount, "账号不能为空");

            // 先规范化：去首尾空格 + 全角转半角（中文输入法打的 １２３４５６ 会变成 123456）
            account = AccountNameRules.Normalize(account);

            if (account.Length < MinAccountLength || account.Length > MaxAccountLength)
                return RegisterResult.Fail(RegisterReasons.InvalidAccount,
                    $"账号长度需在 {MinAccountLength}~{MaxAccountLength} 个字符之间（当前 {account.Length} 个）");

            if (AccountNameRules.TryFindInvalidChar(account, out var invalid, out var index))
            {
                return RegisterResult.Fail(RegisterReasons.InvalidAccount,
                    $"账号的第 {index + 1} 个字符不合法：{AccountNameRules.DescribeChar(invalid)}。" +
                    "只能用半角字母、数字、下划线、中划线、点，且不能有空格");
            }

            if (password == null || password.Length < MinPasswordLength)
                return RegisterResult.Fail(RegisterReasons.WeakPassword, $"密码至少 {MinPasswordLength} 位");

            if (_store.Exists(account))
                return RegisterResult.Fail(RegisterReasons.AccountExists, "账号已存在");

            return null;
        }

        private RegisterResult CreateInternal(string account, string password, string playerId)
        {
            var record = new AccountRecord
            {
                Account = account,
                PasswordHash = PasswordHasher.Hash(password, _passwordIterations),
                PlayerId = string.IsNullOrEmpty(playerId) ? account : playerId,
                CreatedAtMs = NowMs(),
                Player = new PlayerData()
            };

            if (!_store.CreateAccount(record))
                return RegisterResult.Fail(RegisterReasons.Rejected, "写入存储失败");

            _logger.Info($"账号已注册：account='{account}'，playerId='{record.PlayerId}'");
            return RegisterResult.Ok(record.PlayerId);
        }

        /// <summary>删账号（连同等级/属性/道具一起删）。</summary>
        public bool Delete(string account)
        {
            account = AccountNameRules.Normalize(account);

            var removed = _store.DeleteAccount(account);
            if (removed) _logger.Info($"账号已删除：account='{account}'");
            return removed;
        }

        public bool ChangePassword(string account, string oldPassword, string newPassword, out string error)
        {
            error = null;
            account = AccountNameRules.Normalize(account);

            if (newPassword == null || newPassword.Length < 1) { error = "新密码不能为空"; return false; }
            if (!_store.TryGetAccount(account, out var record)) { error = "账号不存在"; return false; }
            if (!PasswordHasher.Verify(oldPassword, record.PasswordHash)) { error = "原密码错误"; return false; }

            record.PasswordHash = PasswordHasher.Hash(newPassword, _passwordIterations);
            if (!_store.UpdateAccount(record)) { error = "写入存储失败"; return false; }

            _logger.Info($"账号密码已修改：account='{account}'");
            return true;
        }

        /// <summary>管理员重置密码（不需要原密码）。</summary>
        public bool ResetPassword(string account, string newPassword, out string error)
        {
            error = null;
            account = AccountNameRules.Normalize(account);

            if (!_store.TryGetAccount(account, out var record)) { error = "账号不存在"; return false; }

            record.PasswordHash = PasswordHasher.Hash(newPassword, _passwordIterations);
            if (!_store.UpdateAccount(record)) { error = "写入存储失败"; return false; }
            return true;
        }

        public bool SetBanned(string account, bool banned)
        {
            account = AccountNameRules.Normalize(account);

            if (!_store.TryGetAccount(account, out var record)) return false;

            record.Banned = banned;
            return _store.UpdateAccount(record);
        }

        // ---------------- 登录校验 ----------------

        public LoginResponse Validate(LoginRequest request, IMessagePeer peer)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Account))
                return LoginProtocols.Failure(LoginReasons.InvalidCredentials, "账号不能为空");

            // 登录也做同样的规范化：用全角数字/带空格输入也能登进同一个账号
            var account = AccountNameRules.Normalize(request.Account);

            if (!_store.TryGetAccount(account, out var record))
            {
                if (!AutoRegisterOnLogin) return LoginProtocols.Failure(LoginReasons.InvalidCredentials, "账号或密码错误");

                if (!Register(account, request.Password, out var registerError))
                    return LoginProtocols.Failure(LoginReasons.Rejected, registerError);

                if (!_store.TryGetAccount(account, out record))
                    return LoginProtocols.Failure(LoginReasons.Rejected, "注册后读取失败");
            }
            else if (!PasswordHasher.Verify(request.Password, record.PasswordHash))
            {
                // 账号不存在和密码错误返回同一句话，避免暴露账号是否存在
                return LoginProtocols.Failure(LoginReasons.InvalidCredentials, "账号或密码错误");
            }

            if (record.Banned)
                return LoginProtocols.Failure(LoginReasons.Rejected, "账号已被封禁");

            record.LastLoginMs = NowMs();
            _store.UpdateAccount(record);

            return LoginProtocols.Success(record.PlayerId);
        }

        // ---------------- 玩家数据（等级/属性/金币）----------------

        public PlayerData GetPlayerData(string account)
            => _store.TryGetPlayerData(AccountNameRules.Normalize(account), out var data) ? data : null;

        public bool SetLevel(string account, int level)
            => Modify(account, data => data.Level = level);

        public bool AddExp(string account, long exp)
            => Modify(account, data => data.Exp = Math.Max(0, data.Exp + exp));

        public bool AddGold(string account, long gold)
            => Modify(account, data => data.Gold = Math.Max(0, data.Gold + gold));

        /// <summary>设置一个属性（力量/敏捷/智力…属性名随便起）。</summary>
        public bool SetAttribute(string account, string name, long value)
            => Modify(account, data => data.Attributes[name] = value);

        public bool AddAttribute(string account, string name, long delta)
            => Modify(account, data =>
            {
                data.Attributes.TryGetValue(name, out var current);
                data.Attributes[name] = Math.Max(0, current + delta);
            });

        public bool RemoveAttribute(string account, string name)
            => Modify(account, data => data.Attributes.Remove(name));

        // ---------------- 道具增删改查 ----------------

        public IReadOnlyList<ItemStack> GetItems(string account)
            => _store.GetItems(AccountNameRules.Normalize(account));

        /// <summary>加道具。</summary>
        public bool AddItem(string account, string itemId, int count = 1, string extraJson = null)
            => _store.AddItem(AccountNameRules.Normalize(account), itemId, count, extraJson);

        /// <summary>扣道具（数量不足返回 false）。</summary>
        public bool RemoveItem(string account, string itemId, int count = 1)
            => _store.RemoveItem(AccountNameRules.Normalize(account), itemId, count);

        /// <summary>把道具数量设成指定值（0 = 删掉这格）。</summary>
        public bool SetItemCount(string account, string itemId, int count, string extraJson = null)
            => _store.SetItemCount(AccountNameRules.Normalize(account), itemId, count, extraJson);

        /// <summary>查某个道具当前数量（没有就是 0）。</summary>
        public int GetItemCount(string account, string itemId)
        {
            foreach (var item in _store.GetItems(AccountNameRules.Normalize(account)))
            {
                if (string.Equals(item.ItemId, itemId, StringComparison.Ordinal)) return item.Count;
            }
            return 0;
        }

        /// <summary>原子地"扣掉 A 给 B"这类操作（先扣后加，扣失败就整体失败）。</summary>
        public bool Trade(string fromAccount, string toAccount, string itemId, int count = 1)
        {
            if (!RemoveItem(fromAccount, itemId, count)) return false;
            if (AddItem(toAccount, itemId, count)) return true;

            AddItem(fromAccount, itemId, count);   // 回滚
            return false;
        }

        // ---------------- 内部 ----------------

        private bool Modify(string account, Action<PlayerData> change)
        {
            account = AccountNameRules.Normalize(account);

            if (string.IsNullOrEmpty(account) || change == null) return false;
            if (!_store.TryGetPlayerData(account, out var data) || data == null) return false;

            change(data);
            return _store.SavePlayerData(account, data);
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
