using GameFramework.Resource;
using GameFramework.UI;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 加载界面。启动阶段（热更）用它显示进度与提示，属于最顶层，永远盖在其它界面之上。
///
/// 用法：
///   var loading = GameController.Instance.UI.Open(LoadingPanel.Name) as LoadingPanel;  // 也可以直接 Open<LoadingPanel>()
///   loading.SetTip("正在下载资源…");
///   loading.SetProgress(0.35f);
///   loading.SetProgress(0.35f, "35%");   // 需要自定义文本时
///   loading.Hide();
///
/// 预制体：Assets/Prefabs/LoadingPanel.prefab（LoadingSlider / TipText / ProgressText）。
/// 节点可以在预制体上手动拖，也可以留空——运行时按名字自动取。
/// </summary>
[UIPanel(Name, UILayer.Top)]
public partial class LoadingPanel : Panel, IProgressPanel
{
    /// <summary>面板注册名，和 [UIPanel] 里写的一致，避免各处硬编码字符串。</summary>
    public const string Name = "LoadingPanel";

    [Header("进度条平滑速度（每秒逼近平滑的程度，<=0 表示不平滑直接赋值）")]
    public float smoothSpeed = 8f;

    private float targetProgress;
    private float displayProgress = -1f;

    protected override void OnInit()
    {
        // progressSlider / progressText / tipText 由 LoadingPanel.Bindings.g.cs 生成并赋值
        ApplyProgress(0f, 0f);
    }

    protected override void OnUpdate()
    {
        if (Mathf.Approximately(displayProgress, targetProgress))
            return;

        float next = smoothSpeed <= 0f
            ? targetProgress
            : Mathf.Lerp(displayProgress, targetProgress, Mathf.Clamp01(smoothSpeed * Time.unscaledDeltaTime));

        ApplyProgress(next, targetProgress);
    }

    /// <summary>设置总进度（0~1）。进度条会平滑逼近，不会一帧跳过去。</summary>
    public void SetProgress(float progress)
    {
        targetProgress = Mathf.Clamp01(progress);
        if (displayProgress < 0f)
            ApplyProgress(targetProgress, targetProgress);
    }

    /// <summary>设置进度并把进度文本换成自定义内容（不传就显示百分比）。</summary>
    public void SetProgress(float progress, string text)
    {
        SetProgress(progress);
        if (progressText != null && !string.IsNullOrEmpty(text))
            progressText.text = text;
    }

    /// <summary>设置提示文本（例如「正在检查更新…」「正在下载资源 3/10」）。</summary>
    public void SetTip(string tip)
    {
        if (tipText != null)
            tipText.text = tip;
    }

    /// <summary>热更下载进度上报：进度条 + 文本一次搞定。</summary>
    public void SetHotUpdateProgress(ResourceDownloadReport report)
    {
        if (report.TotalCount <= 0)
        {
            SetProgress(1f);
            SetTip("资源已是最新");
            return;
        }

        SetProgress(report.Progress);
        SetTip(string.Format("正在下载资源 {0}/{1}（{2}）",
            report.CurrentCount, report.TotalCount, FormatBytes(report.CurrentBytes, report.TotalBytes)));
    }

    /// <summary>立刻把进度条和文本刷到指定值（不走平滑）。</summary>
    public void SetImmediate(float progress, string text)
    {
        targetProgress = Mathf.Clamp01(progress);
        ApplyProgress(targetProgress, targetProgress);
        if (!string.IsNullOrEmpty(text))
            SetTip(text);
    }

    private void ApplyProgress(float value, float showValue)
    {
        value = Mathf.Clamp01(value);

        if (progressSlider != null)
            progressSlider.value = value;

        if (progressText != null)
            progressText.text = Mathf.RoundToInt(Mathf.Clamp01(showValue) * 100f) + "%";

        displayProgress = value;
    }

    private static string FormatBytes(long current, long total)
    {
        return YooAssetResourceService.FormatBytes(current) + "/" + YooAssetResourceService.FormatBytes(total);
    }
}
