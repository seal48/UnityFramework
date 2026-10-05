namespace GameFramework.UI
{
    /// <summary>
    /// 带进度的加载界面。
    ///
    /// 存在的意义：场景加载器（SceneFramework）需要驱动进度条，但它**不应该认识具体面板** ——
    /// 否则框架层就反向依赖了业务层。所以框架只认这个接口，谁实现谁就能当加载界面。
    ///
    /// 用法：面板继承 <see cref="Panel"/> 并实现本接口，然后在场景加载选项里把
    /// LoadingPanelName 指到它登记的名字即可。
    /// </summary>
    public interface IProgressPanel
    {
        /// <summary>平滑设置进度（0~1）。加载过程中每帧调。</summary>
        void SetProgress(float progress);

        /// <summary>立刻设置进度和提示文字（不等动画）。显示 / 隐藏时用。</summary>
        void SetImmediate(float progress, string tip);
    }
}
