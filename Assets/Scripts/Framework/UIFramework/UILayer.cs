namespace GameFramework.UI
{
    /// <summary>
    /// UI 层级。Canvas 下按这个顺序排列子节点，序号越大越显示在前面。
    /// 面板在注册时指定自己属于哪个层级，打开时会被挂到对应层级的容器下面。
    /// </summary>
    public enum UILayer
    {
        /// <summary>背景层：全屏背景、场景底图等，永远压在最下面。</summary>
        Background = 0,

        /// <summary>普通层：主界面、功能界面等常规全屏面板。</summary>
        Normal = 1,

        /// <summary>弹窗层：确认框、背包、设置等需要盖住普通层的弹窗。</summary>
        Popup = 2,

        /// <summary>顶层：飘字、Toast、Loading 遮罩、新手引导等，永远在最上面。</summary>
        Top = 3,
    }
}
