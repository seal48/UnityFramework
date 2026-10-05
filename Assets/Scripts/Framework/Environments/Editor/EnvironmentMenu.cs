using System.IO;
using GameFramework.Environments;
using UnityEditor;
using UnityEngine;

namespace GameFramework.Environments.Editor
{
    /// <summary>
    /// 环境配置的编辑器入口：切换当前环境、创建配置资产。
    /// 构建相关在 <see cref="EnvironmentBuilder"/>。
    /// </summary>
    public static class EnvironmentMenu
    {
        private const string AssetFolder = "Assets/Resources";
        private const string AssetPath = AssetFolder + "/" + EnvironmentConfig.ResourceName + ".asset";

        private const string SwitchRoot = "Tools/环境/切换到/";
        private const int SwitchPriority = 20;

        [MenuItem("Tools/环境/当前环境", false, 1)]
        private static void ShowCurrent()
        {
            EnvironmentConfig cfg = Load();
            if (cfg == null)
            {
                Debug.LogWarning("[Environment] 还没有环境配置资产。先执行菜单 Tools/环境/创建配置资产。");
                return;
            }

            EnvironmentEntry e = cfg.Current;
            Debug.Log("[Environment] 当前环境：" + e.DisplayName +
                      "\n  服务器      " + e.ServerHost + ":" + e.ServerPort +
                      "\n  资源模式    " + e.ResourceMode + "  主站 " + (string.IsNullOrEmpty(e.ResourceHostUrl) ? "(空)" : e.ResourceHostUrl) +
                      "\n  日志级别    " + e.LogLevel + "   推送日志 " + e.LogPushMessages +
                      "\n  存档目录    " + (e.StorageInGameFolder ? "游戏目录旁（调试用）" : "平台标准目录") +
                      "\n  资产路径    " + AssetPath);
        }

        [MenuItem("Tools/环境/创建配置资产", false, 2)]
        private static void CreateAsset()
        {
            if (File.Exists(AssetPath))
            {
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<EnvironmentConfig>(AssetPath);
                EditorGUIUtility.PingObject(Selection.activeObject);
                Debug.Log("[Environment] 配置资产已存在：" + AssetPath);
                return;
            }

            if (!Directory.Exists(AssetFolder))
                Directory.CreateDirectory(AssetFolder);

            EnvironmentConfig asset = ScriptableObject.CreateInstance<EnvironmentConfig>();
            AssetDatabase.CreateAsset(asset, AssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            Debug.Log("[Environment] 已创建配置资产：" + AssetPath + "（三套环境的默认值请按需修改）");
        }

        [MenuItem("Tools/环境/打开配置资产", false, 3)]
        private static void OpenAsset()
        {
            EnvironmentConfig cfg = Load();
            if (cfg == null) { CreateAsset(); return; }

            Selection.activeObject = cfg;
            EditorGUIUtility.PingObject(cfg);
        }

        // ---------------- 切换环境 ----------------

        [MenuItem(SwitchRoot + "开发（Development）", false, SwitchPriority)]
        private static void SwitchToDevelopment() { SwitchTo(GameEnvironment.Development); }

        [MenuItem(SwitchRoot + "开发（Development）", true)]
        private static bool ValidateDevelopment() { return MarkIfActive(GameEnvironment.Development); }

        [MenuItem(SwitchRoot + "测试（Test）", false, SwitchPriority + 1)]
        private static void SwitchToTest() { SwitchTo(GameEnvironment.Test); }

        [MenuItem(SwitchRoot + "测试（Test）", true)]
        private static bool ValidateTest() { return MarkIfActive(GameEnvironment.Test); }

        [MenuItem(SwitchRoot + "正式（Production）", false, SwitchPriority + 2)]
        private static void SwitchToProduction() { SwitchTo(GameEnvironment.Production); }

        [MenuItem(SwitchRoot + "正式（Production）", true)]
        private static bool ValidateProduction() { return MarkIfActive(GameEnvironment.Production); }

        /// <summary>给当前环境打勾（validate 函数里调用）。资产不存在时这些菜单直接禁用。</summary>
        private static bool MarkIfActive(GameEnvironment env)
        {
            EnvironmentConfig cfg = Load();
            if (cfg == null)
                return false;

            Menu.SetChecked(SwitchRoot + MenuName(env), cfg.Active == env);
            return true;
        }

        private static string MenuName(GameEnvironment env)
        {
            switch (env)
            {
                case GameEnvironment.Test: return "测试（Test）";
                case GameEnvironment.Production: return "正式（Production）";
                default: return "开发（Development）";
            }
        }

        /// <summary>切换当前环境（编辑器内立即生效，下次 Play 用它）。</summary>
        public static void SwitchTo(GameEnvironment env)
        {
            EnvironmentConfig cfg = Load();
            if (cfg == null)
            {
                Debug.LogError("[Environment] 没有配置资产，先执行菜单 Tools/环境/创建配置资产。");
                return;
            }

            if (cfg.Active == env)
            {
                Debug.Log("[Environment] 已经是 " + cfg.Current.DisplayName + " 环境。");
                return;
            }

            cfg.Active = env;
            EditorUtility.SetDirty(cfg);
            AssetDatabase.SaveAssets();
            EnvironmentConfig.ResetCache();

            EnvironmentEntry e = cfg.Current;
            Debug.Log("[Environment] 已切换到 " + e.DisplayName +
                      "：服务器 " + e.ServerHost + ":" + e.ServerPort +
                      "，资源 " + e.ResourceMode +
                      "，日志 " + e.LogLevel);
        }

        /// <summary>读资产（不走运行时缓存，编辑器里改了立刻能读到）。</summary>
        private static EnvironmentConfig Load()
        {
            return AssetDatabase.LoadAssetAtPath<EnvironmentConfig>(AssetPath);
        }
    }
}
