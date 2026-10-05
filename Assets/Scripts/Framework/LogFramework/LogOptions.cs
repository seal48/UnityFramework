using System;
using UnityEngine;

namespace GameFramework.Log
{
    /// <summary>日志配置。由 GameController 填好后传给 GameLog.Init。</summary>
    [Serializable]
    public sealed class LogOptions
    {
        [Tooltip("真机 / 正式包的最低输出级别。数值越大越啰嗦")]
        public LogLevel MinLevel = LogLevel.Info;

        [Tooltip("编辑器 / Development Build 下的最低输出级别，一般比正式包啰嗦")]
        public LogLevel MinLevelInDevelopment = LogLevel.Debug;

        [Tooltip("控制台带颜色。真机 logcat 会显示成一堆转义字符，所以建议真机关掉")]
        public bool ColoredConsole = true;

        [Tooltip("把日志写进文件。真机排查主要靠它")]
        public bool WriteToFile = true;

        [Tooltip("日志目录名")]
        public string FolderName = "GameLogs";

        [Tooltip("日志根目录（留空 = Application.persistentDataPath/FolderName）")]
        public string RootPathOverride = string.Empty;

        [Tooltip("最多保留几个日志文件（按天一个），超了删最旧的")]
        public int MaxFileCount = 7;

        [Tooltip("内存里保留最近多少条：游戏内控制台 / 崩溃上报从这里取")]
        public int MaxHistory = 500;

        [Tooltip("攒多久落盘一次（秒）。Error / Fatal 不等，立刻写")]
        public float FlushInterval = 2f;

        [Tooltip("要静音的模块标签（见 LogTag），比如压测时把 UI 关掉")]
        public string[] MutedTags = new string[0];

        [Tooltip("Error / Fatal 附带调用堆栈（写文件用，控制台由 Unity 自己附）")]
        public bool CaptureStackTrace = true;
    }
}