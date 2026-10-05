using System;
using System.Collections.Generic;

namespace GameFramework.Config
{
    /// <summary>
    /// 所有配置表的集合。本文件手写；各表的属性 + 读取逻辑由导出工具生成到
    /// Generated/ConfigDatabase.g.cs，所以"一张表都还没有"时这里也能编译通过。
    ///
    /// 客户端由 GameController 加载；服务端用同一个类，只是换一个 readFile 实现。
    /// </summary>
    public sealed partial class ConfigDatabase
    {
        private readonly List<object> loadedTables = new List<object>();

        /// <summary>当前已加载并生效的配置表，业务代码的快捷入口。</summary>
        public static ConfigDatabase Current { get; private set; }

        public bool IsLoaded { get; private set; }

        public IReadOnlyList<object> LoadedTables { get { return loadedTables; } }

        /// <summary>
        /// 加载全部表。readFile 传入表名（如 "Item"），返回对应 .bytes 的字节内容。
        /// 客户端传"从资源系统读"，服务端传"从磁盘读"，所以这个类本身不依赖 Unity。
        /// </summary>
        public void Load(Func<string, byte[]> readFile)
        {
            if (IsLoaded) throw new InvalidOperationException("[配置表] 已经加载过了，不要重复 Load。");
            if (readFile == null) throw new ArgumentNullException(nameof(readFile));

            LoadTables(readFile, loadedTables);
            IsLoaded = true;
            Current = this;
        }

        /// <summary>卸载（清空静态入口，重新加载时用）。</summary>
        public void Unload()
        {
            ClearTables();
            loadedTables.Clear();
            IsLoaded = false;
            if (ReferenceEquals(Current, this)) Current = null;
        }

        /// <summary>由生成代码实现：逐个读出各表并登记到 loaded。</summary>
        partial void LoadTables(Func<string, byte[]> readFile, List<object> loaded);

        /// <summary>由生成代码实现：把各表属性置空。</summary>
        partial void ClearTables();
    }
}