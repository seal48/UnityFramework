using System;
using UnityEngine;

namespace GameFramework.Resource
{
    /// <summary>
    /// 一次资源加载的句柄。用完必须 Dispose()，否则引用计数不会归零、资源不会释放。
    /// </summary>
    public sealed class ResourceAsset<T> : IDisposable where T : UnityEngine.Object
    {
        private IResourceService _owner;
        private readonly string _location;

        /// <summary>资源本体。</summary>
        public T Asset { get; private set; }

        /// <summary>加载时使用的地址。</summary>
        public string Location { get { return _location; } }

        /// <summary>是否已经释放。</summary>
        public bool IsReleased { get; private set; }

        internal ResourceAsset(T asset, string location, IResourceService owner)
        {
            Asset = asset;
            _location = location;
            _owner = owner;
        }

        public void Dispose()
        {
            if (IsReleased)
                return;

            IsReleased = true;
            Asset = null;

            IResourceService owner = _owner;
            _owner = null;
            if (owner != null)
                owner.ReleaseAsset(_location);
        }
    }

    /// <summary>
    /// 一次 Prefab 实例化的句柄。Dispose() 会销毁 GameObject 并释放对应的资源引用。
    /// </summary>
    public sealed class ResourceInstance : IDisposable
    {
        private IResourceService _owner;
        private readonly string _location;

        /// <summary>实例化出来的对象。</summary>
        public GameObject GameObject { get; private set; }

        /// <summary>加载时使用的地址。</summary>
        public string Location { get { return _location; } }

        /// <summary>是否已经释放。</summary>
        public bool IsReleased { get; private set; }

        internal ResourceInstance(GameObject go, string location, IResourceService owner)
        {
            GameObject = go;
            _location = location;
            _owner = owner;
        }

        public void Dispose()
        {
            Dispose(false);
        }

        /// <summary>立即销毁（用 DestroyImmediate，只在明确需要同步销毁时使用）。</summary>
        public void DisposeImmediate()
        {
            Dispose(true);
        }

        private void Dispose(bool immediate)
        {
            if (IsReleased)
                return;

            IsReleased = true;

            GameObject go = GameObject;
            GameObject = null;

            if (go != null)
            {
                if (immediate)
                    UnityEngine.Object.DestroyImmediate(go);
                else
                    UnityEngine.Object.Destroy(go);
            }

            IResourceService owner = _owner;
            _owner = null;
            if (owner != null)
                owner.ReleaseAsset(_location);
        }
    }
}
