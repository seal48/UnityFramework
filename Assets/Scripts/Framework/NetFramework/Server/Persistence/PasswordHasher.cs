using System;
using System.Security.Cryptography;

namespace GameFramework.Net.Server.Persistence
{
    /// <summary>
    /// 密码哈希：PBKDF2-SHA256，存的是 "pbkdf2$sha256$迭代次数$盐$哈希"，不存明文。
    /// 校验用固定时间比较，避免时序侧信道。
    /// </summary>
    public static class PasswordHasher
    {
        private const int SaltBytes = 16;
        private const int KeyBytes = 32;
        private const int DefaultIterations = 100000;

        public static string Hash(string password, int iterations = DefaultIterations)
        {
            if (password == null) password = string.Empty;
            if (iterations < 1000) iterations = 1000;

            var salt = new byte[SaltBytes];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }

            var key = Derive(password, salt, iterations);
            return $"pbkdf2$sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
        }

        /// <summary>校验密码；哈希串格式不对/为空时一律返回 false。</summary>
        public static bool Verify(string password, string storedHash)
        {
            if (string.IsNullOrEmpty(storedHash)) return false;

            var parts = storedHash.Split('$');
            if (parts.Length != 5 || parts[0] != "pbkdf2") return false;
            if (!int.TryParse(parts[2], out var iterations) || iterations < 1) return false;

            byte[] salt;
            byte[] expected;
            try
            {
                salt = Convert.FromBase64String(parts[3]);
                expected = Convert.FromBase64String(parts[4]);
            }
            catch (FormatException)
            {
                return false;
            }

            var actual = Derive(password ?? string.Empty, salt, iterations);
            return FixedTimeEquals(actual, expected);
        }

        private static byte[] Derive(string password, byte[] salt, int iterations)
        {
            using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
            {
                return pbkdf2.GetBytes(KeyBytes);
            }
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;

            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
