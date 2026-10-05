using UnityEngine;

namespace GameFramework.Pool
{
    /// <summary>
    /// 池化对象的生命周期。挂在预制体上的脚本实现它就能收到「取出 / 归还 / 销毁」回调，
    /// 用来恢复初始状态、停特效、解绑事件这些事。
    /// 三个方法都是可选的：只实现关心的那个就行。
    /// </summary>
    public interface IPoolableObject
    {
        /// <summary>从池里取出来、激活之前调用。用来把对象恢复到初始状态。</summary>
        void OnPoolSpawn();

        /// <summary>归还到池里、隐藏之后调用。用来停协程 / 清数据 / 断引用。</summary>
        void OnPoolDespawn();

        /// <summary>真的被销毁时调用（池满了、池被释放、切场景），做最后的清理。</summary>
        void OnPoolDestroy();
    }

    /// <summary>
    /// 池化预制体的推荐基类。继承它就有三个空实现的生命周期，只重写需要的那几个。
    /// 不继承也行，直接实现 IPoolableObject 即可（接口和 MonoBehaviour 不冲突）。
    /// </summary>
    public abstract class PoolBehaviour : MonoBehaviour, IPoolableObject
    {
        /// <summary>当前是否处于「已取出」状态。由对象池维护，业务代码只读。</summary>
        public bool IsSpawned { get; internal set; }

        public virtual void OnPoolSpawn() { }
        public virtual void OnPoolDespawn() { }
        public virtual void OnPoolDestroy() { }
    }

    /// <summary>
    /// 通用对象池 Pool&lt;T&gt; 里的对象可以实现它，拿到「取出 / 归还」回调。
    /// GameObject 用不到这个（那是 IPoolableObject）。
    /// </summary>
    public interface IPoolableItem
    {
        void OnPoolGet();
        void OnPoolRelease();
    }
}