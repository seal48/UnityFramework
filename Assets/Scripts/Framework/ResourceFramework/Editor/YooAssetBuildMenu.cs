using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using YooAsset;
using YooAsset.Editor;

namespace GameFramework.Resource.EditorTools
{
    /// <summary>
    /// YooAsset 收集器配置 + 一键构建 + 同步到本地静态服务器目录。
    /// 菜单都在 Tools/YooAsset 下面。
    ///
    /// 产物目录结构（YooAsset 默认）：
    ///   {工程根}/Bundles/{平台}/{包名}/{版本}/     ← 本次构建的产物（要传到服务器）
    ///   {工程根}/Bundles/{平台}/{包名}/OutputCache  ← 构建缓存，不用管
    ///   Assets/StreamingAssets/yoo/{包名}/          ← 首包内置文件（按拷贝选项生成）
    /// </summary>
    public static class YooAssetBuildMenu
    {
        /// <summary>资源包名，必须和 ResourceInitOptions.PackageName 一致。</summary>
        public const string PackageName = "DefaultPackage";

        /// <summary>收集的目录，UI 预制体都在这里。</summary>
        private static readonly string[] CollectFolders = { "Assets/Prefabs" };

        /// <summary>配置表数据目录（.bytes，由配置表导出工具生成）。不是 prefab，所以单独一组，用 CollectAll 全收。</summary>
        private static readonly string[] ConfigCollectFolders = { "Assets/ConfigData" };

        /// <summary>场景目录（只收 .unity）。</summary>
        private static readonly string[] SceneCollectFolders = { "Assets/Scenes" };

        /// <summary>本地静态服务器根目录（相对工程根目录）。</summary>
        private const string LocalServerRoot = "ServerData";

        /// <summary>构建缓存目录名，扫描版本目录时要跳过它。</summary>
        private const string OutputCacheFolderName = "OutputCache";


        // ============================== 1. 收集器 ==============================

        [MenuItem("Tools/YooAsset/1. 配置资源收集器（首次运行）", false, 1)]
        public static void SetupCollector()
        {
            AssetBundleCollectorSetting setting = AssetBundleCollectorSettingData.Setting;
            if (setting == null)
            {
                Debug.LogError("[YooAsset] 读取收集器配置失败");
                return;
            }

            AssetBundleCollectorPackage package = null;
            for (int i = 0; i < setting.Packages.Count; i++)
            {
                if (setting.Packages[i].PackageName == PackageName)
                    package = setting.Packages[i];
            }

            if (package == null)
                package = AssetBundleCollectorSettingData.CreatePackage(PackageName);

            package.PackageDesc = "默认资源包";
            package.EnableAddressable = false;   // 关掉：直接用资源路径当加载地址，业务里写的 "Assets/Prefabs/xxx.prefab" 就能用
            package.SupportExtensionless = true; // 允许不带后缀的地址
            package.LocationToLower = false;
            package.IncludeAssetGUID = false;
            package.AutoCollectShaders = true;
            package.IgnoreRuleName = nameof(NormalIgnoreRule);

            AssetBundleCollectorGroup group = null;
            for (int i = 0; i < package.Groups.Count; i++)
            {
                if (package.Groups[i].GroupName == "Prefabs")
                    group = package.Groups[i];
            }

            if (group == null)
                group = AssetBundleCollectorSettingData.CreateGroup(package, "Prefabs");

            group.GroupDesc = "UI 预制体";
            group.AssetTags = "prefab";
            group.ActiveRuleName = nameof(EnableGroup);

            for (int i = 0; i < CollectFolders.Length; i++)
            {
                string folder = CollectFolders[i];
                if (AssetDatabase.IsValidFolder(folder) == false)
                {
                    Debug.LogWarning("[YooAsset] 收集目录不存在，已跳过：" + folder);
                    continue;
                }

                bool exists = false;
                for (int j = 0; j < group.Collectors.Count; j++)
                {
                    if (group.Collectors[j].CollectPath == folder)
                        exists = true;
                }

                if (exists)
                    continue;

                AssetBundleCollector collector = new AssetBundleCollector();
                collector.CollectPath = folder;
                collector.CollectorGUID = AssetDatabase.AssetPathToGUID(folder);
                collector.CollectorType = ECollectorType.MainAssetCollector;
                collector.AddressRuleName = nameof(AddressByFileName);
                collector.PackRuleName = nameof(PackDirectory);
                collector.FilterRuleName = nameof(CollectPrefab);
                collector.AssetTags = "prefab";
                AssetBundleCollectorSettingData.CreateCollector(group, collector);
            }

            // 配置表：单独一组，CollectAll 收所有文件（.bytes 不在 CollectPrefab 的范围内）
            AssetBundleCollectorGroup configGroup = null;
            for (int i = 0; i < package.Groups.Count; i++)
            {
                if (package.Groups[i].GroupName == "Config")
                    configGroup = package.Groups[i];
            }

            if (configGroup == null)
                configGroup = AssetBundleCollectorSettingData.CreateGroup(package, "Config");

            configGroup.GroupDesc = "配置表数据";
            configGroup.AssetTags = "config";
            configGroup.ActiveRuleName = nameof(EnableGroup);

            for (int i = 0; i < ConfigCollectFolders.Length; i++)
            {
                string folder = ConfigCollectFolders[i];
                if (AssetDatabase.IsValidFolder(folder) == false)
                {
                    Debug.LogWarning("[YooAsset] 配置目录不存在，已跳过：" + folder);
                    continue;
                }

                bool exists = false;
                for (int j = 0; j < configGroup.Collectors.Count; j++)
                {
                    if (configGroup.Collectors[j].CollectPath == folder)
                        exists = true;
                }

                if (exists)
                    continue;

                AssetBundleCollector collector = new AssetBundleCollector();
                collector.CollectPath = folder;
                collector.CollectorGUID = AssetDatabase.AssetPathToGUID(folder);
                collector.CollectorType = ECollectorType.MainAssetCollector;
                collector.AddressRuleName = nameof(AddressByFileName);
                collector.PackRuleName = nameof(PackDirectory);
                collector.FilterRuleName = nameof(CollectAll);
                collector.AssetTags = "config";
                AssetBundleCollectorSettingData.CreateCollector(configGroup, collector);
            }

            // 场景：单独一组，只收 .unity
            AssetBundleCollectorGroup sceneGroup = null;
            for (int i = 0; i < package.Groups.Count; i++)
            {
                if (package.Groups[i].GroupName == "Scenes")
                    sceneGroup = package.Groups[i];
            }

            if (sceneGroup == null)
                sceneGroup = AssetBundleCollectorSettingData.CreateGroup(package, "Scenes");

            sceneGroup.GroupDesc = "场景（只收 .unity）";
            sceneGroup.AssetTags = "scene";
            sceneGroup.ActiveRuleName = nameof(EnableGroup);

            for (int i = 0; i < SceneCollectFolders.Length; i++)
            {
                string folder = SceneCollectFolders[i];
                if (AssetDatabase.IsValidFolder(folder) == false)
                {
                    Debug.LogWarning("[YooAsset] 场景目录不存在，已跳过：" + folder);
                    continue;
                }

                bool exists = false;
                for (int j = 0; j < sceneGroup.Collectors.Count; j++)
                {
                    if (sceneGroup.Collectors[j].CollectPath == folder)
                        exists = true;
                }

                if (exists)
                    continue;

                AssetBundleCollector collector = new AssetBundleCollector();
                collector.CollectPath = folder;
                collector.CollectorGUID = AssetDatabase.AssetPathToGUID(folder);
                collector.CollectorType = ECollectorType.MainAssetCollector;
                collector.AddressRuleName = nameof(AddressByFileName);
                collector.PackRuleName = nameof(PackDirectory);
                collector.FilterRuleName = nameof(CollectScene);
                collector.AssetTags = "scene";
                AssetBundleCollectorSettingData.CreateCollector(sceneGroup, collector);
            }

            AssetBundleCollectorSettingData.SaveFile();
            Debug.Log("[YooAsset] 收集器配置完成：包 " + PackageName + "，收集目录 " + string.Join("、", CollectFolders));
        }


        // ============================== 2. 构建 ==============================

        [MenuItem("Tools/YooAsset/2. 构建资源（Android + 拷进首包）", false, 11)]
        public static void BuildAndroidWithBuildin()
        {
            Build(BuildTarget.Android, EBuildinFileCopyOption.ClearAndCopyAll);
        }

        [MenuItem("Tools/YooAsset/3. 构建资源（Android，只走远端热更）", false, 12)]
        public static void BuildAndroidRemoteOnly()
        {
            Build(BuildTarget.Android, EBuildinFileCopyOption.None);
        }

        [MenuItem("Tools/YooAsset/4. 构建资源（当前平台，只走远端热更）", false, 13)]
        public static void BuildCurrentPlatform()
        {
            Build(EditorUserBuildSettings.activeBuildTarget, EBuildinFileCopyOption.None);
        }

        private static string Build(BuildTarget target, EBuildinFileCopyOption copyOption)
        {
            // YooAsset 的构建流程要求没有任何未保存的改动，这里先统一保存一次
            UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();

            AssetBundleCollectorSetting setting = AssetBundleCollectorSettingData.Setting;
            if (setting == null)
            {
                Debug.LogError("[YooAsset] 读取收集器配置失败，先跑一次「配置资源收集器」");
                return null;
            }

            string version = DateTime.Now.ToString("yyyyMMdd.HHmmss");
            string builtinShaderBundleName =
                DefaultPackRule.CreateShadersPackRuleResult().GetBundleName(PackageName, setting.UniqueBundleName);

            ScriptableBuildParameters buildParameters = new ScriptableBuildParameters();
            buildParameters.BuildOutputRoot = AssetBundleBuilderHelper.GetDefaultBuildOutputRoot();
            buildParameters.BuildinFileRoot = AssetBundleBuilderHelper.GetStreamingAssetsRoot();
            buildParameters.BuildPipeline = EBuildPipeline.ScriptableBuildPipeline.ToString();
            buildParameters.BuildBundleType = (int)EBuildBundleType.AssetBundle;
            buildParameters.BuildTarget = target;
            buildParameters.PackageName = PackageName;
            buildParameters.PackageVersion = version;
            buildParameters.EnableSharePackRule = true;
            buildParameters.VerifyBuildingResult = true;
            buildParameters.FileNameStyle = EFileNameStyle.HashName;
            buildParameters.BuildinFileCopyOption = copyOption;
            buildParameters.BuildinFileCopyParams = string.Empty;
            buildParameters.CompressOption = ECompressOption.LZ4;
            buildParameters.ClearBuildCacheFiles = false;
            buildParameters.UseAssetDependencyDB = true;
            buildParameters.BuiltinShadersBundleName = builtinShaderBundleName;

            ScriptableBuildPipeline pipeline = new ScriptableBuildPipeline();
            BuildResult buildResult = pipeline.Run(buildParameters, true);

            if (buildResult.Success == false)
            {
                Debug.LogError("[YooAsset] 构建失败：" + buildResult.FailedTask + "\n" + buildResult.ErrorInfo);
                return null;
            }

            Debug.Log("[YooAsset] 构建成功：" + target + " / " + PackageName + " / " + version +
                      "\n产物目录：" + buildResult.OutputPackageDirectory);
            return version;
        }


        // ============================== 3. 同步到本地服务器 ==============================

        [MenuItem("Tools/YooAsset/5. 同步产物到本地服务器目录", false, 21)]
        public static void SyncToLocalServer()
        {
            SyncToLocalServer(EditorUserBuildSettings.activeBuildTarget);
        }

        [MenuItem("Tools/YooAsset/6. 同步 Android 产物到本地服务器目录", false, 22)]
        public static void SyncAndroidToLocalServer()
        {
            SyncToLocalServer(BuildTarget.Android);
        }

        /// <summary>
        /// 把最新的构建产物平铺拷贝到 ServerData/{平台}/{包名}/。
        /// 平铺是关键：远端目录要一直保留历史版本的文件，只覆盖 DefaultPackage.version 这个版本指针，
        /// 老客户端才不会因为找不到旧文件而报错。
        /// </summary>
        public static void SyncToLocalServer(BuildTarget target)
        {
            string projectPath = Directory.GetParent(Application.dataPath).FullName;
            string platformRoot = Path.Combine(AssetBundleBuilderHelper.GetDefaultBuildOutputRoot(),
                target.ToString(), PackageName);

            if (Directory.Exists(platformRoot) == false)
            {
                Debug.LogError("[YooAsset] 找不到构建产物目录，先构建一次：" + platformRoot);
                return;
            }

            // 找最新的版本目录（跳过 OutputCache）
            string newestVersionDir = null;
            DateTime newestTime = DateTime.MinValue;
            string[] dirs = Directory.GetDirectories(platformRoot);
            for (int i = 0; i < dirs.Length; i++)
            {
                if (Path.GetFileName(dirs[i]) == OutputCacheFolderName)
                    continue;

                DateTime time = Directory.GetLastWriteTime(dirs[i]);
                if (time > newestTime)
                {
                    newestTime = time;
                    newestVersionDir = dirs[i];
                }
            }

            if (string.IsNullOrEmpty(newestVersionDir))
            {
                Debug.LogError("[YooAsset] 产物目录里没有版本文件夹：" + platformRoot);
                return;
            }

            string destRoot = Path.Combine(projectPath, LocalServerRoot, target.ToString(), PackageName);
            Directory.CreateDirectory(destRoot);

            int copied = 0;
            int skipped = 0;
            string[] files = Directory.GetFiles(newestVersionDir);
            for (int i = 0; i < files.Length; i++)
            {
                string fileName = Path.GetFileName(files[i]);

                // 构建产物里的日志/报告文件不需要传到服务器
                if (fileName == "buildlogtep.json" || fileName.EndsWith(".report"))
                {
                    skipped++;
                    continue;
                }

                File.Copy(files[i], Path.Combine(destRoot, fileName), true);
                copied++;
            }

            Debug.Log("[YooAsset] 已同步 " + copied + " 个文件到 " + destRoot + "（跳过 " + skipped + " 个日志文件）" +
                      "\nHostServerURL = http://127.0.0.1:8000/" + target + "/" + PackageName);
        }
    }
}
