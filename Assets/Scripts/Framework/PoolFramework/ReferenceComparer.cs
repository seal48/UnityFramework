using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace GameFramework.Pool
{
    /// <summary>
    /// 按引用比较。默认比较器会走 Equals / GetHashCode，被重写过的类型会误判成同一个对象；
    /// 而且 Unity 的「假 null」在默认比较器下行为也不确定，所以池里的集合统一用它。
    /// </summary>
    internal sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();

        public bool Equals(T x, T y) { return ReferenceEquals(x, y); }

        public int GetHashCode(T obj) { return RuntimeHelpers.GetHashCode(obj); }
    }
}