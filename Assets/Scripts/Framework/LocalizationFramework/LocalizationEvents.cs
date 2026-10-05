using GameFramework.Event;

namespace GameFramework.Localization
{
    /// <summary>
    /// 语言切换 / 字体就绪后发出。LocalizedElement 听它刷新文本 + 图片 + 字体。
    /// </summary>
    public struct LanguageChangedEvent : IGameEvent
    {
        public string Language;
    }
}
