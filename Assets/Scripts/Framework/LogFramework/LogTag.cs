namespace GameFramework.Log
{
    /// <summary>模块标签。集中登记，免得各处拼字符串写错，也方便按模块静音。</summary>
    public static class LogTag
    {
        public const string Root = "ROOT";
        public const string Game = "Game";
        public const string Startup = "Startup";
        public const string Procedure = "Procedure";
        public const string Storage = "Storage";
        public const string Resource = "Resource";
        public const string Config = "Config";
        public const string Pool = "ObjectPool";
        public const string UI = "UI";
        public const string UIRoot = "UIRoot";
        public const string UIManager = "UIManager";
        public const string Panel = "Panel";
        public const string UIAnim = "UIAnim";
        public const string Net = "NET";
        public const string Hub = "HUB";
        public const string EventBus = "EventBus";
        public const string Business = "Biz";
        public const string Timer = "Timer";
        public const string Audio = "Audio";
        public const string Scene = "Scene";
        public const string Platform = "Platform";
        public const string Localization = "Loc";
    }
}