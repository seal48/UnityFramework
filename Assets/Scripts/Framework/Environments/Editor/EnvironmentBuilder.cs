using System;
using System.Collections.Generic;
using GameFramework.Environments;
using GameFramework.Resource;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace GameFramework.Environments.Editor
{
    /// <summary>
    /// 按环境打包：构建前把选中的环境写进 <see cref="EnvironmentConfig"/>，构建完再还原编辑器里的选择。
    ///
    /// 为什么可行：EnvironmentConfig 是 Resources 下的资产，构建时它的值会被序列化进包，
    /// 所以「构建时切换」是可靠的 —— 正式包不会意外连到开发服。
    ///
    /// 命令行（CI）：
    ///   Unity.exe -quit -batchmode -projectPath &lt;工程&gt; \
    ///     -executeMethod GameFramework.Environments.Editor.EnvironmentBuilder.BuildFromCommandLine \
    ///     -environment Production \
    ///     -buildTarget Android
    ///
    /// <c>-environment</c> 接受 Development / Test / Production（也认 开发 / 测试 / 正式）。
    /// </summary>
    public static class EnvironmentBuilder
    {
        private const string MenuRoot = "Tools/环境/构建/";
        private const string ConfigAssetPath = "Assets/Resources/" + EnvironmentConfig.ResourceName + ".asset";

        [MenuItem(MenuRoot + "开发（Development）", false, 100)]
        private static void BuildDevelopment() { Build(GameEnvironment.Development); }

        [MenuItem(MenuRoot + "测试（Test）", false, 101)]
        private static void BuildTest() { Build(GameEnvironment.Test); }

        [MenuItem(MenuRoot + "正式（Production）", false, 102)]
        private static void BuildProduction() { Build(GameEnvironment.Production); }

        /// <summary>按指定环境构建当前平台，输出到 Builds/&lt;环境名&gt;/。</summary>
        public static void Build(GameEnvironment env)
        {
            EnvironmentConfig cfg = AssetDatabase.LoadAssetAtPath<EnvironmentConfig>(ConfigAssetPath);
            if (cfg == null)
            {
                Debug.LogError("[Environment] 找不到 " + ConfigAssetPath + "，先执行菜单 Tools/环境/创建配置资产。");
                return;
            }

            string[] scenes = EnabledScenes();
            if (scenes.Length == 0)
            {
                Debug.LogError("[Environment] Build Settings 里没有启用的场景，无法构建。");
                return;
            }

            GameEnvironment previous = cfg.Active;
            EnvironmentEntry entry = cfg.Get(env);

            if (!GuardBeforeBuild(entry, env))
                return;   // 环境不适合打包：中止，且不改动资产

            WarnIfPlaceholder(entry, env);

            string outputDir = "Builds/" + env;
            string outputPath = outputDir + "/" + PlayerSettings.productName + ExtensionFor(EditorUserBuildSettings.activeBuildTarget);

            // 先登记"构建完要还原成哪个环境"。
            // 光靠 finally 不够：Android 构建失败时 Unity 会弹**模态错误框**（GradleInvokationException
            // .ParseAndShowException），主线程被卡住 → finally 根本没机会跑，环境就被留在构建用的那套上。
            // 所以再挂一个 delayCall 兜底：编辑器一恢复就还原。
            ScheduleRestore(previous);

            try
            {
                // 1) 把目标环境写进资产（构建时会被烤进包）
                cfg.Active = env;
                EditorUtility.SetDirty(cfg);
                AssetDatabase.SaveAssets();
                EnvironmentConfig.ResetCache();

                System.IO.Directory.CreateDirectory(outputDir);

                Debug.Log("[Environment] 开始构建 " + entry.DisplayName +
                          "\n  平台     " + EditorUserBuildSettings.activeBuildTarget +
                          "\n  服务器   " + entry.ServerHost + ":" + entry.ServerPort +
                          "\n  资源     " + entry.ResourceMode + "  " + entry.ResourceHostUrl +
                          "\n  日志     " + entry.LogLevel +
                          "\n  输出     " + outputPath);

                var options = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = outputPath,
                    target = EditorUserBuildSettings.activeBuildTarget,
                    options = BuildOptions.None,
                };

                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;

                if (summary.result == BuildResult.Succeeded)
                {
                    Debug.Log("[Environment] 构建成功：" + outputPath +
                              "（" + (summary.totalSize / 1024f / 1024f).ToString("F1") + " MB，" +
                              summary.totalTime.TotalSeconds.ToString("F0") + "s）");
                }
                else
                {
                    Debug.LogError("[Environment] 构建失败：" + summary.result +
                                   "，错误 " + summary.totalErrors + " 个");
                }
            }
            finally
            {
                // 2) 还原编辑器里的环境选择（包里已经是目标环境，不受影响）
                RestoreEnvironment();
            }
        }

        /// <summary>待还原的环境；(int) 为负表示没有待还原的任务。</summary>
        private static GameEnvironment pendingRestore = (GameEnvironment)(-1);

        /// <summary>
        /// 打包前的硬性检查。返回 false = 不能打包（已说明原因）。
        ///
        /// 目前只有一条：**EditorSimulate 只能在编辑器里用**。资源服务里它是 `#if UNITY_EDITOR` 分支，
        /// 真机会直接报错返回 null，打出来的包进不去游戏。所以开发环境（默认 EditorSimulate）不能打包 ——
        /// 要出包就切测试 / 正式环境，或者把该环境的资源模式改成 Host / Offline。
        /// </summary>
        private static bool GuardBeforeBuild(EnvironmentEntry entry, GameEnvironment env)
        {
            if (entry.ResourceMode == ResourcePlayMode.EditorSimulate)
            {
                Debug.LogError("[Environment] 不能打包：" + env + "（" + entry.DisplayName + "）的资源模式是 EditorSimulate，" +
                               "它只在编辑器里有效 —— 打出来的包会加载不了资源。\n" +
                               "  要出包请二选一：\n" +
                               "    1) 换环境：Tools/环境/构建/测试｜正式\n" +
                               "    2) 把该环境的资源模式改成 Host（或 Offline），再构建");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 构建前检查地址是不是"本机 / 占位"值。开发期指向本地服务器是正常的，
        /// 但**正式包**指向 localhost / example.com 一定是忘了改 —— 这里至少提醒一句。
        /// </summary>
        private static void WarnIfPlaceholder(EnvironmentEntry entry, GameEnvironment env)
        {
            bool localServer = IsLocalOrPlaceholder(entry.ServerHost);
            bool localResource = IsLocalOrPlaceholder(entry.ResourceHostUrl);

            if (!localServer && !localResource)
                return;

            string what = localServer && localResource ? "服务器和资源站"
                        : localServer ? "服务器" : "资源站";

            string message = "[Environment] 注意：" + env + " 环境的" + what + "还是本机 / 占位地址" +
                             "（" + (localServer ? entry.ServerHost : "") +
                             (localServer && localResource ? "  " : "") +
                             (localResource ? entry.ResourceHostUrl : "") + "）。";

            if (env == GameEnvironment.Production)
                Debug.LogError(message + " 正式包请先把 EnvironmentConfig.asset 里的地址改成真实线上地址。");
            else
                Debug.LogWarning(message + " 本地联调可以忽略；发布前记得替换。");
        }

        private static bool IsLocalOrPlaceholder(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            return value.Contains("127.0.0.1")
                || value.Contains("localhost")
                || value.Contains("example.com")
                || value.Contains("0.0.0.0");
        }

        /// <summary>登记还原任务：finally 会调一次，delayCall 再兜一次（防构建卡在模态框里）。</summary>
        private static void ScheduleRestore(GameEnvironment previous)
        {
            pendingRestore = previous;
            EditorApplication.delayCall -= RestoreEnvironment;
            EditorApplication.delayCall += RestoreEnvironment;
        }

        /// <summary>把编辑器里的环境还原成构建前的那套。幂等，没有待还原任务时什么都不做。</summary>
        private static void RestoreEnvironment()
        {
            EditorApplication.delayCall -= RestoreEnvironment;

            if ((int)pendingRestore < 0)
                return;

            GameEnvironment target = pendingRestore;
            pendingRestore = (GameEnvironment)(-1);

            EnvironmentConfig cfg = AssetDatabase.LoadAssetAtPath<EnvironmentConfig>(ConfigAssetPath);
            if (cfg == null || cfg.Active == target)
                return;

            cfg.Active = target;
            EditorUtility.SetDirty(cfg);
            AssetDatabase.SaveAssets();
            EnvironmentConfig.ResetCache();

            Debug.Log("[Environment] 编辑器环境已还原为 " + cfg.Get(target).DisplayName + "。");
        }

        /// <summary>命令行入口：<c>-executeMethod GameFramework.Environments.Editor.EnvironmentBuilder.BuildFromCommandLine</c></summary>
        public static void BuildFromCommandLine()
        {
            GameEnvironment env = ParseEnvironmentArg();
            Debug.Log("[Environment] 命令行构建，环境 = " + env);
            Build(env);
        }

        /// <summary>解析 -environment 参数（认英文和中文名），缺省用配置里当前的环境。</summary>
        private static GameEnvironment ParseEnvironmentArg()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], "-environment", StringComparison.OrdinalIgnoreCase))
                    continue;

                string value = args[i + 1].Trim();
                switch (value.ToLowerInvariant())
                {
                    case "development":
                    case "dev":
                    case "开发":
                        return GameEnvironment.Development;
                    case "test":
                    case "测试":
                        return GameEnvironment.Test;
                    case "production":
                    case "release":
                    case "正式":
                        return GameEnvironment.Production;
                    default:
                        Debug.LogWarning("[Environment] 认不出 -environment 的值：" + value + "，改用配置里的当前环境。");
                        break;
                }
            }

            EnvironmentConfig cfg = AssetDatabase.LoadAssetAtPath<EnvironmentConfig>(ConfigAssetPath);
            return cfg != null ? cfg.Active : GameEnvironment.Development;
        }

        /// <summary>Build Settings 里启用的场景，按原顺序。</summary>
        private static string[] EnabledScenes()
        {
            var list = new List<string>();
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            for (int i = 0; i < scenes.Length; i++)
            {
                if (scenes[i].enabled)
                    list.Add(scenes[i].path);
            }
            return list.ToArray();
        }

        private static string ExtensionFor(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                    return ".exe";
                case BuildTarget.Android:
                    return ".apk";
                case BuildTarget.iOS:
                    return "";
                case BuildTarget.WebGL:
                    return "";
                default:
                    return "";
            }
        }
    }
}
