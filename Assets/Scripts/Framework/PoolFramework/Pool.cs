using GameFramework.Log;
using System;
using System.Collections.Generic;

namespace GameFramework.Pool
{
    /// <summary>
    /// 通用对象池（纯 C# 逻辑，只用 Debug 打日志，完全不碰 GameObject）。适合池化 List / 消息对象 / 树节点这类普通类实例，
    /// 省掉频繁 new + GC。
    ///
    /// GameObject 不要用这个 —— 用 ObjectPoolManager，它多管了父子节点、激活状态那些 Unity 概念。
    ///
    /// 用法：
    ///   Pool&lt;List&lt;int&gt;&gt; pool = new Pool&lt;List&lt;int&gt;&gt;(
    ///       () =&gt; new List&lt;int&gt;(),
    ///       onRelease: list =&gt; list.Clear());
    ///
    ///   List&lt;int&gt; list = pool.Get();
    ///   pool.Release(list);
    ///
    /// T 实现 IPoolableItem 时会自动收到 OnPoolGet / OnPoolRelease 回调。
    /// </summary>
    public sealed class Pool<T> where T : class
    {
        private readonly Stack<T> items = new Stack<T>();
        private readonly HashSet<T> alive = new HashSet<T>(ReferenceComparer<T>.Instance);
        private readonly Func<T> create;
        private readonly Action<T> onGet;
        private readonly Action<T> onRelease;
        private readonly Action<T> onDestroy;
        private readonly int maxSize;
        private readonly bool collectionCheck;

        /// <param name="create">没有空闲实例时怎么造一个新的。不能为空。</param>
        /// <param name="onRelease">归还时调用（清空集合、重置字段）。</param>
        /// <param name="onDestroy">真的销毁时调用（释放句柄）。</param>
        /// <param name="onGet">取出时调用。</param>
        /// <param name="maxSize">池里最多留多少个空闲实例，0 = 不限制。</param>
        /// <param name="collectionCheck">检测重复归还，发现时打日志。</param>
        public Pool(Func<T> create, Action<T> onRelease = null, Action<T> onDestroy = null, Action<T> onGet = null,
            int maxSize = 64, bool collectionCheck = true)
        {
            if (create == null) throw new ArgumentNullException(nameof(create));

            this.create = create;
            this.onRelease = onRelease;
            this.onDestroy = onDestroy;
            this.onGet = onGet;
            this.maxSize = maxSize < 0 ? 0 : maxSize;
            this.collectionCheck = collectionCheck;
        }

        /// <summary>池里空闲的实例数。</summary>
        public int CountInPool { get { return items.Count; } }

        /// <summary>已取出未归还的实例数。</summary>
        public int CountAlive { get { return alive.Count; } }

        /// <summary>历史上创建过多少个实例。</summary>
        public int TotalCreated { get; private set; }

        /// <summary>池里最多保留多少个空闲实例，0 = 不限制。</summary>
        public int MaxSize { get { return maxSize; } }

        /// <summary>取一个实例。池空了就按 create 造一个新的。</summary>
        public T Get()
        {
            T item = null;

            // 用户可能往池里塞过 null（外部直接改过集合），保险起见跳过
            while (items.Count > 0)
            {
                T candidate = items.Pop();
                if (candidate == null) continue;

                item = candidate;
                break;
            }

            if (item == null)
                item = Create();

            alive.Add(item);

            IPoolableItem poolable = item as IPoolableItem;
            if (poolable != null) poolable.OnPoolGet();
            if (onGet != null) onGet(item);

            return item;
        }

        /// <summary>归还一个实例。重复归还会被拦下（collectionCheck 打开时）。</summary>
        public bool Release(T item)
        {
            if (item == null) return false;

            if (!alive.Remove(item))
            {
                if (collectionCheck)
                    GameLog.Error(LogTag.Pool, "同一个实例被归还了两次，或者归还到了错误的池：" + item);
                return false;
            }

            IPoolableItem poolable = item as IPoolableItem;
            if (poolable != null) poolable.OnPoolRelease();
            if (onRelease != null) onRelease(item);

            if (maxSize > 0 && items.Count >= maxSize)
            {
                Destroy(item);
                return true;
            }

            items.Push(item);
            return true;
        }

        /// <summary>预热：先造好 count 个放回池里，避免第一次用的时候卡一下。</summary>
        public void Prewarm(int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (maxSize > 0 && items.Count >= maxSize) return;
                items.Push(Create());
            }
        }

        /// <summary>回收池里的空闲实例，一次最多销毁 count 个（-1 = 全清）。</summary>
        public void Shrink(int count = -1)
        {
            int limit = count < 0 ? items.Count : Math.Min(count, items.Count);
            for (int i = 0; i < limit; i++)
                Destroy(items.Pop());
        }

        /// <summary>清空池里空闲的实例（已取出的不动）。</summary>
        public void Clear()
        {
            Shrink(-1);
        }

        public override string ToString()
        {
            return "Pool<" + typeof(T).Name + "> 空闲 " + items.Count + "，存活 " + alive.Count + "，累计创建 " + TotalCreated;
        }

        private T Create()
        {
            T item = create();
            TotalCreated++;
            return item;
        }

        private void Destroy(T item)
        {
            if (item == null) return;
            if (onDestroy != null) onDestroy(item);
        }

    }
}