using System;
using System.Text;

namespace GameFramework.Storage
{
    /// <summary>
    /// 存档里敏感字段的一层混淆。**它不是加密**：密钥就在包里，真想改存档的人拦不住。
    ///
    /// 它只解决两件小事：
    ///   1. 存档文件用文本编辑器打开时看不到明文 token，免得被截图 / 贴群里；
    ///   2. 手动改过的值一定解不出来（会变成空），不会出现「改一半还能登」的迷惑情况。
    ///
    /// 真正的安全来自服务端：token 要有有效期，改密码 / 封号 / 换设备要能让旧 token 立刻失效。
    /// </summary>
    internal static class LocalSecret
    {
        private const string Key = "GameFramework.Storage.v1";
        private static readonly byte[] KeyBytes = Encoding.UTF8.GetBytes(Key);

        /// <summary>明文 → 落盘字符串。空字符串原样返回。</summary>
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return string.Empty;

            byte[] raw = Encoding.UTF8.GetBytes(plain);
            for (int i = 0; i < raw.Length; i++)
                raw[i] = (byte)(raw[i] ^ KeyBytes[i % KeyBytes.Length] ^ (i & 0xFF));

            return Convert.ToBase64String(raw);
        }

        /// <summary>落盘字符串 → 明文。解不出来（被人改过）就返回空字符串。</summary>
        public static string Reveal(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return string.Empty;

            try
            {
                byte[] raw = Convert.FromBase64String(stored);
                for (int i = 0; i < raw.Length; i++)
                    raw[i] = (byte)(raw[i] ^ KeyBytes[i % KeyBytes.Length] ^ (i & 0xFF));

                return Encoding.UTF8.GetString(raw);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}