using System;
using DG.Tweening;
using UnityEngine;

namespace GameFramework.UI
{
    /// <summary>面板开关动画的预设。挂在面板预制体的 Panel 组件上选。</summary>
    public enum UIPanelAnimPreset
    {
        /// <summary>不做动画，直接显示 / 隐藏。</summary>
        None = 0,

        /// <summary>淡入淡出（自动补 CanvasGroup）。</summary>
        Fade = 1,

        /// <summary>缩放弹出。</summary>
        Scale = 2,

        /// <summary>淡入 + 缩放，最常用的入场。</summary>
        FadeScale = 3,

        /// <summary>从左侧滑入。</summary>
        SlideFromLeft = 4,

        /// <summary>从右侧滑入。</summary>
        SlideFromRight = 5,

        /// <summary>从上方滑入。</summary>
        SlideFromTop = 6,

        /// <summary>从下方滑入。</summary>
        SlideFromBottom = 7,
    }

    /// <summary>
    /// UI 动画全局参数。由 GameController 填在 UIInitOptions 里，面板可以通过 Panel.Anim.Options 读到。
    /// </summary>
    [Serializable]
    public sealed class UIAnimOptions
    {
        [Tooltip("总开关：关掉后面板开关动画瞬间完成（流程和回调照常，方便自动化测试 / 低端机降级）")]
        public bool Enabled = true;

        [Tooltip("默认时长：Anim 里不传 duration 时用它")]
        public float DefaultDuration = 0.25f;

        [Tooltip("面板入场默认时长")]
        public float ShowDuration = 0.25f;

        [Tooltip("面板退场默认时长")]
        public float HideDuration = 0.2f;

        [Tooltip("入场缓动")]
        public Ease ShowEase = Ease.OutQuad;

        [Tooltip("退场缓动")]
        public Ease HideEase = Ease.InQuad;

        [Tooltip("UI 动画是否忽略 Time.timeScale（暂停游戏时界面动画照常播）")]
        public bool UseUnscaledTime = true;

        [Tooltip("Slide 预设的位移距离（像素）")]
        public float SlideDistance = 160f;

        [Tooltip("Scale 预设的起始缩放")]
        public float StartScale = 0.9f;

        [Tooltip("切换动画期间禁止点击，避免连点连开")]
        public bool BlockRaycastWhileAnimating = true;

        [Tooltip("DOTween 池容量：同时存在的 tween 上限，0 表示用 DOTween 默认值")]
        public int MaxTweens = 512;

        [Tooltip("DOTween 池容量：同时存在的 Sequence 上限")]
        public int MaxSequences = 64;
    }
}