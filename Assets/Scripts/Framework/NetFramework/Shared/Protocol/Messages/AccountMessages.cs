using System;

namespace GameFramework.Net.Protocol
{
    // ================= 注册 =================

    /// <summary>register.request 的消息内容：客户端申请注册账号。</summary>
    [Serializable]
    public class RegisterRequest
    {
        public string Account;
        public string Password;

        /// <summary>客户端想用的玩家名（可空，服务器不填就用账号名）。</summary>
        public string PlayerName;

        public string ClientVersion;
        public string ClientPlatform;
        public long ClientTimeMs;
    }

    /// <summary>register.response 的消息内容：注册结果。</summary>
    [Serializable]
    public class RegisterResponse
    {
        public bool Success;

        /// <summary>失败原因（见 RegisterReasons 里的常量），成功时为 null。</summary>
        public string Reason;

        /// <summary>失败时给玩家看的说明文本。</summary>
        public string Message;

        /// <summary>注册成功后分配的 playerId（可以直接拿去登录）。</summary>
        public string PlayerId;

        public string ServerName;
        public int ProtocolVersion;
        public long ServerTimeMs;
    }

    /// <summary>注册失败原因常量。</summary>
    public static class RegisterReasons
    {
        public const string InvalidAccount = "invalid_account";
        public const string WeakPassword = "weak_password";
        public const string AccountExists = "account_exists";
        public const string RegisterClosed = "register_closed";
        public const string Rejected = "rejected";
    }

    // ================= 登录 =================

    /// <summary>login.request 的消息内容：客户端发起登录。</summary>
    [Serializable]
    public class LoginRequest
    {
        public string Account;
        public string Password;
        public string ClientVersion;
        public string ClientPlatform;
        public long ClientTimeMs;
    }

    /// <summary>login.response 的消息内容：服务器返回登录结果。</summary>
    [Serializable]
    public class LoginResponse
    {
        /// <summary>是否登录成功。</summary>
        public bool Success;

        /// <summary>失败原因（见 LoginReasons 里的常量），成功时为 null。</summary>
        public string Reason;

        /// <summary>失败时给玩家看的说明文本。</summary>
        public string Message;

        /// <summary>登录成功后分配的唯一玩家 id。</summary>
        public string PlayerId;

        /// <summary>本次连接的会话 id（断线重连/顶号时用来区分）。</summary>
        public string SessionId;

        public string ServerName;
        public int ProtocolVersion;
        public long ServerTimeMs;
    }

    /// <summary>登录失败原因常量，方便前后端用同一套字符串做判断/本地化。</summary>
    public static class LoginReasons
    {
        public const string InvalidCredentials = "invalid_credentials";
        public const string AlreadyLoggedIn = "already_logged_in";
        public const string ServerFull = "server_full";
        public const string Rejected = "rejected";
    }
}
