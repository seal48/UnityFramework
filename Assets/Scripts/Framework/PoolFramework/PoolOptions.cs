using System;
using UnityEngine;

namespace GameFramework.Pool
{
    /// <summary>单个对象池的配置。每个池可以在创建时单独给一份，不给就用 PoolInitOptions.Default。</summary>
    [Serializable]
    public sealed class PoolOptions
    {
        [Tooltip("池里最多保留多少个空闲实例，超出的归还时直接销毁（0 = 不限制）")]
        public int MaxSize = 64;

        [Tooltip("同时存活（已取出未归还）的上限，0 = 不限制。超过时 Spawn 返回 null 并报错，防止一种资源把内存吃满")]
        public int MaxAlive = 0;

        [Tooltip("池空闲多久以后开始回收里面的实例（秒），<= 0 表示不回收。回收是分批做的，不会一帧销毁一堆")]
        public float IdleShrinkDelay = 60f;

        [Tooltip("检测重复归还 / 归还不属于本池的对象，发现时打错误日志。建议开发期打开")]
        public bool CollectionCheck = true;

        [Tooltip("首次用到时预热多少个（0 = 不预热）。预热要加载预制体，异步地址用 SpawnAsync 第一次加载完后补做")]
        public int PrewarmCount = 0;

        public PoolOptions Clone()
        {
            return new PoolOptions
            {
                MaxSize = MaxSize,
                MaxAlive = MaxAlive,
                IdleShrinkDelay = IdleShrinkDelay,
                CollectionCheck = CollectionCheck,
                PrewarmCount = PrewarmCount,
            };
        }
    }

    /// <summary>启动时预热的一项：把某个地址的预制体预先创建若干份放回池里。</summary>
    [Serializable]
    public struct PoolPrewarmEntry
    {
        [Tooltip("预制体地址（YooAsset 可寻址地址），比如 Assets/Prefabs/Effects/Hit.prefab")]
        public string Location;

        [Tooltip("预热数量")]
        public int Count;
    }

    /// <summary>对象池框架的初始化参数。由 GameController 填好后传给 ObjectPoolManager.Init。</summary>
    [Serializable]
    public sealed class PoolInitOptions
    {
        [Tooltip("对象池根节点的名字，所有池化实例都挂在它下面")]
        public string RootName = "[ObjectPool]";

        [Tooltip("切场景时保留对象池根节点。关掉的话池里的实例会跟着场景被销毁（池会自动清理死引用）")]
        public bool DontDestroyOnLoad = true;

        [Tooltip("所有池的默认配置；单个池可以在 Spawn 时另给一份")]
        public PoolOptions Default = new PoolOptions();

        [Tooltip("启动时预热列表（地址 + 数量）。留空就是完全不预热，用到再创建")]
        public PoolPrewarmEntry[] Prewarm = new PoolPrewarmEntry[0];

        [Tooltip("切场景时自动清空所有池（推荐打开：防止池里缓存的实例一直引着旧场景的对象不放）")]
        public bool ClearPoolsOnSceneChange = false;

        [Tooltip("初始化完成后打一条统计日志")]
        public bool LogOnInit = true;
    }

    /// <summary>某一时刻对象池的统计快照，用来打日志 / 接调试面板。</summary>
    public struct PoolStatistics
    {
        /// <summary>池的数量。</summary>
        public int PoolCount;

        /// <summary>已取出未归还的实例总数。</summary>
        public int AliveCount;

        /// <summary>池里空闲的实例总数。</summary>
        public int PooledCount;

        /// <summary>实例总数（存活 + 空闲）。</summary>
        public int TotalCount { get { return AliveCount + PooledCount; } }

        public override string ToString()
        {
            return "池 " + PoolCount + " 个，存活 " + AliveCount + "，空闲 " + PooledCount + "，合计 " + TotalCount;
        }
    }
}