using System;
using UnityEngine;

namespace GameFramework.Storage
{
    /// <summary>存档根目录放哪。</summary>
    public enum StorageRootMode
    {
        /// <summary>平台标准目录 Application.persistentDataPath。安卓只能用它。</summary>
        PersistentData = 0,

        /// <summary>游戏目录旁边（PC/编辑器是 exe 或工程根目录），方便直接翻看存档；目录不可写时自动回退到 PersistentData。</summary>
        GameFolder = 1,
    }
    /// <summary>本地存储的配置。由 GameController 填好后传给 LocalStorageManager.Init。</summary>
    [Serializable]
    public sealed class StorageOptions
    {
        [Tooltip("存档目录名，最终路径是 {persistentDataPath}/{FolderName}")]
        public string FolderName = "LocalData";

        [Tooltip("自定义根目录（留空 = Application.persistentDataPath）。调试 / 自动化测试可以指到临时目录")]
        public string RootPathOverride = string.Empty;

        [Tooltip("存档根目录：PersistentData = 平台标准目录（安卓用这个）；GameFolder = 游戏目录旁边（PC/编辑器方便翻看），不可写时自动回退")]
        public StorageRootMode RootMode = StorageRootMode.PersistentData;

        [Tooltip("数据改动后隔多少秒落盘。0 = 每次改动立刻落盘（最稳，写盘最频繁）")]
        public float AutoFlushDelay = 1f;

        [Tooltip("切后台 / 失去焦点时立刻落盘。移动端建议保持打开：系统可能随时杀进程")]
        public bool FlushOnPause = true;

        [Tooltip("退出时立刻落盘")]
        public bool FlushOnQuit = true;

        [Tooltip("JSON 缩进输出，方便用文本编辑器直接看存档内容（开启加密后这个字段只影响内存 JSON，落盘内容不可读）")]
        public bool PrettyJson = true;

        [Tooltip("存档落盘加密（AES，见 SecurityFramework）。加密后存档文件不可直接改/看；关闭则明文可改。")]
        public bool EnableEncryption = true;

        [Tooltip("存档解析失败时，把坏文件改名成 .bad 备份，再按默认值重建")]
        public bool BackupCorruptFile = true;

        [Tooltip("初始化完成后打一条日志")]
        public bool LogOnInit = true;

        public StorageOptions Clone()
        {
            return new StorageOptions
            {
                FolderName = FolderName,
                RootPathOverride = RootPathOverride,
                RootMode = RootMode,
                AutoFlushDelay = AutoFlushDelay,
                FlushOnPause = FlushOnPause,
                FlushOnQuit = FlushOnQuit,
                PrettyJson = PrettyJson,
                EnableEncryption = EnableEncryption,
                BackupCorruptFile = BackupCorruptFile,
                LogOnInit = LogOnInit,
            };
        }
    }
}