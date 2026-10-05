using GameFramework.Core;
using GameFramework.Log;
using System;
using System.Collections.Generic;
using GameFramework.Resource;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameFramework.Pool
{
    /// <summary>
    /// 对象池总管理器：按预制体（或地址）维护一批池，统一提供 取出 / 归还 / 预热 / 回收。
    ///
    /// 由 GameController 在资源系统就绪后初始化（ProcedureInitPool），
    /// 业务代码通过 GameController.Instance.Pool 使用，不要自己 new。
    ///
    /// 和资源系统的关系：用地址取出（SpawnAsync）时，池会一直持有那份预制体的引用，
    /// 直到 ReleasePool / ClearAll / Shutdown 才释放，所以别拿它去取大量一次性资源。
    /// </summary>
    public sealed class ObjectPoolManager : IGameModule, ITickable
    {
        /// <summary>排队等待归还的一项（延迟归还用）。</summary>
        private struct PendingDespawn
        {
            public GameObject Instance;
            public float DueTime;
        }

        private readonly Dictionary<GameObject, GameObjectPool> poolsByPrefab =
            new Dictionary<GameObject, GameObjectPool>(ReferenceComparer<GameObject>.Instance);

        private readonly Dictionary<string, GameObjectPool> poolsByLocation =
            new Dictionary<string, GameObjectPool>(StringComparer.Ordinal);

        private readonly Dictionary<string, ResourceAsset<GameObject>> prefabHandles =
            new Dictionary<string, ResourceAsset<GameObject>>(StringComparer.Ordinal);

        private readonly Dictionary<string, List<Action<GameObjectPool>>> pendingLoads =
            new Dictionary<string, List<Action<GameObjectPool>>>(StringComparer.Ordinal);

        private readonly List<PendingDespawn> pendingDespawns = new List<PendingDespawn>();
        private readonly List<GameObjectPool> poolBuffer = new List<GameObjectPool>();

        private IResourceService resource;
        private PoolInitOptions options;
        private GameObject root;
        private bool initialized;

        /// <summary>异步加载 + 清空池的世代号：清空之后才回来的加载结果会被丢掉。</summary>
        private int generation;

        private int prewarmPending;
        private bool prewarmLoading;
        private Action<bool, string> initCallback;

        /// <summary>是否初始化完成。</summary>
        public bool IsInitialized { get { return initialized; } }

        /// <summary>对象池根节点，所有池节点都挂在它下面。</summary>
        public GameObject Root { get { return root; } }

        /// <summary>池的数量。</summary>
        public int PoolCount { get { return poolsByPrefab.Count; } }

        #region 初始化与关闭

        /// <summary>
        /// 初始化：创建根节点 → 按配置预热。
        /// 预热是异步的（要加载预制体），全部加载完（或失败）之后才回调，方便放在启动流程里等。
        /// </summary>
        public void Init(IResourceService resourceService, PoolInitOptions initOptions, Action<bool, string> onComplete)
        {
            if (initialized)
            {
                if (onComplete != null) onComplete(true, "对象池已经初始化过了");
                return;
            }

            resource = resourceService;
            options = initOptions != null ? initOptions : new PoolInitOptions();
            initCallback = onComplete;
            generation++;

            GameObjectPool.TearingDown = false;

            root = new GameObject(string.IsNullOrEmpty(options.RootName) ? "[ObjectPool]" : options.RootName);

            if (options.DontDestroyOnLoad && Application.isPlaying)
                UnityEngine.Object.DontDestroyOnLoad(root);

            if (Application.isPlaying)
                Application.quitting += OnApplicationQuitting;

            if (options.ClearPoolsOnSceneChange)
                SceneManager.sceneUnloaded += OnSceneUnloaded;

            initialized = true;

            BeginPrewarm();
        }

        private void BeginPrewarm()
        {
            PoolPrewarmEntry[] list = options.Prewarm;
            if (list == null || list.Length == 0)
            {
                FinishInit();
                return;
            }

            if (resource == null)
            {
                GameLog.Warn(LogTag.Pool, "配了预热列表，但没有资源服务，跳过预热。");
                FinishInit();
                return;
            }

            prewarmLoading = true;
            int started = 0;

            for (int i = 0; i < list.Length; i++)
            {
                string location = list[i].Location;
                int count = list[i].Count;
                if (string.IsNullOrEmpty(location) || count <= 0) continue;

                started++;
                prewarmPending++;
                PrewarmAsync(location, count);
            }

            prewarmLoading = false;

            if (started == 0 || prewarmPending == 0)
                FinishInit();
        }

        private void OnPrewarmStep()
        {
            if (prewarmPending > 0)
                prewarmPending--;

            if (!prewarmLoading && prewarmPending == 0)
                FinishInit();
        }

        private void FinishInit()
        {
            if (options != null && options.LogOnInit)
                GameLog.Info(LogTag.Pool, "就绪：" + GetStatistics() + "（预制体地址 " + prefabHandles.Count + " 个）");

            Action<bool, string> callback = initCallback;
            initCallback = null;

            if (callback != null)
                callback(true, "对象池就绪，池 " + poolsByPrefab.Count + " 个");
        }

        /// <summary>整体关闭：销毁所有池和实例，释放预制体引用。由 GameController.OnDestroy 调用。</summary>
        public void Shutdown()
        {
            if (!initialized && root == null)
                return;

            initialized = false;
            generation++;
            prewarmPending = 0;
            prewarmLoading = false;
            initCallback = null;

            if (Application.isPlaying)
                Application.quitting -= OnApplicationQuitting;

            if (options != null && options.ClearPoolsOnSceneChange)
                SceneManager.sceneUnloaded -= OnSceneUnloaded;

            pendingDespawns.Clear();
            pendingLoads.Clear();

            // 这之后对象被销毁是整体拆除导致的，不用报「漏归还」
            GameObjectPool.TearingDown = true;

            poolBuffer.Clear();
            poolBuffer.AddRange(poolsByPrefab.Values);
            for (int i = 0; i < poolBuffer.Count; i++)
                poolBuffer[i].Destroy(true);

            poolsByPrefab.Clear();
            poolsByLocation.Clear();
            poolBuffer.Clear();

            ReleaseAllPrefabHandles();

            if (root != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(root);
                else UnityEngine.Object.DestroyImmediate(root);
                root = null;
            }

            resource = null;
        }

        /// <summary>清空所有池：销毁全部实例并释放预制体引用，但保留根节点，之后还能继续用。</summary>
        public void ClearAll()
        {
            if (root == null)
                return;

            generation++;
            pendingDespawns.Clear();

            bool previous = GameObjectPool.TearingDown;
            GameObjectPool.TearingDown = true;

            try
            {
                poolBuffer.Clear();
                poolBuffer.AddRange(poolsByPrefab.Values);
                for (int i = 0; i < poolBuffer.Count; i++)
                    poolBuffer[i].Destroy(true);

                poolBuffer.Clear();
                poolsByPrefab.Clear();
                poolsByLocation.Clear();
            }
            finally
            {
                GameObjectPool.TearingDown = previous;
            }

            ReleaseAllPrefabHandles();
        }

        private void ReleaseAllPrefabHandles()
        {
            foreach (KeyValuePair<string, ResourceAsset<GameObject>> pair in prefabHandles)
            {
                if (pair.Value != null)
                    pair.Value.Dispose();
            }

            prefabHandles.Clear();
        }

        private void OnApplicationQuitting()
        {
            GameObjectPool.TearingDown = true;
        }

        private void OnSceneUnloaded(Scene scene)
        {
            ClearAll();
        }

        /// <summary>每帧驱动：处理延迟归还、空闲回收。由 GameController.Update 调用。</summary>
        public void Tick(float deltaTime, float unscaledDeltaTime)
        {
            if (!initialized)
                return;

            if (pendingDespawns.Count > 0)
            {
                float now = Time.unscaledTime;

                for (int i = pendingDespawns.Count - 1; i >= 0; i--)
                {
                    if (now < pendingDespawns[i].DueTime)
                        continue;

                    GameObject instance = pendingDespawns[i].Instance;
                    pendingDespawns.RemoveAt(i);

                    // 期间对象可能已经被销毁 / 池被释放了，那种情况直接忽略
                    if (instance == null) continue;

                    PooledInstance marker = instance.GetComponent<PooledInstance>();
                    if (marker != null && marker.Pool != null)
                        marker.Pool.Despawn(instance, false);
                }
            }

            poolBuffer.Clear();
            poolBuffer.AddRange(poolsByPrefab.Values);

            for (int i = 0; i < poolBuffer.Count; i++)
                poolBuffer[i].Update(Time.unscaledDeltaTime);

            poolBuffer.Clear();
        }

        #endregion

        #region 取出

        /// <summary>取一个实例（挂到池节点下，位置保持预制体原本的局部变换）。</summary>
        public GameObject Spawn(GameObject prefab)
        {
            if (!CheckReady() || prefab == null)
                return null;

            return GetOrCreatePool(prefab, null, null).Spawn(null);
        }

        /// <summary>取一个实例并挂到 parent 下。</summary>
        public GameObject Spawn(GameObject prefab, Transform parent)
        {
            if (!CheckReady() || prefab == null)
                return null;

            return GetOrCreatePool(prefab, null, null).Spawn(parent);
        }

        /// <summary>
        /// 取一个实例并摆到指定位置。worldSpace = true（默认）时 position / rotation 按世界坐标算，
        /// false 时按 parent 的局部坐标算。
        /// </summary>
        public GameObject Spawn(GameObject prefab, Transform parent, Vector3 position, Quaternion rotation, bool worldSpace = true)
        {
            if (!CheckReady() || prefab == null)
                return null;

            return GetOrCreatePool(prefab, null, null).Spawn(parent, position, rotation, worldSpace);
        }

        /// <summary>取一个实例并顺手取它身上的组件（预制体根节点上没有该组件时返回 null 并告警）。</summary>
        public T Spawn<T>(GameObject prefab, Transform parent, Vector3 position, Quaternion rotation, bool worldSpace = true) where T : Component
        {
            GameObject instance = Spawn(prefab, parent, position, rotation, worldSpace);
            if (instance == null)
                return null;

            T component = instance.GetComponent<T>();
            if (component == null)
            {
                GameLog.WarnFormat(LogTag.Pool, "{0} 上没有组件 {1}，Spawn 出来的对象已归还。", prefab.name, typeof(T).Name);
                Despawn(instance);
            }

            return component;
        }

        /// <summary>
        /// 按地址取一个实例。预制体第一次用到时才加载，加载完成后回调（同一帧，可能在本次调用里就回调）。
        /// 失败时回调参数为 null。
        /// </summary>
        public void SpawnAsync(string location, Transform parent, Action<GameObject> onSpawned)
        {
            SpawnAsync(location, parent, Vector3.zero, Quaternion.identity, false, onSpawned);
        }

        /// <summary>按地址取一个实例并摆到指定位置。</summary>
        public void SpawnAsync(string location, Transform parent, Vector3 position, Quaternion rotation, bool worldSpace, Action<GameObject> onSpawned)
        {
            if (!CheckReady())
            {
                if (onSpawned != null) onSpawned(null);
                return;
            }

            GetPoolAsync(location, delegate(GameObjectPool pool)
            {
                GameObject instance = null;
                if (pool != null)
                    instance = pool.Spawn(parent, position, rotation, worldSpace);

                if (onSpawned != null) onSpawned(instance);
            });
        }

        /// <summary>预热：先把实例创建好放回池里。</summary>
        public void Prewarm(GameObject prefab, int count)
        {
            if (!CheckReady() || prefab == null || count <= 0)
                return;

            GetOrCreatePool(prefab, null, null).Prewarm(count);
        }

        /// <summary>按地址预热（第一次用到时才加载预制体）。</summary>
        public void PrewarmAsync(string location, int count)
        {
            if (!CheckReady() || string.IsNullOrEmpty(location) || count <= 0)
                return;

            GetPoolAsync(location, delegate(GameObjectPool pool)
            {
                if (pool != null)
                    pool.Prewarm(count);
                else
                    GameLog.Warn(LogTag.Pool, "预热失败，预制体加载不出来：" + location);
            });
        }

        #endregion

        #region 归还

        /// <summary>归还一个实例。不是池化对象 / 重复归还会被打日志并返回 false。</summary>
        public bool Despawn(GameObject instance)
        {
            if (instance == null)
                return false;

            PooledInstance marker = instance.GetComponent<PooledInstance>();
            if (marker == null || marker.Pool == null)
            {
                GameLog.Error(LogTag.Pool, "这个对象不是池化对象（或它的池已经释放），不能归还：" + instance.name, instance);
                return false;
            }

            return marker.Pool.Despawn(instance, false);
        }

        /// <summary>延迟归还：delay 秒后自动归还。常用于特效、飘字这类播完就没用的东西。</summary>
        public void Despawn(GameObject instance, float delay)
        {
            if (instance == null)
                return;

            if (delay <= 0f)
            {
                Despawn(instance);
                return;
            }

            CancelPendingDespawn(instance);
            pendingDespawns.Add(new PendingDespawn
            {
                Instance = instance,
                DueTime = Time.unscaledTime + delay,
            });
        }

        private void CancelPendingDespawn(GameObject instance)
        {
            for (int i = pendingDespawns.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(pendingDespawns[i].Instance, instance))
                    pendingDespawns.RemoveAt(i);
            }
        }

        /// <summary>把该预制体所有已取出的实例都收回来，返回收回的数量。</summary>
        public int DespawnAll(GameObject prefab)
        {
            GameObjectPool pool = GetPool(prefab);
            return pool == null ? 0 : pool.DespawnAll();
        }

        #endregion

        #region 池的查询与释放

        /// <summary>取某个预制体对应的池，没有就返回 null。</summary>
        public GameObjectPool GetPool(GameObject prefab)
        {
            if (prefab == null)
                return null;

            GameObjectPool pool;
            return poolsByPrefab.TryGetValue(prefab, out pool) ? pool : null;
        }

        /// <summary>取某个地址对应的池（必须先 SpawnAsync / PrewarmAsync 过），没有就返回 null。</summary>
        public GameObjectPool GetPool(string location)
        {
            if (string.IsNullOrEmpty(location))
                return null;

            GameObjectPool pool;
            return poolsByLocation.TryGetValue(location, out pool) ? pool : null;
        }

        /// <summary>把所有池写进 results（会先清空）。</summary>
        public void GetAllPools(List<GameObjectPool> results)
        {
            if (results == null)
                return;

            results.Clear();
            results.AddRange(poolsByPrefab.Values);
        }

        /// <summary>释放某个预制体的池：销毁它的全部实例（含已取出的），并放掉预制体引用。</summary>
        public bool ReleasePool(GameObject prefab)
        {
            GameObjectPool pool = GetPool(prefab);
            if (pool == null)
                return false;

            ReleasePoolInternal(pool);
            return true;
        }

        /// <summary>按地址释放池。</summary>
        public bool ReleasePool(string location)
        {
            GameObjectPool pool = GetPool(location);
            if (pool == null)
                return false;

            ReleasePoolInternal(pool);
            return true;
        }

        private void ReleasePoolInternal(GameObjectPool pool)
        {
            poolsByPrefab.Remove(pool.Prefab);

            bool previous = GameObjectPool.TearingDown;
            GameObjectPool.TearingDown = true;

            try
            {
                pool.Destroy(true);
            }
            finally
            {
                GameObjectPool.TearingDown = previous;
            }

            // 地址索引和预制体引用要一起摘掉
            List<string> locations = null;

            foreach (KeyValuePair<string, GameObjectPool> pair in poolsByLocation)
            {
                if (pair.Value != pool) continue;

                if (locations == null) locations = new List<string>();
                locations.Add(pair.Key);
            }

            if (locations == null)
                return;

            for (int i = 0; i < locations.Count; i++)
            {
                string location = locations[i];
                poolsByLocation.Remove(location);

                ResourceAsset<GameObject> handle;
                if (prefabHandles.TryGetValue(location, out handle))
                {
                    if (handle != null) handle.Dispose();
                    prefabHandles.Remove(location);
                }
            }
        }

        /// <summary>当前统计快照。</summary>
        public PoolStatistics GetStatistics()
        {
            PoolStatistics stats = new PoolStatistics();
            stats.PoolCount = poolsByPrefab.Count;

            foreach (GameObjectPool pool in poolsByPrefab.Values)
            {
                stats.AliveCount += pool.AliveCount;
                stats.PooledCount += pool.PooledCount;
            }

            return stats;
        }

        /// <summary>打一条统计日志（排查泄漏 / 定容量用）。</summary>
        public void LogStatistics()
        {
            GameLog.Info(LogTag.Pool, GetStatistics().ToString());

            foreach (GameObjectPool pool in poolsByPrefab.Values)
                GameLog.Info(LogTag.Pool, "  · " + pool);
        }

        #endregion

        #region 内部

        private bool CheckReady()
        {
            if (initialized)
                return true;

            GameLog.Error(LogTag.Pool, "对象池还没初始化（要等 ProcedureInitPool 跑完），本次调用被忽略。");
            return false;
        }

        private GameObjectPool GetOrCreatePool(GameObject prefab, string location, PoolOptions poolOptions)
        {
            GameObjectPool pool;

            if (poolsByPrefab.TryGetValue(prefab, out pool))
            {
                // 同一个预制体只可能有一个池；用地址又拿了一次就把地址登记上
                if (!string.IsNullOrEmpty(location))
                    poolsByLocation[location] = pool;

                return pool;
            }

            Transform parent = root != null ? root.transform : null;
            PoolOptions effective = poolOptions != null ? poolOptions : options.Default;
            pool = new GameObjectPool(prefab, parent, effective);

            // 预制体这时已经在手上，直接同步补齐预热，省掉第一次用的时候那一下 Instantiate
            if (effective != null && effective.PrewarmCount > 0)
                pool.Prewarm(effective.PrewarmCount);

            poolsByPrefab[prefab] = pool;

            if (!string.IsNullOrEmpty(location))
                poolsByLocation[location] = pool;

            return pool;
        }

        private void GetPoolAsync(string location, Action<GameObjectPool> onReady)
        {
            if (string.IsNullOrEmpty(location))
            {
                GameLog.Error(LogTag.Pool, "地址为空，不能用地址创建对象池。");
                if (onReady != null) onReady(null);
                return;
            }

            GameObjectPool pool;
            if (poolsByLocation.TryGetValue(location, out pool))
            {
                if (onReady != null) onReady(pool);
                return;
            }

            List<Action<GameObjectPool>> queue;
            if (pendingLoads.TryGetValue(location, out queue))
            {
                // 同一份预制体已经在加载了，排队等结果，别重复加载
                if (onReady != null) queue.Add(onReady);
                return;
            }

            if (resource == null)
            {
                GameLog.Error(LogTag.Pool, "没有资源服务，不能用地址创建对象池：" + location);
                if (onReady != null) onReady(null);
                return;
            }

            queue = new List<Action<GameObjectPool>>();
            if (onReady != null) queue.Add(onReady);
            pendingLoads[location] = queue;

            int requestGeneration = generation;
            string requestedLocation = location;

            resource.LoadAssetAsync<GameObject>(requestedLocation, delegate(ResourceAsset<GameObject> handle)
            {
                OnPrefabLoaded(requestedLocation, handle, requestGeneration);
            });
        }

        private void OnPrefabLoaded(string location, ResourceAsset<GameObject> handle, int requestGeneration)
        {
            List<Action<GameObjectPool>> queue = null;
            if (pendingLoads.ContainsKey(location))
            {
                queue = pendingLoads[location];
                pendingLoads.Remove(location);
            }

            GameObjectPool pool = null;

            if (requestGeneration != generation)
            {
                // 加载期间池被清空 / 关闭了，结果直接丢掉，别把引用留下
                if (handle != null) handle.Dispose();
            }
            else if (handle == null || handle.Asset == null)
            {
                GameLog.Error(LogTag.Pool, "预制体加载失败：" + location);
            }
            else
            {
                // 池活着期间一直拿着这份引用，ReleasePool / ClearAll 时才还回去
                prefabHandles[location] = handle;
                pool = GetOrCreatePool(handle.Asset, location, null);
            }

            if (queue == null)
                return;

            for (int i = 0; i < queue.Count; i++)
            {
                if (queue[i] != null)
                    queue[i](pool);
            }
        }

        #endregion
    }
}