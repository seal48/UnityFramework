using System;
using Newtonsoft.Json;

namespace GameFramework.Storage
{
    /// <summary>
    /// 账号 / 设备信息（account.json）。
    ///
    /// 分两类：
    ///   设备级 —— DeviceId、首次启动时间、协议是否同意：跟设备走，不跟账号走；
    ///   会话级 —— token、上次账号、上次连的服务器：登录后写，退出/失效时清；
    ///   记住密码 —— PasswordCipher：设备级「记住我」，登录成功后写，退出登录时清。
    ///
    /// Token 和记住的密码落盘时都走 LocalSecret 混淆，明文只存在内存里。
    /// </summary>
    [Serializable]
    public sealed class LocalAccount
    {
        // ---- 设备级 ----

        /// <summary>首次启动生成的安装 ID。卸载重装会变，别拿它当账号 ID 用。</summary>
        public string DeviceId = string.Empty;

        /// <summary>首次启动时间（Unix 秒）。</summary>
        public long FirstLaunchUnixSeconds = 0;

        /// <summary>用户是否已经同意隐私协议 / 用户协议。Android 上必须在初始化任何 SDK 之前就能同步读到。</summary>
        public bool AgreementsAccepted = false;

        /// <summary>已同意的协议版本号。协议改版时 +1，用来重新弹一次。</summary>
        public int AgreementsVersion = 0;

        // ---- 会话级 ----

        /// <summary>上次登录的账号名，用来在登录界面预填。</summary>
        public string LastAccount = string.Empty;

        /// <summary>服务端下发的玩家 ID。</summary>
        public string PlayerId = string.Empty;

        /// <summary>上次连上的服务器地址。主站不可用时可以用它兜底。</summary>
        public string ServerHost = string.Empty;

        /// <summary>上次选的服 ID（ServerList 表主键）。0 = 没选过（用推荐服）。设备级，跟设备走。</summary>
        public int LastServerId = 0;

        /// <summary>token 过期时间（Unix 秒）。0 = 服务端没给过期时间，当作不过期。</summary>
        public long TokenExpireUnixSeconds = 0;

        /// <summary>落盘用的 token 密文。别直接用它，用 Token。</summary>
        public string TokenCipher = string.Empty;

        /// <summary>明文 token（只在内存里）。写入时自动混淆，读取时自动还原。</summary>
        [JsonIgnore]
        public string Token
        {
            get { return LocalSecret.Reveal(TokenCipher); }
            set { TokenCipher = LocalSecret.Protect(value); }
        }

        /// <summary>记住密码用的密文（同 token 一样走 LocalSecret 混淆，不是加密，见 StorageFramework README）。</summary>
        public string PasswordCipher = string.Empty;

        /// <summary>明文密码（只在内存里）。写入时自动混淆，读取时自动还原。</summary>
        [JsonIgnore]
        public string Password
        {
            get { return LocalSecret.Reveal(PasswordCipher); }
            set { PasswordCipher = LocalSecret.Protect(value); }
        }

        /// <summary>是否已记住密码。</summary>
        public bool HasStoredPassword()
        {
            return !string.IsNullOrEmpty(PasswordCipher);
        }

        /// <summary>清掉记住的密码（退出登录时调用）。只清密码，不影响账号名 / token。</summary>
        public void ClearRememberedPassword()
        {
            PasswordCipher = string.Empty;
        }

        /// <summary>缓存里的 token 现在还能用吗（有 token，且没过期）。</summary>
        public bool HasUsableToken()
        {
            if (string.IsNullOrEmpty(TokenCipher)) return false;
            if (TokenExpireUnixSeconds <= 0) return true;

            return LocalStorageManager.NowUnixSeconds() < TokenExpireUnixSeconds;
        }

        /// <summary>清掉会话级信息（退出登录、token 失效、被踢下线）。设备级信息保留。</summary>
        public void ClearSession()
        {
            TokenCipher = string.Empty;
            TokenExpireUnixSeconds = 0;
            PlayerId = string.Empty;
        }
    }
}