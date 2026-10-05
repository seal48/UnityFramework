using System;
using System.Text;

namespace GameFramework.Net.Protocol
{
    /// <summary>
    /// 账号名的规范化与字符校验，前后端共用，保证"客户端输入的写法"和"服务器存的名字"一致。
    ///
    /// 会处理两类最常见的手滑：
    ///   1) 首尾空格（复制粘贴常见）—— 直接去掉；
    ///   2) 全角字符（中文输入法下打出的 １２３４５６ＡＢＣ）—— 转成半角 123456ABC。
    /// 这样玩家用中文输入法打"１２３４５６"，注册出来仍然是 "123456"，登录也照样能登。
    /// </summary>
    public static class AccountNameRules
    {
        /// <summary>去掉首尾空白，并把全角字符转成半角（Unicode NFKC）。</summary>
        public static string Normalize(string account)
        {
            if (string.IsNullOrEmpty(account)) return account;

            var converted = ToHalfWidth(account);
            var trimmed = converted.Trim();
            if (trimmed.Length == 0) return trimmed;

            try
            {
                // 再走一遍 NFKC（能处理全角以外的一些兼容字符）；不支持 ICU 的环境会抛异常，忽略即可
                return trimmed.Normalize(NormalizationForm.FormKC);
            }
            catch (Exception)
            {
                // 含非法代理对等异常字符时就不做规范化，交给后面的字符校验去报错
                return trimmed;
            }
        }

        /// <summary>
        /// 全角 → 半角。中文输入法下打出来的全角数字（１２３４５６）、全角字母、全角空格都在这里被修正。
        /// 手动转换而不是只依赖 string.Normalize：后端用 InvariantGlobalization 时 ICU 不可用，Normalize 会失效。
        /// </summary>
        public static string ToHalfWidth(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var chars = text.ToCharArray();
            var changed = false;

            for (var i = 0; i < chars.Length; i++)
            {
                var c = chars[i];

                if (c == '\u3000')                    // 全角空格
                {
                    chars[i] = ' ';
                    changed = true;
                }
                else if (c >= '\uFF01' && c <= '\uFF5E')   // 全角 ASCII（！ 到 ～）
                {
                    chars[i] = (char)(c - 0xFEE0);
                    changed = true;
                }
            }

            return changed ? new string(chars) : text;
        }

        /// <summary>允许的字符：半角字母、数字、下划线、中划线、点。</summary>
        public static bool IsAllowedChar(char c)
        {
            if (c < 128 && (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')) return true;
            return false;
        }

        /// <summary>找出第一个不合法字符。</summary>
        public static bool TryFindInvalidChar(string account, out char invalid, out int index)
        {
            invalid = '\0';
            index = -1;

            if (string.IsNullOrEmpty(account)) return false;

            for (var i = 0; i < account.Length; i++)
            {
                if (IsAllowedChar(account[i])) continue;

                invalid = account[i];
                index = i;
                return true;
            }

            return false;
        }

        /// <summary>把不合法字符描述成人能看懂的样子（空格/全角/码点）。</summary>
        public static string DescribeChar(char c)
        {
            if (char.IsWhiteSpace(c)) return $"空格（U+{(int)c:X4}）";
            if (c < 128) return $"{c}（U+{(int)c:X4}）";
            return $"'{c}'（U+{(int)c:X4}，可能是全角字符）";
        }
    }
}
