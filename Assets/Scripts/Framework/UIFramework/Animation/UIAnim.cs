using GameFramework.Log;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace GameFramework.UI
{
    /// <summary>
    /// 面板动画器：把 DOTween 包成「按节点名直接对组件做动画」的形式，只在面板内部使用。
    ///
    /// 约定：
    /// - 取节点沿用 Panel 的规则（短名或相对路径），取不到会报错；
    /// - 所有动画都登记在本面板的播放列表里，面板隐藏 / 销毁时统一 Kill，回调不会打在已销毁对象上；
    /// - 同一个目标的同一个属性重复播时，DOTween 会按默认冲突规则杀掉前一个，所以连点不会叠加；
    /// - duration 传负数表示用全局默认时长（UIAnimOptions.DefaultDuration）；
    /// - 返回值就是 DOTween 的 Tween / Sequence，可以继续链式 .SetEase() / .SetDelay() / .OnComplete()；
    ///   取不到目标、或者总开关关掉时返回一个「空 Sequence」，它立即完成，链式写法和回调都不会断；
    ///   PlayPreset 例外：没有开关动画（None / 总开关关掉）时返回 null，表示不用等待。
    /// </summary>
    public sealed class UIAnim
    {
        private struct Entry
        {
            public Tween Tween;
            public GameObject Owner;
        }

        private static readonly UIAnimOptions SharedOptions = new UIAnimOptions();

        private readonly Panel panel;
        private readonly UIAnimOptions options;
        private readonly List<Entry> entries = new List<Entry>(8);

        private CanvasGroup rootGroup;
        private RectTransform rootRect;
        private bool hasRestPos;
        private Vector2 restPos;

        internal UIAnim(Panel panel, UIAnimOptions options)
        {
            this.panel = panel;
            this.options = options != null ? options : SharedOptions;
        }

        /// <summary>全局参数。</summary>
        public UIAnimOptions Options { get { return options; } }

        /// <summary>动画是否启用（总开关关掉时所有时长变 0，流程与回调不变）。</summary>
        public bool Enabled { get { return options.Enabled; } }

        /// <summary>当前是否有动画在播。</summary>
        public bool IsPlayingAny { get { return CountPlaying() > 0; } }

        #region 面板自身（根节点）

        /// <summary>面板根节点上的 CanvasGroup，没有就自动加一个（淡入淡出用）。</summary>
        public CanvasGroup RootGroup
        {
            get
            {
                if (rootGroup == null)
                {
                    rootGroup = panel.GetComponent<CanvasGroup>();
                    if (rootGroup == null)
                        rootGroup = panel.gameObject.AddComponent<CanvasGroup>();
                }
                return rootGroup;
            }
        }

        private RectTransform RootRect
        {
            get
            {
                if (rootRect == null)
                    rootRect = panel.transform as RectTransform;
                return rootRect;
            }
        }

        /// <summary>面板的停靠位置：第一次用的时候记下来，之后一直以它为准。</summary>
        private Vector2 RestPosition
        {
            get
            {
                if (!hasRestPos)
                {
                    RectTransform rect = RootRect;
                    restPos = rect != null ? rect.anchoredPosition : Vector2.zero;
                    hasRestPos = true;
                }
                return restPos;
            }
        }

        #endregion

        #region 取节点

        /// <summary>按节点名（短名或相对路径）取节点，取不到会报错并返回 null。</summary>
        public GameObject Node(string node)
        {
            GameObject go = panel.GetNode(node);
            if (go == null)
                GameLog.ErrorFormat(LogTag.UIAnim, "[{0}] 取不到节点 \"{1}\"。", panel.name, node);
            return go;
        }

        private CanvasGroup ResolveGroup(string node)
        {
            GameObject go = Node(node);
            if (go == null)
                return null;

            CanvasGroup group = go.GetComponent<CanvasGroup>();
            if (group == null)
                group = go.AddComponent<CanvasGroup>();
            return group;
        }

        private Graphic ResolveGraphic(string node)
        {
            GameObject go = Node(node);
            if (go == null)
                return null;

            Graphic graphic = go.GetComponent<Graphic>();
            if (graphic == null)
                GameLog.ErrorFormat(LogTag.UIAnim, "[{0}] 节点 \"{1}\" 上没有 Graphic（Image / Text / RawImage）。", panel.name, node);
            return graphic;
        }

        private RectTransform ResolveRect(string node)
        {
            GameObject go = Node(node);
            return go != null ? go.transform as RectTransform : null;
        }

        #endregion

        #region 立即设值（搭起始状态用）

        public void SetAlpha(string node, float alpha) { SetAlpha(ResolveGroup(node), alpha); }
        public void SetAlpha(CanvasGroup group, float alpha) { if (group != null) group.alpha = alpha; }

        public void SetScale(string node, float scale)
        {
            RectTransform rect = ResolveRect(node);
            if (rect != null)
                rect.localScale = new Vector3(scale, scale, rect.localScale.z);
        }

        public void SetAnchoredPos(string node, Vector2 position)
        {
            RectTransform rect = ResolveRect(node);
            if (rect != null)
                rect.anchoredPosition = position;
        }

        #endregion

        #region 透明度

        /// <summary>淡入淡出：走 CanvasGroup（整棵子树一起淡），节点没有 CanvasGroup 会自动加。</summary>
        public Tween Fade(string node, float to, float duration = -1f)
        {
            return Fade(ResolveGroup(node), to, duration);
        }

        public Tween Fade(CanvasGroup group, float to, float duration = -1f)
        {
            if (group == null)
                return Noop(null);
            return Finish(group.DOFade(to, Used(duration)), group.gameObject);
        }

        /// <summary>只改 Graphic 自己的 alpha（Image / Text / RawImage）。</summary>
        public Tween FadeGraphic(string node, float to, float duration = -1f)
        {
            return Fade(ResolveGraphic(node), to, duration);
        }

        public Tween Fade(Graphic target, float to, float duration = -1f)
        {
            if (target == null)
                return Noop(null);
            return Finish(target.DOFade(to, Used(duration)), target.gameObject);
        }

        public Tween FadeIn(string node, float duration = -1f) { return Fade(node, 1f, duration); }

        public Tween FadeOut(string node, float duration = -1f) { return Fade(node, 0f, duration); }

        #endregion

        #region 缩放

        public Tween Scale(string node, float to, float duration = -1f)
        {
            return Scale(ResolveRect(node), to, duration);
        }

        public Tween Scale(Transform target, float to, float duration = -1f)
        {
            if (target == null)
                return Noop(null);

            float z = target.localScale.z;
            return Finish(target.DOScale(new Vector3(to, to, z), Used(duration)), target.gameObject);
        }

        /// <summary>从 from 缩放到它当前的大小。</summary>
        public Tween ScaleFrom(string node, float from, float duration = -1f)
        {
            RectTransform rect = ResolveRect(node);
            if (rect == null)
                return Noop(null);

            float z = rect.localScale.z;
            float to = rect.localScale.x;
            rect.localScale = new Vector3(from, from, z);
            return Finish(rect.DOScale(new Vector3(to, to, z), Used(duration)), rect.gameObject);
        }

        /// <summary>弹一下，最终大小不变。</summary>
        public Tween Pop(string node, float strength = 0.12f, float duration = 0.25f)
        {
            RectTransform rect = ResolveRect(node);
            if (rect == null)
                return Noop(null);

            float d = Used(duration);
            if (d <= 0f)
                return Noop(rect.gameObject);

            return Finish(rect.DOPunchScale(new Vector3(strength, strength, 0f), d, 8, 1f), rect.gameObject);
        }

        #endregion

        #region 位移（anchoredPosition）

        public Tween Move(string node, Vector2 to, float duration = -1f)
        {
            RectTransform rect = ResolveRect(node);
            if (rect == null)
                return Noop(null);
            return Finish(rect.DOAnchorPos(to, Used(duration)), rect.gameObject);
        }

        /// <summary>相对当前位置移动。</summary>
        public Tween MoveOffset(string node, Vector2 offset, float duration = -1f)
        {
            RectTransform rect = ResolveRect(node);
            if (rect == null)
                return Noop(null);
            return Finish(rect.DOAnchorPos(rect.anchoredPosition + offset, Used(duration)), rect.gameObject);
        }

        public Tween MoveX(string node, float to, float duration = -1f)
        {
            RectTransform rect = ResolveRect(node);
            if (rect == null)
                return Noop(null);
            return Finish(rect.DOAnchorPosX(to, Used(duration)), rect.gameObject);
        }

        public Tween MoveY(string node, float to, float duration = -1f)
        {
            RectTransform rect = ResolveRect(node);
            if (rect == null)
                return Noop(null);
            return Finish(rect.DOAnchorPosY(to, Used(duration)), rect.gameObject);
        }

        #endregion

        #region 旋转

        public Tween Rotate(string node, float toZ, float duration = -1f)
        {
            RectTransform rect = ResolveRect(node);
            if (rect == null)
                return Noop(null);
            return Finish(rect.DOLocalRotate(new Vector3(0f, 0f, toZ), Used(duration)), rect.gameObject);
        }

        #endregion

        #region 颜色 / 填充 / 数值

        /// <summary>改颜色（Image / Text / RawImage）。</summary>
        public Tween Tint(string node, Color to, float duration = -1f)
        {
            Graphic graphic = ResolveGraphic(node);
            if (graphic == null)
                return Noop(null);
            return Finish(graphic.DOColor(to, Used(duration)), graphic.gameObject);
        }

        /// <summary>Image 的填充量（做进度条 / 环形进度）。</summary>
        public Tween FillAmount(string node, float to, float duration = -1f)
        {
            GameObject go = Node(node);
            if (go == null)
                return Noop(null);

            Image image = go.GetComponent<Image>();
            if (image == null)
            {
                GameLog.ErrorFormat(LogTag.UIAnim, "[{0}] 节点 \"{1}\" 上没有 Image 组件。", panel.name, node);
                return Noop(go);
            }

            return Finish(image.DOFillAmount(to, Used(duration)), go);
        }

        public Tween SliderValue(string node, float to, float duration = -1f)
        {
            GameObject go = Node(node);
            if (go == null)
                return Noop(null);

            Slider slider = go.GetComponent<Slider>();
            if (slider == null)
            {
                GameLog.ErrorFormat(LogTag.UIAnim, "[{0}] 节点 \"{1}\" 上没有 Slider 组件。", panel.name, node);
                return Noop(go);
            }

            return Finish(slider.DOValue(to, Used(duration)), go);
        }

        /// <summary>Text 数字滚动：从 from 滚到 to。</summary>
        public Tween CountTo(string node, int to, float duration = -1f, int from = 0, bool thousandsSeparator = true)
        {
            GameObject go = Node(node);
            if (go == null)
                return Noop(null);

            Text text = go.GetComponent<Text>();
            if (text == null)
            {
                GameLog.ErrorFormat(LogTag.UIAnim, "[{0}] 节点 \"{1}\" 上没有 Text 组件。", panel.name, node);
                return Noop(go);
            }

            return Finish(text.DOCounter(from, to, Used(duration), thousandsSeparator), go);
        }

        /// <summary>Text 逐字显示 / 乱码收敛（打字机效果）。</summary>
        public Tween TextTo(string node, string to, float duration = -1f, ScrambleMode scrambleMode = ScrambleMode.None)
        {
            GameObject go = Node(node);
            if (go == null)
                return Noop(null);

            Text text = go.GetComponent<Text>();
            if (text == null)
            {
                GameLog.ErrorFormat(LogTag.UIAnim, "[{0}] 节点 \"{1}\" 上没有 Text 组件。", panel.name, node);
                return Noop(go);
            }

            return Finish(text.DOText(to, Used(duration), true, scrambleMode, null), go);
        }

        #endregion

        #region 序列

        /// <summary>建一个空的 Sequence（已配好时间基准并登记），自己 Append / Join / Insert。</summary>
        public Sequence NewSequence()
        {
            Sequence sequence = DOTween.Sequence();
            Finish(sequence, panel.gameObject);
            return sequence;
        }

        #endregion

        #region 控制

        /// <summary>杀掉某个节点上的动画。</summary>
        public void Kill(string node, bool complete = false)
        {
            GameObject go = panel.GetNode(node);
            if (go == null)
                return;

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Entry entry = entries[i];
                if (entry.Owner != go)
                    continue;

                if (entry.Tween != null && entry.Tween.IsActive())
                    entry.Tween.Kill(complete);
                entries.RemoveAt(i);
            }
        }

        /// <summary>杀掉本面板所有动画（面板隐藏 / 销毁时框架会自动调）。</summary>
        public void KillAll()
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Tween tween = entries[i].Tween;
                if (tween != null && tween.IsActive())
                    tween.Kill(false);
            }
            entries.Clear();
        }

        /// <summary>某个节点上是否还有动画在播。</summary>
        public bool IsPlaying(string node)
        {
            GameObject go = panel.GetNode(node);
            if (go == null)
                return false;

            for (int i = 0; i < entries.Count; i++)
            {
                Tween tween = entries[i].Tween;
                if (entries[i].Owner == go && tween != null && tween.IsActive())
                    return true;
            }
            return false;
        }

        #endregion

        #region 开关动画预设

        /// <summary>把面板恢复成初始样子（隐藏后 / 动画被打断后用）。</summary>
        public void ResetRoot()
        {
            RectTransform rect = RootRect;
            if (rect != null)
            {
                rect.localScale = Vector3.one;
                if (hasRestPos)
                    rect.anchoredPosition = restPos;
            }

            if (rootGroup != null)
            {
                rootGroup.alpha = 1f;
                rootGroup.blocksRaycasts = true;
                rootGroup.interactable = true;
            }
        }

        /// <summary>恢复点击（入场动画播完后调）。</summary>
        public void ResetRaycastBlock()
        {
            if (rootGroup == null)
                return;

            rootGroup.blocksRaycasts = true;
            rootGroup.interactable = true;
        }

        /// <summary>
        /// 按预设摆好起始状态。必须在面板被激活之前调用，否则会先闪一帧完整界面。
        /// </summary>
        public void PreparePreset(UIPanelAnimPreset preset)
        {
            // 没有入场动画（None / 总开关关掉）时把根节点恢复干净，避免上一次被打断的动画留下半透明状态
            if (preset == UIPanelAnimPreset.None || !options.Enabled)
            {
                ResetRoot();
                return;
            }

            if (options.BlockRaycastWhileAnimating)
            {
                CanvasGroup group = RootGroup;
                group.blocksRaycasts = false;
                group.interactable = false;
            }

            RectTransform rect = RootRect;

            switch (preset)
            {
                case UIPanelAnimPreset.Fade:
                    SetAlpha(RootGroup, 0f);
                    break;

                case UIPanelAnimPreset.Scale:
                    if (rect != null)
                        rect.localScale = new Vector3(options.StartScale, options.StartScale, rect.localScale.z);
                    break;

                case UIPanelAnimPreset.FadeScale:
                    SetAlpha(RootGroup, 0f);
                    if (rect != null)
                        rect.localScale = new Vector3(options.StartScale, options.StartScale, rect.localScale.z);
                    break;

                default:
                    if (rect != null)
                        rect.anchoredPosition = RestPosition + SlideOffset(preset);
                    break;
            }
        }

        /// <summary>按预设播入场 / 退场动画。</summary>
        public Tween PlayPreset(UIPanelAnimPreset preset, bool isShow, float duration = 0f)
        {
            GameObject owner = panel.gameObject;

            // 返回 null 表示「没有动画，不用等」，UIManager 会立刻走显示 / 隐藏流程
            if (preset == UIPanelAnimPreset.None || !options.Enabled)
                return null;

            float d = UsedPreset(duration, isShow);
            if (d <= 0f)
                return null;

            Ease ease = isShow ? options.ShowEase : options.HideEase;
            CanvasGroup group = RootGroup;
            RectTransform rect = RootRect;

            switch (preset)
            {
                case UIPanelAnimPreset.Fade:
                    return Finish(group.DOFade(isShow ? 1f : 0f, d).SetEase(ease), owner);

                case UIPanelAnimPreset.Scale:
                {
                    if (rect == null)
                        return Noop(owner);

                    float scale = isShow ? 1f : options.StartScale;
                    return Finish(rect.DOScale(new Vector3(scale, scale, rect.localScale.z), d).SetEase(ease), owner);
                }

                case UIPanelAnimPreset.FadeScale:
                {
                    if (rect == null)
                        return Noop(owner);

                    float scale = isShow ? 1f : options.StartScale;
                    Sequence sequence = DOTween.Sequence();
                    sequence.Append(group.DOFade(isShow ? 1f : 0f, d));
                    sequence.Join(rect.DOScale(new Vector3(scale, scale, rect.localScale.z), d));
                    sequence.SetEase(ease);
                    return Finish(sequence, owner);
                }

                default:
                {
                    if (rect == null)
                        return Noop(owner);

                    Vector2 target = isShow ? RestPosition : RestPosition + SlideOffset(preset);
                    return Finish(rect.DOAnchorPos(target, d).SetEase(ease), owner);
                }
            }
        }

        private Vector2 SlideOffset(UIPanelAnimPreset preset)
        {
            switch (preset)
            {
                case UIPanelAnimPreset.SlideFromLeft: return new Vector2(-options.SlideDistance, 0f);
                case UIPanelAnimPreset.SlideFromRight: return new Vector2(options.SlideDistance, 0f);
                case UIPanelAnimPreset.SlideFromTop: return new Vector2(0f, options.SlideDistance);
                case UIPanelAnimPreset.SlideFromBottom: return new Vector2(0f, -options.SlideDistance);
                default: return Vector2.zero;
            }
        }

        #endregion

        #region 内部

        /// <summary>把「不传时长」换算成实际时长；总开关关掉时强制 0（瞬间完成，回调照常）。</summary>
        private float Used(float duration)
        {
            if (!options.Enabled)
                return 0f;
            return duration < 0f ? options.DefaultDuration : duration;
        }

        private float UsedPreset(float duration, bool isShow)
        {
            if (!options.Enabled)
                return 0f;
            if (duration > 0f)
                return duration;
            return isShow ? options.ShowDuration : options.HideDuration;
        }

        /// <summary>统一收尾：挂 link（目标销毁自动 Kill）、设时间基准、登记到本面板。</summary>
        private Tween Finish(Tween tween, GameObject owner)
        {
            if (tween == null)
                return null;

            if (owner != null)
                tween.SetLink(owner);

            tween.SetUpdate(options.UseUnscaledTime);
            entries.Add(new Entry { Tween = tween, Owner = owner });

            if (entries.Count > 24)
                PruneFinished();

            return tween;
        }

        /// <summary>没有实际动画时返回的空 Sequence：立即完成，链式回调和流程都不会断。</summary>
        private Tween Noop(GameObject owner)
        {
            Sequence sequence = DOTween.Sequence();
            Finish(sequence, owner);
            return sequence;
        }

        private void PruneFinished()
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Tween tween = entries[i].Tween;
                if (tween == null || !tween.IsActive())
                    entries.RemoveAt(i);
            }
        }

        private int CountPlaying()
        {
            int count = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                Tween tween = entries[i].Tween;
                if (tween != null && tween.IsActive())
                    count++;
            }
            return count;
        }

        #endregion
    }
}