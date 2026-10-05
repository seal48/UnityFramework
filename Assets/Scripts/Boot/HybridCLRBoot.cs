using System;
using System.IO;
using System.Reflection;
using UnityEngine;

/// <summary>
/// HybridCLR 代码热更引导（AOT，场景挂载）。
///
/// 职责：
///   1) 在真机（IL2CPP）上加载 AOT 元数据补充（global-metadata），让热更代码能访问
///      AOT 程序集的泛型 / 反射信息（编辑器里不需要，编辑器是 Mono 全元数据）；
///   2) 按依赖顺序加载热更程序集（Business / ServerSelect / Game）；
///   3) 反射调用热更入口 Game.HotEntry.Boot()，启动游戏。
///
/// 热更程序集来源（按优先级）：
///   · 设备：StreamingAssets/HotUpdate/（首包带一份，之后由 YooAsset 下载覆盖）
///   · 编辑器：HybridCLRData/HotUpdateDlls/<平台>/（HybridCLR/CompileDll 的产物）
/// </summary>
public class HybridCLRBoot : MonoBehaviour
{
    [Header("热更程序集（按依赖顺序）")]
    [Tooltip("要加载的热更 DLL 文件名，顺序不能乱：依赖在前")]
    [SerializeField]
    private string[] hotUpdateDlls = new string[]
    {
        "GameFramework.Business.dll",
        "GameFramework.ServerSelect.dll",
        "Game.dll",
    };

    [Tooltip("真机上从 StreamingAssets 的这个子目录加载热更 DLL")]
    [SerializeField]
    private string deviceDllFolder = "HotUpdate";

    [Tooltip("编辑器里从哪个目录加载（相对工程根）")]
    [SerializeField]
    private string editorDllFolder = "HybridCLRData/HotUpdateDlls";

    [Tooltip("AOT 元数据补充文件（真机 IL2CPP 需要，编辑器不需要）")]
    [SerializeField]
    private string metadataFileName = "global-metadata.dat";

    [Tooltip("热更程序集用到的 AOT 元数据补充（编辑器可跳过）")]
    [SerializeField]
    private bool loadAotMetadataOnDeviceOnly = true;

    private void Awake()
    {
        Application.targetFrameRate = 60;

        if (Application.isEditor)
        {
            // 编辑器：Game 等热更程序集已经被 Unity 正常编译进编辑器（热更程序集在编辑器里是普通程序集），
            // 不能重复 Assembly.Load（会双份类型冲突）。直接反射进已加载的 Game 程序集即可。
            Debug.Log("[HybridCLRBoot] 编辑器模式：直接使用已编译的热更程序集");
        }
        else
        {
            // 真机 IL2CPP：Game 等程序集不在包里，必须加载 AOT 元数据补充 + 热更 DLL
            LoadAotMetadata();
            LoadHotUpdateAssemblies();
        }

        EnterGame();
    }

    /// <summary>真机上补充 AOT 元数据。编辑器跳过（Mono 有全部元数据）。</summary>
    private void LoadAotMetadata()
    {
        bool isIl2Cpp = Application.isEditor == false; // 真机 = IL2CPP 构建
        if (Application.isEditor || !loadAotMetadataOnDeviceOnly)
            return;

        string path = Path.Combine(Application.streamingAssetsPath, deviceDllFolder, metadataFileName);
        if (!File.Exists(path))
        {
            Debug.LogError("[HybridCLRBoot] 找不到 AOT 元数据补充文件：" + path);
            return;
        }

        byte[] metaBytes = File.ReadAllBytes(path);
        var err = HybridCLR.RuntimeApi.LoadMetadataForAOTAssembly(
            metaBytes, HybridCLR.HomologousImageMode.SuperSet);
        Debug.Log("[HybridCLRBoot] LoadMetadataForAOTAssembly => " + err);
    }

    /// <summary>按依赖顺序加载热更程序集。</summary>
    private void LoadHotUpdateAssemblies()
    {
        for (int i = 0; i < hotUpdateDlls.Length; i++)
        {
            byte[] dllBytes = ReadDll(hotUpdateDlls[i]);
            if (dllBytes == null)
            {
                Debug.LogError("[HybridCLRBoot] 加载热更程序集失败（找不到文件）：" + hotUpdateDlls[i]);
                continue;
            }

            byte[] pdbBytes = ReadPdb(hotUpdateDlls[i]);
            Assembly asm = pdbBytes != null
                ? Assembly.Load(dllBytes, pdbBytes)
                : Assembly.Load(dllBytes);

            Debug.Log("[HybridCLRBoot] 已加载热更程序集：" + asm.GetName().Name + " v" + asm.GetName().Version);
        }
    }

    private byte[] ReadDll(string dllName)
    {
        // 编辑器：HybridCLRData/HotUpdateDlls/<平台>/
        if (Application.isEditor)
        {
            string platform = GetPlatformFolderName();
            string path = Path.Combine(editorDllFolder, platform, dllName);
            if (File.Exists(path))
                return File.ReadAllBytes(path);

            // 兜底：Win64（编辑器模拟 Android 时产物目录不一致）
            string alt = Path.Combine(editorDllFolder, "Win64", dllName);
            if (File.Exists(alt))
                return File.ReadAllBytes(alt);

            Debug.LogWarning("[HybridCLRBoot] 编辑器没找到 " + path + "，也没找到 " + alt);
            return null;
        }

        // 真机：StreamingAssets/HotUpdate/
        string devicePath = Path.Combine(Application.streamingAssetsPath, deviceDllFolder, dllName);
        if (File.Exists(devicePath))
            return File.ReadAllBytes(devicePath);

        Debug.LogWarning("[HybridCLRBoot] 真机没找到 " + devicePath);
        return null;
    }

    private byte[] ReadPdb(string dllName)
    {
        string pdbName = dllName.Replace(".dll", ".pdb");
        string dir = Application.isEditor
            ? Path.Combine(editorDllFolder, GetPlatformFolderName())
            : Path.Combine(Application.streamingAssetsPath, deviceDllFolder);

        string path = Path.Combine(dir, pdbName);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private string GetPlatformFolderName()
    {
#if UNITY_ANDROID
        return "Android";
#elif UNITY_IOS
        return "IOS";
#elif UNITY_STANDALONE_WIN
        return "Win64";
#else
        return "Win64";
#endif
    }

    /// <summary>反射调用热更入口，启动游戏。</summary>
    private void EnterGame()
    {
        Assembly gameAsm = null;
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name == "Game")
            {
                gameAsm = asm;
                break;
            }
        }

        if (gameAsm == null)
        {
            Debug.LogError("[HybridCLRBoot] 没找到热更程序集 Game，无法进入游戏。");
            return;
        }

        Type entry = gameAsm.GetType("Game.HotEntry");
        if (entry == null)
        {
            Debug.LogError("[HybridCLRBoot] Game 程序集里没有 HotEntry 类型。");
            return;
        }

        entry.GetMethod("Boot").Invoke(null, null);
        Debug.Log("[HybridCLRBoot] 已调用热更入口 HotEntry.Boot()");
    }
}
