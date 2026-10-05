using GameFramework.Log;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameFramework.Pool
{
    /// <summary>
    /// 池化实例身上的标记。由对象池自动挂上，业务代码不用管它。
    /// 它的作用是：归还时能立刻知道对象属于哪个池（不用遍历所有池去找），
    /// 以及对象被外部直接 Destroy 时能回报给池（漏归还能被发现）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PooledInstance : MonoBehaviour
    {
        internal GameObjectPool Pool;

        /// <summary>被取出过几次（第一次取出记 1）。调试用：能看出对象有没有被反复复用。</summary>
        public int SpawnCount { get; internal set; }

        /// <summary>当前是否处于「已取出」状态。</summary>
        public bool IsSpawned { get; internal set; }

        /// <summary>它属于哪个池，池被释放后为 null。</summary>
        public GameObjectPool Owner { get { return Pool; } }

        private IPoolableObject[] behaviours;
        private bool behavioursCached;

        /// <summary>
        /// 取这个实例上所有 IPoolableObject（含子节点、含未激活的）。
        /// 只缓存一次：第一次取出时扫一遍，之后直接用。中途动态加组件不会被发现（正常运行期不该这么干）。
        /// </summary>
        internal IPoolableObject[] GetBehaviours()
        {
            if (behavioursCached)
                return behaviours;

            behavioursCached = true;

            MonoBehaviour[] all = GetComponentsInChildren<MonoBehaviour>(true);
            List<IPoolableObject> found = null;

            for (int i = 0; i < all.Length; i++)
            {
                IPoolableObject poolable = all[i] as IPoolableObject;
                if (poolable == null) continue;

                if (found == null) found = new List<IPoolableObject>();
                found.Add(poolable);
            }

            behaviours = found == null ? EmptyBehaviours : found.ToArray();
            return behaviours;
        }

        private static readonly IPoolableObject[] EmptyBehaviours = new IPoolableObject[0];

        private void OnDestroy()
        {
            GameObjectPool owner = Pool;
            Pool = null;

            if (owner != null)
                owner.NotifyInstanceDestroyed(this);
        }
    }

    /// <summary>
    /// 一个预制体对应一个池：负责创建、激活、回收、销毁和统计。
    ///
    /// 由 ObjectPoolManager 创建和管理，业务代码一般不用直接碰它 ——
    /// 通过 GameController.Instance.Pool.Spawn / Despawn 就够了。
    /// 需要单独调参数（改上限、强制回收）时才从这里取。
    /// </summary>
    public sealed class GameObjectPool
    {
        /// <summary>正在整体拆除（切场景 / 退出 / 清空）：这期间对象被销毁是正常的，不用报「漏归还」。</summary>
        internal static bool TearingDown;

        private readonly Stack<GameObject> items = new Stack<GameObject>();
        private readonly HashSet<GameObject> alive = new HashSet<GameObject>(ReferenceComparer<GameObject>.Instance);
        private readonly GameObject prefab;
        private readonly Transform node;
        private readonly PoolOptions options;

        internal GameObjectPool(GameObject prefab, Transform parent, PoolOptions options)
        {
            this.prefab = prefab;
            this.options = options != null ? options : new PoolOptions();

            GameObject holder = new GameObject(prefab != null ? prefab.name : "Pool");
            node = holder.transform;
            node.SetParent(parent, false);
        }

        /// <summary>池对应的预制体。</summary>
        public GameObject Prefab { get { return prefab; } }

        /// <summary>池的名字（取自预制体），也是池节点的名字。</summary>
        public string Name { get { return prefab != null ? prefab.name : "Pool"; } }

        /// <summary>池节点。空闲实例挂在它下面，Spawn 不指定父节点时也默认挂这里。</summary>
        public Transform Node { get { return node; } }

        /// <summary>本池的配置。</summary>
        public PoolOptions Options { get { return options; } }

        /// <summary>已取出未归还的实例数。</summary>
        public int AliveCount { get { return alive.Count; } }

        /// <summary>池里空闲的实例数。</summary>
        public int PooledCount { get { return items.Count; } }

        /// <summary>累计创建过多少个实例。</summary>
        public int TotalCreated { get; private set; }

        /// <summary>累计被取出过多少次（同一个实例复用会重复计数）。</summary>
        public int TotalSpawned { get; private set; }

        /// <summary>存活数的历史峰值，用来定 MaxSize / MaxAlive。</summary>
        public int PeakAlive { get; private set; }

        /// <summary>池空闲了多久（秒），到 IdleShrinkDelay 就开始回收。</summary>
        public float IdleElapsed { get; private set; }

        #region 取出 / 归还

        /// <summary>取一个实例，挂到 parent 下（parent 为 null 就挂到池节点下）。不改动它的位置。</summary>
        internal GameObject Spawn(Transform parent)
        {
            GameObject instance = PopOrCreate();

            if (instance == null)
                return null;

            instance.transform.SetParent(parent != null ? parent : node, false);
            Activate(instance);
            return instance;
        }

        /// <summary>取一个实例并摆到指定位置。worldSpace 决定 position / rotation 是按世界还是按父节点算。</summary>
        internal GameObject Spawn(Transform parent, Vector3 position, Quaternion rotation, bool worldSpace)
        {
            GameObject instance = PopOrCreate();

            if (instance == null)
                return null;

            Transform t = instance.transform;
            t.SetParent(parent != null ? parent : node, false);

            if (worldSpace)
            {
                t.position = position;
                t.rotation = rotation;
            }
            else
            {
                t.localPosition = position;
                t.localRotation = rotation;
            }

            Activate(instance);
            return instance;
        }

        /// <summary>
        /// 归还一个实例。返回 false 表示没归还成（不属于本池 / 重复归还 / 传了 null）。
        /// destroyInsteadOfPool = true 时直接销毁，不放回池里。
        /// </summary>
        internal bool Despawn(GameObject instance, bool destroyInsteadOfPool)
        {
            if (instance == null)
                return false;

            PooledInstance marker = instance.GetComponent<PooledInstance>();

            if (marker == null || marker.Pool != this)
            {
                if (options.CollectionCheck)
                    GameLog.Error(LogTag.Pool, "这个对象不属于本池，不能归还：" + instance.name, instance);
                return false;
            }

            if (!marker.IsSpawned)
            {
                if (options.CollectionCheck)
                    GameLog.Error(LogTag.Pool, "重复归还（同一个对象被 Despawn 了两次）：" + instance.name, instance);
                return false;
            }

            marker.IsSpawned = false;
            alive.Remove(instance);

            Deactivate(instance, marker);

            if (destroyInsteadOfPool || (options.MaxSize > 0 && items.Count >= options.MaxSize))
            {
                DestroyInstance(instance, marker);
                return true;
            }

            instance.transform.SetParent(node, false);
            items.Push(instance);
            return true;
        }

        /// <summary>把本池所有存活的实例都收回来，返回收回的数量。</summary>
        internal int DespawnAll()
        {
            if (alive.Count == 0)
                return 0;

            GameObject[] snapshot = new GameObject[alive.Count];
            alive.CopyTo(snapshot);

            int count = 0;
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (Despawn(snapshot[i], false))
                    count++;
            }

            return count;
        }

        private GameObject PopOrCreate()
        {
            while (items.Count > 0)
            {
                GameObject candidate = items.Pop();
                if (candidate != null)
                    return candidate;
            }

            if (options.MaxAlive > 0 && alive.Count >= options.MaxAlive)
            {
                GameLog.ErrorFormat(LogTag.Pool, "池 \"{0}\" 存活数已达上限 {1}，本次 Spawn 被拒绝。" +
                    "请检查有没有忘记归还，或者调大 PoolOptions.MaxAlive。", Name, options.MaxAlive);
                return null;
            }

            return CreateInstance();
        }

        /// <summary>实例化一个预制体，并把池标记挂上。失败返回 null（原因已经打进日志）。</summary>
        private GameObject CreateInstance()
        {
            if (prefab == null)
            {
                GameLog.Error(LogTag.Pool, "池 \"" + Name + "\" 的预制体为空，无法创建实例。");
                return null;
            }

            GameObject instance = UnityEngine.Object.Instantiate(prefab);
            TotalCreated++;

            // 去掉 Instantiate 自动加的 "(Clone)"，层级面板里一眼能认出来
            instance.name = prefab.name;

            PooledInstance marker = instance.GetComponent<PooledInstance>();
            if (marker == null)
                marker = instance.AddComponent<PooledInstance>();
            marker.Pool = this;

            return instance;
        }

        private void Activate(GameObject instance)
        {
            PooledInstance marker = instance.GetComponent<PooledInstance>();
            if (marker == null)
                return;

            marker.SpawnCount++;
            marker.IsSpawned = true;

            if (!instance.activeSelf)
                instance.SetActive(true);

            IPoolableObject[] behaviours = marker.GetBehaviours();
            for (int i = 0; i < behaviours.Length; i++)
            {
                PoolBehaviour behaviour = behaviours[i] as PoolBehaviour;
                if (behaviour != null) behaviour.IsSpawned = true;

                try
                {
                    behaviours[i].OnPoolSpawn();
                }
                catch (Exception ex)
                {
                    GameLog.Error(LogTag.Pool, ex.Message, ex, instance);
                }
            }

            alive.Add(instance);
            TotalSpawned++;
            IdleElapsed = 0f;
            if (alive.Count > PeakAlive)
                PeakAlive = alive.Count;
        }

        private void Deactivate(GameObject instance, PooledInstance marker)
        {
            IPoolableObject[] behaviours = marker.GetBehaviours();
            for (int i = 0; i < behaviours.Length; i++)
            {
                try
                {
                    behaviours[i].OnPoolDespawn();
                }
                catch (Exception ex)
                {
                    GameLog.Error(LogTag.Pool, ex.Message, ex, instance);
                }

                PoolBehaviour behaviour = behaviours[i] as PoolBehaviour;
                if (behaviour != null) behaviour.IsSpawned = false;
            }

            if (instance.activeSelf)
                instance.SetActive(false);
        }

        #endregion

        #region 预热 / 回收 / 销毁

        /// <summary>
        /// 预热：先创建 count 个放回池里，避免第一次用的时候卡一下。
        /// 只是把实例造出来放好，不会触发 OnPoolSpawn / OnPoolDespawn（对象还没被真正取出过）。
        /// </summary>
        public void Prewarm(int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (options.MaxSize > 0 && items.Count >= options.MaxSize)
                    return;

                GameObject instance = CreateInstance();
                if (instance == null)
                    return;

                PooledInstance marker = instance.GetComponent<PooledInstance>();
                marker.IsSpawned = false;

                instance.transform.SetParent(node, false);
                if (instance.activeSelf)
                    instance.SetActive(false);

                items.Push(instance);
            }
        }

        /// <summary>销毁池里空闲的实例，一次最多 count 个（-1 = 全清）。已取出的不动。</summary>
        public void Shrink(int count = -1)
        {
            int limit = count < 0 ? items.Count : Math.Min(count, items.Count);

            for (int i = 0; i < limit; i++)
            {
                GameObject instance = items.Pop();
                if (instance == null) continue;

                DestroyInstance(instance, instance.GetComponent<PooledInstance>());
            }
        }

        /// <summary>整体销毁：池里空闲的、以及（includeAlive 时）已经取出去的实例全部销毁，然后销毁池节点。</summary>
        internal void Destroy(bool includeAlive)
        {
            Shrink(-1);

            if (includeAlive && alive.Count > 0)
            {
                GameObject[] snapshot = new GameObject[alive.Count];
                alive.CopyTo(snapshot);
                alive.Clear();

                for (int i = 0; i < snapshot.Length; i++)
                {
                    GameObject instance = snapshot[i];
                    if (instance == null) continue;

                    PooledInstance marker = instance.GetComponent<PooledInstance>();
                    if (marker != null && marker.IsSpawned)
                        Deactivate(instance, marker);

                    DestroyInstance(instance, marker);
                }
            }

            items.Clear();
            alive.Clear();

            if (node != null && node.gameObject != null)
                DestroyObject(node.gameObject);
        }

        private void DestroyInstance(GameObject instance, PooledInstance marker)
        {
            if (instance == null)
                return;

            if (marker != null)
            {
                marker.IsSpawned = false;

                IPoolableObject[] behaviours = marker.GetBehaviours();
                for (int i = 0; i < behaviours.Length; i++)
                {
                    try
                    {
                        behaviours[i].OnPoolDestroy();
                    }
                    catch (Exception ex)
                    {
                        GameLog.Error(LogTag.Pool, ex.Message, ex, instance);
                    }
                }

                // 断开回报，免得 OnDestroy 里又被算一次「漏归还」
                marker.Pool = null;
            }

            DestroyObject(instance);
        }

        /// <summary>对象被外部直接 Destroy 时由 PooledInstance 回报过来。</summary>
        internal void NotifyInstanceDestroyed(PooledInstance marker)
        {
            if (marker == null)
                return;

            GameObject instance = marker.gameObject;

            // 没有归还就消失了 = 漏归还，池会少一个可用实例（不是内存泄漏，但会越跑越少）
            if (alive.Remove(instance) && !TearingDown && options.CollectionCheck)
            {
                GameLog.WarnFormat(LogTag.Pool, "池 \"{0}\" 里的对象被直接 Destroy 了，没有走 Despawn，实例会少一个：" +
                    "请改用 GameController.Instance.Pool.Despawn(obj)。", Name);
            }
        }

        /// <summary>每帧驱动：空闲太久就把池里的实例分批回收掉。由 ObjectPoolManager.Tick 调用。</summary>
        internal void Update(float unscaledDelta)
        {
            if (options.IdleShrinkDelay <= 0f || items.Count == 0)
            {
                IdleElapsed = 0f;
                return;
            }

            IdleElapsed += unscaledDelta;
            if (IdleElapsed < options.IdleShrinkDelay)
                return;

            IdleElapsed = 0f;

            int batch = Mathf.Max(1, items.Count / 4);
            Shrink(batch);
        }

        private static void DestroyObject(GameObject go)
        {
            if (go == null) return;

            // 编辑器里（比如做工具、改预制体）不能用 Destroy，会报错
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(go);
            else
                UnityEngine.Object.DestroyImmediate(go);
        }

        #endregion

        public override string ToString()
        {
            return string.Format("Pool \"{0}\"：存活 {1}，空闲 {2}，累计创建 {3}，累计取出 {4}，峰值 {5}",
                Name, alive.Count, items.Count, TotalCreated, TotalSpawned, PeakAlive);
        }
    }
}