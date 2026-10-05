using System;
using System.Collections.Generic;
using GameFramework.Core;
using GameFramework.Resource;
using UnityEngine;

namespace GameFramework.Config
{
    /// <summary>
    /// 配置表加载器（Unity 侧），由 GameController 初始化。
    ///
    /// 它只负责「把字节读出来」：客户端走资源系统读 Assets/ConfigData/XXX.bytes；
    /// 服务端用 Runtime/ConfigFileLoader 从磁盘读同一个文件，两边共用同一份生成代码。
    /// 真正的解析在生成的 TbXXX.Read 里（ConfigReader 会校验表结构哈希）。
    /// </summary>
    public sealed class ConfigManager : IGameModule, IDisposable
    {
        /// <summary>配置表数据在资源系统里的地址前缀（收集器关了 Addressable，所以直接用资源路径）。</summary>
        public const string DataAssetPrefix = "Assets/ConfigData/";

        private readonly ConfigDatabase database = new ConfigDatabase();
        private readonly Dictionary<string, ResourceAsset<TextAsset>> assets =
            new Dictionary<string, ResourceAsset<TextAsset>>(StringComparer.Ordinal);

        private IResourceService resource;
        private Action<bool, string> pending;
        private string firstError;
        private int expected;
        private int completed;
        private bool loading;
        private bool disposed;

        /// <summary>配置表集合。业务代码：GameController.Instance.Config.Database.Item.Get(id)。</summary>
        public ConfigDatabase Database { get { return database; } }

        public bool IsLoaded { get { return database.IsLoaded; } }

        /// <summary>是否已经初始化完成（配置表读完了）。</summary>
        public bool IsInitialized { get { return database.IsLoaded; } }

        /// <summary>加载全部表；全部读完（或出错）后回调 onComplete(是否成功, 说明)。</summary>
        public void Init(IResourceService resourceService, Action<bool, string> onComplete)
        {
            if (disposed) throw new ObjectDisposedException(GetType().Name);
            if (resourceService == null) throw new ArgumentNullException(nameof(resourceService));

            if (database.IsLoaded)
            {
                if (onComplete != null) onComplete(true, "配置表已加载");
                return;
            }

            resource = resourceService;
            pending = onComplete;
            firstError = null;
            completed = 0;

            string[] names = ConfigDatabase.TableNames;
            expected = names == null ? 0 : names.Length;

            if (expected == 0)
            {
                CompleteLoad();
                return;
            }

            // 注意：加载回调可能是同步触发的，所以循环期间先不要判定"全部完成"
            loading = true;
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                resource.LoadAssetAsync<TextAsset>(DataAssetPrefix + name + ".bytes", handle => OnLoaded(name, handle));
            }
            loading = false;

            TryComplete();
        }

        private void OnLoaded(string name, ResourceAsset<TextAsset> handle)
        {
            if (handle != null && handle.Asset != null)
            {
                assets[name] = handle;
            }
            else if (firstError == null)
            {
                firstError = "资源里找不到配置表：" + DataAssetPrefix + name + ".bytes";
            }

            completed++;
            if (loading) return;

            TryComplete();
        }

        private void TryComplete()
        {
            if (completed < expected) return;
            if (pending == null) return;

            if (firstError != null)
            {
                Finish(false, firstError);
                return;
            }

            CompleteLoad();
        }

        private void CompleteLoad()
        {
            try
            {
                database.Load(ReadBytes);
                Finish(true, "配置表 " + database.LoadedTables.Count + " 张已就绪");
            }
            catch (Exception ex)
            {
                Finish(false, ex.Message);
            }
        }

        private byte[] ReadBytes(string name)
        {
            ResourceAsset<TextAsset> handle;
            if (!assets.TryGetValue(name, out handle) || handle == null || handle.Asset == null)
                throw new ConfigFormatException("[配置表] 缺少资源：" + DataAssetPrefix + name + ".bytes");

            return handle.Asset.bytes;
        }

        private void Finish(bool success, string message)
        {
            Action<bool, string> callback = pending;
            pending = null;
            if (callback != null) callback(success, message);
        }

        /// <summary>关闭并释放。幂等。</summary>
        public void Shutdown()
        {
            if (disposed) return;
            disposed = true;

            foreach (KeyValuePair<string, ResourceAsset<TextAsset>> pair in assets)
            {
                if (pair.Value != null) pair.Value.Dispose();
            }
            assets.Clear();

            database.Unload();
            resource = null;
            pending = null;
        }

        /// <summary>IDisposable 转发到 <see cref="Shutdown"/>，两种写法行为一致。</summary>
        public void Dispose() { Shutdown(); }
    }
}