using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GameFramework.Security
{
    /// <summary>
    /// 实用级对称加密：AES-128-CBC + PKCS7 + HMAC-SHA256 完整性校验（encrypt-then-MAC）。
    ///
    /// 纯 C#（System.Security.Cryptography），**不依赖 UnityEngine** ——
    /// 所以前后端（NetFramework 协议）、客户端（存档）共用同一份代码。
    ///
    /// ⚠️ 安全边界（务必读）：
    ///   这是「防君子不防贼」的实用级方案。密钥以拆分形式硬编码在包里，
    ///   能挡住：改 JSON 存档、抓包看明文、非专业玩家改数据。
    ///   挡不住：逆向高手（IDA / Frida 解出密钥）。真正的强安全需要
    ///   服务端下发会话密钥 / 加固 / 混淆，那是更高成本的方案。
    ///
    /// 格式：Magic(5) + IV(16) + AES密文 + HMAC-SHA256(32)。
    ///   - HMAC 覆盖 IV + 密文（encrypt-then-MAC），任何位置的 1 字节篡改
    ///     都会导致校验失败返回 null —— 不像裸 CBC 那样看篡改位置碰运气。
    ///   - 密钥策略：主密钥拆成几段运行时拼（+ 一次简单的运算混淆），
    ///     让「字符串搜密钥」搜不到完整明文。这只是提高门槛，不是加密密钥本身。
    /// </summary>
    public static class SymmetricCrypto
    {
        /// <summary>密文头：用于区分「已加密」和「未加密/已损坏」的数据。</summary>
        public static readonly byte[] Magic = { 0x4D, 0x43, 0x53, 0x45, 0x02 }; // "MCSE" + 版本2（v2 起带 HMAC）

        /// <summary>固定头部长度：Magic + IV。</summary>
        public const int HeaderLength = 5 + 16;

        /// <summary>HMAC 长度（SHA-256）。</summary>
        public const int MacLength = 32;

        private static readonly byte[] Key = BuildKey();

        /// <summary>
        /// 密钥（128 bit = 16 字节）。拆段 + 运算拼出来，避免包里直接搜到整段密钥。
        /// 各端保持同一份（前后端约定一致才能互解）。
        /// </summary>
        private static byte[] BuildKey()
        {
            // 三段拼接：K1 ^ K2 = 中间量，再拼 K3
            byte[] k1 = Encoding.UTF8.GetBytes("MCP-FW-SEC");
            byte[] k2 = Encoding.UTF8.GetBytes("2026-GAME!");
            byte[] k3 = Encoding.UTF8.GetBytes("!CN-GAME-26");

            byte[] head = new byte[16];
            for (int i = 0; i < 16; i++)
                head[i] = (byte)(k1[i % k1.Length] ^ k2[i % k2.Length]);

            byte[] full = new byte[16];
            for (int i = 0; i < 16; i++)
                full[i] = (byte)(head[i] ^ k3[i % k3.Length]);

            return full;
        }

        /// <summary>加密。返回「Magic + IV + 密文」。</summary>
        /// <summary>
        /// 加密。返回「Magic + IV + AES密文 + HMAC(IV+密文)」。
        /// HMAC 用同一个主密钥派生（encrypt-then-MAC），保证任何位置篡改都会被检出。
        /// </summary>
        public static byte[] Encrypt(byte[] plain)
        {
            if (plain == null) plain = new byte[0];

            using (var aes = Aes.Create())
            {
                aes.KeySize = 128;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = Key;
                aes.GenerateIV();

                byte[] cipher;
                using (var enc = aes.CreateEncryptor())
                using (var ms = new MemoryStream())
                {
                    using (var cs = new CryptoStream(ms, enc, CryptoStreamMode.Write))
                    {
                        cs.Write(plain, 0, plain.Length);
                    }
                    cipher = ms.ToArray();
                }

                // 计算 HMAC：覆盖 IV + 密文（防篡改，含中间块）
                byte[] mac = ComputeMac(aes.IV, cipher);

                // 拼装：Magic + IV + 密文 + HMAC
                byte[] result = new byte[HeaderLength + cipher.Length + MacLength];
                Buffer.BlockCopy(Magic, 0, result, 0, Magic.Length);
                Buffer.BlockCopy(aes.IV, 0, result, Magic.Length, 16);
                Buffer.BlockCopy(cipher, 0, result, HeaderLength, cipher.Length);
                Buffer.BlockCopy(mac, 0, result, HeaderLength + cipher.Length, MacLength);
                return result;
            }
        }

        /// <summary>
        /// 解密。成功返回明文；头不是 Magic、HMAC 不匹配（被改过/损坏/密钥不对）返回 null，调用方按损坏处理。
        /// </summary>
        public static byte[] Decrypt(byte[] data)
        {
            if (data == null || data.Length < HeaderLength + MacLength)
                return null;

            // 头校验：不是我们的密文头，直接判失败（防止拿明文/别家数据硬解）
            for (int i = 0; i < Magic.Length; i++)
            {
                if (data[i] != Magic[i])
                    return null;
            }

            byte[] iv = new byte[16];
            Buffer.BlockCopy(data, Magic.Length, iv, 0, 16);
            int cipherLength = data.Length - HeaderLength - MacLength;
            if (cipherLength <= 0)
                return null;

            // 先验 HMAC：任何位置篡改（含中间块）都会在这里失败，确定性拦截
            byte[] expectedMac = new byte[MacLength];
            Buffer.BlockCopy(data, HeaderLength + cipherLength, expectedMac, 0, MacLength);
            byte[] actualMac = ComputeMac(iv, data, HeaderLength, cipherLength);
            if (!FixedTimeEquals(actualMac, expectedMac))
                return null;

            try
            {
                using (var aes = Aes.Create())
                {
                    aes.KeySize = 128;
                    aes.BlockSize = 128;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = Key;

                    using (var dec = aes.CreateDecryptor(aes.Key, iv))
                    using (var ms = new MemoryStream(data, HeaderLength, cipherLength))
                    using (var cs = new CryptoStream(ms, dec, CryptoStreamMode.Read))
                    using (var outMs = new MemoryStream())
                    {
                        cs.CopyTo(outMs);
                        return outMs.ToArray();
                    }
                }
            }
            catch (CryptographicException)
            {
                // 填充异常（密钥不对 / 数据被改）→ 解密失败
                return null;
            }
        }

        /// <summary>HMAC-SHA256：覆盖「iv + cipher」两段（避免拼大数组）。</summary>
        private static byte[] ComputeMac(byte[] iv, byte[] cipher)
        {
            using (var hmac = new HMACSHA256(Key))
            {
                hmac.TransformBlock(iv, 0, iv.Length, null, 0);
                hmac.TransformFinalBlock(cipher, 0, cipher.Length);
                return hmac.Hash;
            }
        }

        /// <summary>HMAC-SHA256：覆盖「iv + data 的 [offset, offset+length) 区间」。</summary>
        private static byte[] ComputeMac(byte[] iv, byte[] data, int offset, int length)
        {
            using (var hmac = new HMACSHA256(Key))
            {
                hmac.TransformBlock(iv, 0, iv.Length, null, 0);
                hmac.TransformFinalBlock(data, offset, length);
                return hmac.Hash;
            }
        }

        /// <summary>常量时间比较，避免时序侧信道（实用级：主要防误用，非对抗级）。</summary>
        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>便捷重载：string 进 string 出（UTF-8）。</summary>
        public static string EncryptString(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return string.Empty;
            return Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(plain)));
        }

        /// <summary>便捷重载：解密 string，失败返回 null。</summary>
        public static string DecryptString(string encrypted)
        {
            if (string.IsNullOrEmpty(encrypted)) return null;
            try
            {
                byte[] plain = Decrypt(Convert.FromBase64String(encrypted));
                return plain == null ? null : Encoding.UTF8.GetString(plain);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
