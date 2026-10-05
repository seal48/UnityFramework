using System;
using UnityEngine;

namespace GameFramework.Localization
{
    /// <summary>
    /// 本地化配置，由 GameController 在 Awake 里赋值（Inspector 可改）。
    /// 语言数据本身在配置表里（LocZh / LocEn / LocFont），这里只放语言策略。
    /// </summary>
    [Serializable]
    public sealed class LocalizationOptions
    {
        /// <summary>默认语言（存档里没有语言设置时用这个）。</summary>
        [Tooltip("默认语言（存档里没有语言设置时用这个）")]
        public string DefaultLanguage = "zh-CN";

        /// <summary>回退语言：当前语言缺 key 时去这张表找（一般设中文，保证兜底）。</summary>
        [Tooltip("回退语言：当前语言缺 key 时去这张表找")]
        public string FallbackLanguage = "zh-CN";

        /// <summary>
        /// 支持的语言列表。加语言 = 加 Excel（LocXx）+ 导出 + 在这里登记 +
        /// 在 LocalizationManager.RegisterTables 里加一行。
        /// </summary>
        [Tooltip("支持的语言列表，和 Loc 表一一对应")]
        public string[] SupportedLanguages = { "zh-CN", "en-US" };

        /// <summary>
        /// 编辑器里字体缺失（没配 LocFont / 地址加载失败）时，用系统字体兜底
        /// （Windows 微软雅黑 / macOS 苹方 / Linux Noto CJK）。**只在编辑器生效，不进包**，
        /// 让开发期还没放中文字体时界面也能正常显示中文。
        /// </summary>
        [Tooltip("编辑器里字体缺失时用系统字体兜底（只在编辑器生效）")]
        public bool EditorFontFallback = true;
    }
}
