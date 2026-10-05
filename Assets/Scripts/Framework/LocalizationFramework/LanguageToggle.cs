using System;
using GameFramework.Event;
using UnityEngine;
using UnityEngine.UI;

namespace GameFramework.Localization
{
    /// <summary>
    /// 语言切换按钮：显示「可切换到的语言」，点击轮换。挂到 Button 节点上（Text 取子节点）。
    /// 设置界面做语言选择器、以及开发期演示切换语言都适用。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Button))]
    public sealed class LanguageToggle : MonoBehaviour
    {
        /// <summary>轮换顺序；留空 = 默认 {"zh-CN", "en-US"}。</summary>
        [Tooltip("轮换顺序；留空 = 默认 zh-CN / en-US")]
        public string[] cycle;

        private static readonly string[] DefaultCycle = { "zh-CN", "en-US" };

        private Button _button;
        private Text _label;
        private IDisposable _sub;

        private void Awake()
        {
            _button = GetComponent<Button>();
            _label = GetComponentInChildren<Text>();
        }

        private void OnEnable()
        {
            if (_button != null)
                _button.onClick.AddListener(OnClicked);

            var bus = EventBus.Global;
            if (bus != null)
                _sub = bus.Subscribe<LanguageChangedEvent>(_ => Refresh());

            Refresh();
        }

        private void OnDisable()
        {
            if (_button != null)
                _button.onClick.RemoveListener(OnClicked);

            if (_sub != null)
            {
                _sub.Dispose();
                _sub = null;
            }
        }

        private void OnClicked()
        {
            var loc = LocalizationManager.Current;
            if (loc == null)
                return;

            string next = Next(loc.Language);
            if (next != null)
                loc.SetLanguage(next);
        }

        private void Refresh()
        {
            if (_label == null)
                return;

            var loc = LocalizationManager.Current;
            string next = loc != null ? Next(loc.Language) : null;

            _label.text = DisplayName(next);

            // 套当前语言字体，保证「中文」这类字在没配字体时也能显示
            if (loc != null && loc.Font != null)
                _label.font = loc.Font;
        }

        private string Next(string current)
        {
            string[] list = (cycle != null && cycle.Length > 0) ? cycle : DefaultCycle;

            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] == current)
                    return list[(i + 1) % list.Length];
            }

            return list.Length > 0 ? list[0] : null;
        }

        private static string DisplayName(string lang)
        {
            if (lang == "zh-CN") return "中文";
            if (lang == "en-US") return "English";
            return lang;
        }
    }
}
