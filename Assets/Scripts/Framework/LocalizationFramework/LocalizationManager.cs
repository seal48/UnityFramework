using System;
using System.Collections.Generic;
using GameFramework.Core;
using GameFramework.Config;
using GameFramework.Event;
using GameFramework.Log;
using GameFramework.Resource;
using GameFramework.Storage;
using UnityEngine;

namespace GameFramework.Localization
{
    /// <summary>
    /// 本地化管理器：当前语言 + 文本 / 图片地址 / 字体地址查询 + 换语言。
    ///
    /// 数据全在配置表里（Excel 驱动）：
    ///   LocZh / LocEn ... 每个语言一张表，同一 key 存「文本 + 图片地址」；
    ///   LocFont 语言 → 字体地址。
    /// 取不到 key 时回退默认语言，再回退就原样输出 key（抓漏网之鱼）。
    /// 字体按当前语言异步加载并缓存，加载完发 LanguageChangedEvent 让界面刷新。
    /// </summary>
    public sealed class LocalizationManager : IGameModule
    {
        private readonly LocalizationOptions _options;
        private readonly ConfigManager _config;
        private readonly IResourceService _resource;
        private readonly IEventBus _events;
        private readonly LocalStorageManager _storage;

        private readonly Dictionary<string, Func<string, LocEntry>> _tables =
            new Dictionary<string, Func<string, LocEntry>>(StringComparer.Ordinal);

        private string _language;
        private string _fallback;
        private bool _initialized;
        private ResourceAsset<Font> _fontAsset;
        private Font _editorFont;          // 编辑器兜底用的动态系统字体（不进包）
        private bool _fontWarned;

        public LocalizationManager(LocalizationOptions options, ConfigManager config,
            IResourceService resource, IEventBus events, LocalStorageManager storage)
        {
            _options = options ?? new LocalizationOptions();
            _config = config;
            _resource = resource;
            _events = events;
            _storage = storage;
        }

        /// <summary>当前语言（如 "zh-CN"）。</summary>
        public string Language { get { return _language; } }

        /// <summary>回退语言。</summary>
        public string FallbackLanguage { get { return _fallback; } }

        /// <summary>当前语言字体（异步加载，未就绪 / 未配置时是 null，用默认字体）。</summary>
        public Font Font
        {
            get
            {
                if (_editorFont != null)
                    return _editorFont;
                return _fontAsset != null ? _fontAsset.Asset : null;
            }
        }

        /// <summary>
        /// 初始化：登记语言表 → 从存档读语言（没有用默认）→ 加载字体。
        /// 配置表加载完之后（ProcedureInitLocalization）调用。
        /// </summary>
        public void Init()
        {
            RegisterTables();

            _fallback = Clamp(_options.FallbackLanguage);
            string stored = _storage != null && _storage.IsInitialized ? _storage.Settings.LanguageCode : null;
            _language = Clamp(string.IsNullOrEmpty(stored) ? _options.DefaultLanguage : stored);
            if (_language == null)
                _language = _options.DefaultLanguage;

            _initialized = true;
            Current = this;
            GameLog.Info(LogTag.Localization, "本地化就绪：语言=" + _language + "，回退=" + _fallback);
            LoadFont();
        }

        /// <summary>是否已经初始化完成。</summary>
        public bool IsInitialized { get { return _initialized; } }

        /// <summary>
        /// 全局登记的实例（由 Init / Shutdown 自己维护）。
        /// 框架内的本地化组件用它，从而不必引用业务层的 GameController。
        /// </summary>
        public static LocalizationManager Current { get; private set; }

        /// <summary>资源服务（就是初始化时传进来的那个）。本地化图片要用它加载。</summary>
        public IResourceService Resource { get { return _resource; } }

        /// <summary>切语言：改存档 + 加载对应字体 + 发事件，界面上的 LocalizedElement 自动刷新。</summary>
        public void SetLanguage(string language)
        {
            string lang = Clamp(language);
            if (lang == null)
            {
                GameLog.Warn(LogTag.Localization, "不支持的语言：" + language + "，忽略。");
                return;
            }

            if (lang == _language)
                return;

            _language = lang;

            if (_storage != null && _storage.IsInitialized)
            {
                _storage.Settings.LanguageCode = lang;
                _storage.Save(_storage.SettingsFile);
            }

            LoadFont();
            Publish(new LanguageChangedEvent { Language = lang });
        }

        /// <summary>按当前语言取文本。缺 key 回退默认语言，再缺就原样输出 key。</summary>
        public string GetText(string key)
        {
            return Resolve(key).Text ?? key;
        }

        /// <summary>按当前语言取图片资源地址。没有图片返回空串。</summary>
        public string GetImageAddress(string key)
        {
            return Resolve(key).Image;
        }

        /// <summary>某语言的字体资源地址（LocFont 表）。</summary>
        public string GetFontAddress(string language)
        {
            if (_config == null || _config.Database == null)
                return null;
            LocFontConfig row = _config.Database.LocFont.Get(language);
            return row != null ? row.FontAddress : null;
        }

        /// <summary>关闭：释放缓存的字体资源。幂等。</summary>
        public void Shutdown()
        {
            _initialized = false;
            if (Current == this) Current = null;

            if (_fontAsset != null)
            {
                _fontAsset.Dispose();
                _fontAsset = null;
            }
            _editorFont = null;
        }

        // ---------------- 查询 ----------------

        private LocEntry Resolve(string key)
        {
            if (string.IsNullOrEmpty(key))
                return default;

            LocEntry entry = Lookup(_language, key);
            if (!entry.IsEmpty)
                return entry;

            if (_fallback != null && _fallback != _language)
            {
                entry = Lookup(_fallback, key);
                if (!entry.IsEmpty)
                    return entry;
            }

            return new LocEntry(key, null);
        }

        private LocEntry Lookup(string lang, string key)
        {
            if (lang == null)
                return default;

            Func<string, LocEntry> resolver;
            if (!_tables.TryGetValue(lang, out resolver))
                return default;

            return resolver(key);
        }

        // ---------------- 语言表登记 ----------------
        // 加一种语言：1) 加 LocXx.xlsx 并导出；2) 在这里加一行。
        // 主键已在 Excel 里按 ##group=B 声明，两端都能查。

        private void RegisterTables()
        {
            if (_config == null || _config.Database == null)
                return;

            Register("zh-CN", _config.Database.LocZh);
            Register("en-US", _config.Database.LocEn);
        }

        private void Register(string lang, TbLocZh table)
        {
            _tables[lang] = key => Row(table, key, r => new LocEntry(r.Text, r.Image));
        }

        private void Register(string lang, TbLocEn table)
        {
            _tables[lang] = key => Row(table, key, r => new LocEntry(r.Text, r.Image));
        }

        private static LocEntry Row<T>(ConfigTable<string, T> table, string key, Func<T, LocEntry> map) where T : class
        {
            T row = table.Get(key);
            return row == null ? default : map(row);
        }

        // ---------------- 语言校验 ----------------

        private string Clamp(string lang)
        {
            if (lang == null)
                return null;

            if (_options.SupportedLanguages == null || _options.SupportedLanguages.Length == 0)
                return lang;

            for (int i = 0; i < _options.SupportedLanguages.Length; i++)
            {
                if (_options.SupportedLanguages[i] == lang)
                    return lang;
            }

            return null;
        }

        // ---------------- 字体 ----------------

        private void LoadFont()
        {
            if (_resource == null)
                return;

            if (_fontAsset != null)
            {
                _fontAsset.Dispose();
                _fontAsset = null;
            }

            string addr = GetFontAddress(_language);
            if (string.IsNullOrEmpty(addr))
            {
                // 该语言没配字体：编辑器里用系统字体兜底（开发期看中文用），否则发事件按默认字体刷
                TryEditorFontFallback("该语言没配字体地址");
                return;
            }

            // 有真实字体地址：清掉编辑器兜底，走资源加载
            _editorFont = null;

            _resource.LoadAssetAsync<Font>(addr, asset =>
            {
                if (asset == null)
                {
                    GameLog.Warn(LogTag.Localization, "字体加载失败（地址：" + addr + "），使用默认字体。");
                    TryEditorFontFallback("地址加载失败：" + addr);
                    return;
                }

                _fontAsset = asset;
                GameLog.Info(LogTag.Localization, "字体就绪：" + addr);
                Publish(new LanguageChangedEvent { Language = _language });
            });
        }

        /// <summary>
        /// 编辑器兜底：没配真实字体时用系统动态字体（微软雅黑 / 苹方 / Noto CJK），
        /// 让开发期界面能显示中文。只在编辑器生效，正式包不受影响。
        /// </summary>
        private void TryEditorFontFallback(string reason)
        {
#if UNITY_EDITOR
            if (!_options.EditorFontFallback || _editorFont != null)
            {
                Publish(new LanguageChangedEvent { Language = _language });
                return;
            }

            string osFont = EditorOsFontName();
            if (!string.IsNullOrEmpty(osFont))
            {
                Font f = Font.CreateDynamicFontFromOSFont(osFont, 24);
                if (f != null)
                {
                    _editorFont = f;
                    GameLog.Info(LogTag.Localization, "编辑器兜底字体：" + osFont + "（" + reason + "）。正式包请配 LocFont 或加 EditorFontFallback=false。");
                }
            }
#endif
            Publish(new LanguageChangedEvent { Language = _language });
        }

#if UNITY_EDITOR
        private static string EditorOsFontName()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor: return "Microsoft YaHei";
                case RuntimePlatform.OSXEditor: return "PingFang SC";
                case RuntimePlatform.LinuxEditor: return "Noto Sans CJK SC";
                default: return null;
            }
        }
#endif

        private void Publish<T>(T evt) where T : IGameEvent
        {
            if (_events != null)
                _events.Publish(evt);
        }
    }

    /// <summary>一条本地化数据：文本 + 图片地址（都可能是 null）。</summary>
    public readonly struct LocEntry
    {
        public readonly string Text;
        public readonly string Image;

        public LocEntry(string text, string image)
        {
            Text = text;
            Image = image;
        }

        /// <summary>文本和图片都没有 → 这张表里没有这个 key。</summary>
        public bool IsEmpty
        {
            get { return Text == null && Image == null; }
        }
    }
}
