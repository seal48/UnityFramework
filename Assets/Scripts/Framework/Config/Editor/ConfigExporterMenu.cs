using System.IO;
using UnityEditor;
using UnityEngine;

namespace GameFramework.Config.EditorTools
{
    /// <summary>配置表导出的 Unity 入口。菜单在 Tools/配置表 下面。</summary>
    public static class ConfigExporterMenu
    {
        [MenuItem("Tools/配置表/导出全部表", false, 1)]
        public static void ExportAll()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            ConfigExportReport report = ConfigExporter.ExportAll(projectRoot);

            if (report.Success)
            {
                AssetDatabase.Refresh();
                Debug.Log(report.ToText());
            }
            else
            {
                Debug.LogError(report.ToText());
                EditorUtility.DisplayDialog("配置表导出失败", Shorten(report.ToText()), "知道了");
            }
        }

        [MenuItem("Tools/配置表/打开表目录", false, 20)]
        public static void OpenExcelFolder()
        {
            string path = Directory.GetParent(Application.dataPath).FullName + ConfigExporter.ExcelFolder;
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            EditorUtility.RevealInFinder(path);
        }

        [MenuItem("Tools/配置表/打开生成目录", false, 21)]
        public static void OpenGeneratedFolder()
        {
            string path = Directory.GetParent(Application.dataPath).FullName + ConfigExporter.GeneratedFolder;
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            EditorUtility.RevealInFinder(path);
        }

        private static string Shorten(string text)
        {
            const int max = 1200;
            return text.Length <= max ? text : text.Substring(0, max) + "\n…（完整信息见 Console）";
        }
    }
}