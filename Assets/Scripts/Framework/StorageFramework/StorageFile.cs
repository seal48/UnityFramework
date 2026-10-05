using GameFramework.Log;
using GameFramework.Security;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace GameFramework.Storage
{
    /// <summary>存档文件落盘时的外壳：结构版本号 + 数据本体。</summary>
    internal sealed class StorageEnvelope<T>
    {
        public int Version;
        public T Data;
    }

    /// <summary>存档 JSON 的序列化设置。和网络协议的格式分开：存档要能缩进、要给人看。</summary>
    internal static class StorageFileFormat
    {
        public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private static readonly JsonSerializerSettings Compact = Create(false);
        private static readonly JsonSerializerSettings Pretty = Create(true);

        public static JsonSerializerSettings Get(bool pretty)
        {
            return pretty ? Pretty : Compact;
        }

        private static JsonSerializerSettings Create(bool pretty)
        {
            return new JsonSerializerSettings
            {
                Formatting = pretty ? Formatting.Indented : Formatting.None,
                NullValueHandling = NullValueHandling.Ignore,
                DateTimeZoneHandling = DateTimeZoneHandling.Utc,
                Culture = System.Globalization.CultureInfo.InvariantCulture,
            };
        }
    }

    /// <summary>存档文件的非泛型视图。管理器用它统一记录 / 落盘，不关心具体存的是什么。</summary>
    public abstract class StorageFileBase
    {
        /// <summary>文件名，比如 settings.json。</summary>
        public abstract string FileName { get; }

        /// <summary>内存里的内容和磁盘上的不一致，还没落盘。</summary>
        public bool IsDirty { get; internal set; }

        /// <summary>磁盘上已经有这个文件。</summary>
        public bool ExistsOnDisk { get; internal set; }

        /// <summary>当前程序里的结构版本号。</summary>
        public abstract int Version { get; }

        /// <summary>这次读出来的存档是哪个版本的（全新建的 = 当前版本）。</summary>
        public int LoadedVersion { get; internal set; }

        /// <summary>已经读过盘了（或已经按默认值重建过）。</summary>
        public bool IsLoaded { get; internal set; }

        /// <summary>从磁盘读（文件不存在就按默认值）。自己 new 出来的存档文件，用管理器 Register 注册。</summary>
        public abstract void LoadFrom(string path, StorageOptions options);

        /// <summary>写回磁盘。先写 .tmp 再替换，中途被杀进程不会写坏正式文件。</summary>
        public abstract void WriteTo(string path, StorageOptions options);

        /// <summary>恢复成默认值并标记待落盘。</summary>
        public abstract void ResetToDefault();
    }

    /// <summary>
    /// 一个有版本号的 JSON 存档文件。
    ///
    /// 落盘格式：{ "Version": 2, "Data": { ... } }
    /// 版本号是给自己留的后路 —— 以后改了数据结构，加一条 AddMigration 就能把老玩家的存档升上来，
    /// 不用让玩家清档。没有迁移可走的旧版本会当成损坏处理（备份 + 重建）。
    ///
    /// 写盘是「先写 .tmp 再替换」，中途进程被杀也只会留下一个临时文件，正式文件不会写坏。
    /// </summary>
    public sealed class StorageFile<T> : StorageFileBase where T : class, new()
    {
        private readonly Dictionary<int, Action<T>> migrations = new Dictionary<int, Action<T>>();
        private readonly string fileName;
        private readonly int version;

        private T data;

        /// <param name="fileName">文件名，比如 settings.json。</param>
        /// <param name="version">当前结构版本号，从 1 开始。改结构就 +1 并补一条迁移。</param>
        public StorageFile(string fileName, int version = 1)
        {
            if (string.IsNullOrEmpty(fileName)) throw new ArgumentException("存档文件名不能为空", nameof(fileName));
            if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));

            this.fileName = fileName;
            this.version = version;
            data = new T();
        }

        public override string FileName { get { return fileName; } }
        public override int Version { get { return version; } }

        /// <summary>存档内容。改了字段之后要调 MarkDirty()，或者用管理器的 Save。</summary>
        public T Data { get { return data; } }

        /// <summary>
        /// 注册一条迁移：把 fromVersion 版的存档升到 fromVersion + 1 版。
        /// 例：v2 新增了 VolumeBgm 字段，就写 AddMigration(1, d => d.VolumeBgm = 1f)。
        /// </summary>
        public void AddMigration(int fromVersion, Action<T> migrate)
        {
            if (migrate == null) return;

            migrations[fromVersion] = migrate;
        }

        /// <summary>标记「内存里的数据变了，需要落盘」。</summary>
        public void MarkDirty()
        {
            IsDirty = true;
        }

        public override void ResetToDefault()
        {
            data = new T();
            LoadedVersion = version;
            IsLoaded = true;
            IsDirty = true;
        }

        public override void LoadFrom(string path, StorageOptions options)
        {
            ExistsOnDisk = File.Exists(path);

            if (!ExistsOnDisk)
            {
                // 第一次跑：先用默认值，等落盘的时候把文件建出来
                ResetToDefault();
                return;
            }

            string text;

            try
            {
                byte[] bytes = File.ReadAllBytes(path);

                // 开加密：先解密。解密失败（被改过 / 明文老存档 / 损坏）→ 按损坏重建
                if (options != null && options.EnableEncryption)
                {
                    byte[] plain = SymmetricCrypto.Decrypt(bytes);
                    if (plain == null)
                    {
                        Rebuild(path, options, "存档解密失败（可能是明文旧档 / 被改过 / 损坏）");
                        return;
                    }
                    text = Encoding.UTF8.GetString(plain);
                }
                else
                {
                    text = Encoding.UTF8.GetString(bytes);
                }
            }
            catch (Exception ex)
            {
                Rebuild(path, options, "读不出来（" + ex.GetType().Name + "）");
                return;
            }

            StorageEnvelope<T> envelope;

            try
            {
                envelope = JsonConvert.DeserializeObject<StorageEnvelope<T>>(text, StorageFileFormat.Get(options.PrettyJson));
            }
            catch (Exception ex)
            {
                Rebuild(path, options, "JSON 解析失败（" + ex.Message + "）");
                return;
            }

            if (envelope == null || envelope.Data == null)
            {
                Rebuild(path, options, "文件是空的，或者结构对不上");
                return;
            }

            if (envelope.Version > version)
            {
                // 玩家装过更新的版本，又装回了老版本：不敢猜结构，只能重建
                Rebuild(path, options, "存档版本 " + envelope.Version + " 比当前程序（" + version + "）还新");
                return;
            }

            if (envelope.Version < 1)
            {
                Rebuild(path, options, "存档版本号非法（" + envelope.Version + "）");
                return;
            }

            // 老存档逐级升上来
            for (int v = envelope.Version; v < version; v++)
            {
                Action<T> migrate;

                if (!migrations.TryGetValue(v, out migrate) || migrate == null)
                {
                    Rebuild(path, options, "缺少 v" + v + " → v" + (v + 1) + " 的迁移");
                    return;
                }

                try
                {
                    migrate(envelope.Data);
                }
                catch (Exception ex)
                {
                    Rebuild(path, options, "迁移 v" + v + " 失败（" + ex.Message + "）");
                    return;
                }
            }

            data = envelope.Data;
            LoadedVersion = envelope.Version;
            IsLoaded = true;

            // 升级过的存档要按新版本写回去
            IsDirty = envelope.Version != version;
        }

        public override void WriteTo(string path, StorageOptions options)
        {
            StorageEnvelope<T> envelope = new StorageEnvelope<T>();
            envelope.Version = version;
            envelope.Data = data;

            string json = JsonConvert.SerializeObject(envelope, StorageFileFormat.Get(options.PrettyJson));

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string tempPath = path + ".tmp";

            // 开加密：写「加密字节」；关加密：写明文（调试用）
            if (options != null && options.EnableEncryption)
            {
                byte[] cipher = SymmetricCrypto.Encrypt(Encoding.UTF8.GetBytes(json));
                File.WriteAllBytes(tempPath, cipher);
            }
            else
            {
                File.WriteAllText(tempPath, json, StorageFileFormat.Utf8NoBom);
            }

            ReplaceFile(tempPath, path);

            IsDirty = false;
            ExistsOnDisk = true;
        }

        /// <summary>用一个完整的新文件替换旧文件。Replace 在个别平台上不可用，那就退化成「先删再移」。</summary>
        private static void ReplaceFile(string tempPath, string targetPath)
        {
            if (!File.Exists(targetPath))
            {
                File.Move(tempPath, targetPath);
                return;
            }

            try
            {
                File.Replace(tempPath, targetPath, null);
            }
            catch (Exception)
            {
                File.Delete(targetPath);
                File.Move(tempPath, targetPath);
            }
        }

        /// <summary>文件用不了：备份一份 .bad 留证，然后按默认值重建，别让玩家卡在启动界面。</summary>
        private void Rebuild(string path, StorageOptions options, string reason)
        {
            GameLog.Error(LogTag.Storage, "存档 " + fileName + " 无法使用（" + reason + "），已按默认值重建：" + path);

            if (options != null && options.BackupCorruptFile)
            {
                try
                {
                    File.Copy(path, path + ".bad", true);
                }
                catch (Exception)
                {
                    // 备份失败不影响重建，忽略
                }
            }

            ResetToDefault();
        }
    }
}