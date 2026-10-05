using System;

namespace GameFramework.UI
{
    /// <summary>
    /// 面板注册特性。挂在 Panel 子类上，UIManager 初始化时会自动扫描并注册。
    /// 不写 Location 时，预制体地址 = UIInitOptions.PanelPrefabFolder + 面板名 + ".prefab"。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class UIPanelAttribute : Attribute
    {
        /// <summary>面板名，打开/关闭时用的唯一 Key。</summary>
        public readonly string Name;

        /// <summary>面板所在层级。</summary>
        public readonly UILayer Layer;

        /// <summary>面板预制体地址，留空则按默认目录自动拼。</summary>
        public readonly string Location;

        /// <summary>关闭时是否保留实例（true = 只隐藏，下次打开更快；false = 直接销毁）。</summary>
        public readonly bool Cache;

        public UIPanelAttribute(string name, UILayer layer = UILayer.Normal, string location = null, bool cache = true)
        {
            Name = name;
            Layer = layer;
            Location = location;
            Cache = cache;
        }
    }
}
