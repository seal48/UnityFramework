using System.Collections.Generic;

namespace GameFramework.Config
{
    /// <summary>
    /// 配置表基类：按主键建索引，同时保留 Excel 里的原始顺序。
    /// TKey 支持 int / long / string，TRow 是每张表生成出来的数据行类。
    /// </summary>
    public abstract class ConfigTable<TKey, TRow> where TRow : class
    {
        private readonly Dictionary<TKey, TRow> byKey = new Dictionary<TKey, TRow>();
        private readonly List<TRow> rows = new List<TRow>();

        /// <summary>按 Excel 里的顺序返回所有行。</summary>
        public IReadOnlyList<TRow> Rows { get { return rows; } }

        public int Count { get { return rows.Count; } }

        protected abstract TKey KeyOf(TRow row);

        protected void Add(TRow row)
        {
            byKey[KeyOf(row)] = row;
            rows.Add(row);
        }

        public bool Contains(TKey key) { return byKey.ContainsKey(key); }

        public bool TryGet(TKey key, out TRow row) { return byKey.TryGetValue(key, out row); }

        /// <summary>取不到返回 null。逻辑里优先用 TryGet，避免空引用。</summary>
        public TRow Get(TKey key)
        {
            TRow row;
            return byKey.TryGetValue(key, out row) ? row : null;
        }
    }
}