using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using GameFramework.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace GameFramework.UI.Editor
{
    /// <summary>
    /// UI 绑定代码生成器：扫预制体，把节点生成成 <c>XxxPanel.Bindings.g.cs</c> 里的强类型字段。
    ///
    /// 解决什么问题：
    ///   原来界面里到处写 <c>Get&lt;Button&gt;("LoginButton")</c> —— 字符串拼错只有运行时才发现，
    ///   节点改名重构时编译器帮不上忙。生成成字段之后：
    ///     · 字段名 = 节点名（首字母小写），改名 → 重新生成 → 用错的地方**编译报错**；
    ///     · 类型由节点上挂的组件推导，不用手写泛型。
    ///
    /// 生成的字段在 <see cref="Panel.BindNodes"/> 里统一赋值，时机在 OnInit 之前，所以 OnInit 直接用。
    ///
    /// 用法：菜单 <c>Tools/UI/绑定/...</c>，或在预制体上右键。
    /// </summary>
    public static class UIBindGenerator
    {
        /// <summary>节点上挂这些组件才会生成字段，顺序 = 一个节点挂了多个时的优先级。</summary>
        private static readonly Type[] BindablePriority =
        {
            typeof(Button),
            typeof(Toggle),
            typeof(Slider),
            typeof(ScrollRect),
            typeof(InputField),
            typeof(Dropdown),
            typeof(Text),
            typeof(Image),
        };

        /// <summary>节点名以它开头就不生成（用来排除纯装饰、临时节点）。</summary>
        private const string IgnorePrefix = "_";

        /// <summary>生成文件的后缀。</summary>
        private const string GeneratedSuffix = ".Bindings.g.cs";

        [MenuItem("Tools/UI/绑定/生成选中预制体的绑定代码", false, 100)]
        private static void GenerateForSelection()
        {
            GameObject[] selected = Selection.gameObjects;
            if (selected == null || selected.Length == 0)
            {
                EditorUtility.DisplayDialog("UI 绑定", "先在 Project 里选中预制体（可多选）。", "好");
                return;
            }

            int ok = 0, failed = 0;
            for (int i = 0; i < selected.Length; i++)
            {
                string path = AssetDatabase.GetAssetPath(selected[i]);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (GenerateForPrefab(path, true)) ok++;
                else failed++;
            }

            AssetDatabase.Refresh();
            Debug.Log($"[UIBind] 选中预制体：成功 {ok} 个，跳过/失败 {failed} 个。");
        }

        [MenuItem("Tools/UI/绑定/生成全部面板的绑定代码", false, 101)]
        private static void GenerateForAllPanels()
        {
            int generated = GenerateAllPanels();
            Debug.Log($"[UIBind] 全部面板：生成 {generated} 个绑定文件。");
        }

        /// <summary>
        /// 生成 UI 绑定代码（全部面板）。给菜单 / 自动化（CI、批处理）共用。
        /// 返回成功生成的文件数。
        /// </summary>
        public static int GenerateAllPanels()
        {
            string folder = UIInitOptions.DefaultPanelPrefabFolder;
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { folder.TrimEnd('/') });
            if (guids.Length == 0)
            {
                Debug.LogWarning($"[UIBind] {folder} 下没有预制体。");
                return 0;
            }

            int ok = 0;
            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    EditorUtility.DisplayProgressBar("生成 UI 绑定", path, (float)i / guids.Length);

                    if (GenerateForPrefab(path, false)) ok++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.Refresh();
            return ok;
        }

        /// <summary>生成单个预制体的绑定代码（给自动化用）。</summary>
        public static bool GeneratePanel(string prefabPath)
        {
            bool result = GenerateForPrefab(prefabPath, true);
            AssetDatabase.Refresh();
            return result;
        }

        [MenuItem("Tools/UI/绑定/校验全部面板（只检查不生成）", false, 102)]
        private static void ValidateAllPanels()
        {
            var report = new StringBuilder();
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { UIInitOptions.DefaultPanelPrefabFolder.TrimEnd('/') });

            int checkedCount = 0;
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                var panel = prefab.GetComponent<Panel>();
                if (panel == null) continue;

                checkedCount++;
                MonoScript script = MonoScript.FromMonoBehaviour(panel);
                if (script == null)
                {
                    report.Append("  [缺少脚本] ").Append(path).Append('\n');
                    continue;
                }

                string scriptPath = AssetDatabase.GetAssetPath(script);
                if (!IsPartialClass(scriptPath))
                    report.Append("  [不是 partial] ").Append(scriptPath).Append(" —— 需要改成 partial class 才能合并生成代码\n");
            }

            Debug.Log($"[UIBind] 校验 {checkedCount} 个面板：\n" +
                      (report.Length == 0 ? "  全部正常。" : report.ToString()));
        }

        // ============================== 单个预制体 ==============================

        /// <summary>给一个预制体生成绑定代码。返回 false 表示它不是一个「面板预制体」。</summary>
        private static bool GenerateForPrefab(string prefabPath, bool verbose)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                if (verbose) Debug.LogError($"[UIBind] 加载不了预制体：{prefabPath}");
                return false;
            }

            Panel panel = prefab.GetComponent<Panel>();
            if (panel == null)
            {
                if (verbose) Debug.LogWarning($"[UIBind] {prefabPath} 的根节点上没有 Panel 组件，跳过。");
                return false;
            }

            Type panelType = panel.GetType();
            MonoScript script = MonoScript.FromMonoBehaviour(panel);
            if (script == null)
            {
                Debug.LogError($"[UIBind] 找不到 {panelType.Name} 的脚本资产，跳过。");
                return false;
            }

            string scriptPath = AssetDatabase.GetAssetPath(script);
            if (!IsPartialClass(scriptPath))
            {
                Debug.LogError($"[UIBind] {panelType.Name} 不是 partial class，无法合并生成代码。" +
                               $"\n  请把 {scriptPath} 里的 `class {panelType.Name}` 改成 `partial class {panelType.Name}`（以及其它嵌套类若同名也一样）。");
                return false;
            }

            List<BindEntry> entries = CollectEntries(prefab);
            if (entries.Count == 0)
            {
                if (verbose) Debug.LogWarning($"[UIBind] {prefabPath} 里没有可绑定的节点。");
                return false;
            }

            AssignFieldNames(entries, panelType);

            string code = BuildCode(prefabPath, panelType, entries);
            string outputPath = Path.Combine(Path.GetDirectoryName(scriptPath), panelType.Name + GeneratedSuffix)
                                    .Replace('\\', '/');

            File.WriteAllText(outputPath, code, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(outputPath);

            if (verbose)
                Debug.Log($"[UIBind] 已生成 {outputPath}：{entries.Count} 个字段。", AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(outputPath));

            return true;
        }

        /// <summary>脚本里是否是 partial class（粗略判断：出现 "partial class"）。</summary>
        private static bool IsPartialClass(string scriptPath)
        {
            try
            {
                return File.ReadAllText(scriptPath).Contains("partial class");
            }
            catch (Exception)
            {
                return true;   // 读不到就别拦，交给编译器报错
            }
        }

        /// <summary>扫出所有要绑定的节点。</summary>
        private static List<BindEntry> CollectEntries(GameObject prefab)
        {
            var entries = new List<BindEntry>();

            // 先统计名字重复情况：重名的短名取会有歧义，得用相对路径
            var nameCount = new Dictionary<string, int>(StringComparer.Ordinal);
            Transform root = prefab.transform;
            for (int i = 0; i < root.childCount; i++)
                CollectNames(root.GetChild(i), nameCount);

            for (int i = 0; i < root.childCount; i++)
                CollectEntries(root.GetChild(i), string.Empty, nameCount, entries);

            return entries;
        }

        private static void CollectNames(Transform node, Dictionary<string, int> nameCount)
        {
            int count;
            nameCount.TryGetValue(node.name, out count);
            nameCount[node.name] = count + 1;

            for (int i = 0; i < node.childCount; i++)
                CollectNames(node.GetChild(i), nameCount);
        }

        private static void CollectEntries(Transform node, string parentPath, Dictionary<string, int> nameCount, List<BindEntry> entries)
        {
            string path = parentPath.Length == 0 ? node.name : parentPath + "/" + node.name;

            if (!node.name.StartsWith(IgnorePrefix, StringComparison.Ordinal))
            {
                Component component = FindBindable(node);
                if (component != null)
                {
                    int count;
                    nameCount.TryGetValue(node.name, out count);

                    entries.Add(new BindEntry
                    {
                        NodePath = path,
                        NodeName = node.name,
                        ComponentType = component.GetType(),
                        // 短名唯一就用短名（和手写 Get<T>("名字") 一样好读），重名才退回全路径
                        Key = count > 1 ? path : node.name,
                    });
                }
            }

            for (int i = 0; i < node.childCount; i++)
                CollectEntries(node.GetChild(i), path, nameCount, entries);
        }

        /// <summary>按优先级挑一个组件：Button 优先于它身上的 Image，诸如此类。</summary>
        private static Component FindBindable(Transform node)
        {
            for (int i = 0; i < BindablePriority.Length; i++)
            {
                Component component = node.GetComponent(BindablePriority[i]);
                if (component != null)
                    return component;
            }
            return null;
        }

        // ============================== 字段名 ==============================

        /// <summary>C# 关键字，撞上了要加前缀。</summary>
        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue",
            "decimal","default","delegate","do","double","else","enum","event","explicit","extern","false","finally",
            "fixed","float","for","foreach","goto","if","implicit","in","int","interface","internal","is","lock","long",
            "namespace","new","null","object","operator","out","override","params","private","protected","public",
            "readonly","ref","return","sbyte","sealed","short","sizeof","stackalloc","static","string","struct","switch",
            "this","throw","true","try","typeof","uint","ulong","unchecked","unsafe","ushort","using","virtual","void",
            "volatile","while",
        };

        /// <summary>给每条候选算一个唯一字段名。</summary>
        private static void AssignFieldNames(List<BindEntry> entries, Type panelType)
        {
            // 保留名 = 面板**继承来的**公开成员（name / transform / gameObject / enabled / Panel 自己的成员 ...）。
            // 故意不算面板自己声明的字段 —— 那些正是要被生成代码取代的，算进去会生成 loginButtonButton 这种名字。
            var reserved = new HashSet<string>(StringComparer.Ordinal);
            Type baseType = panelType.BaseType;
            while (baseType != null && baseType != typeof(object))
            {
                MemberInfo[] declared = baseType.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                for (int i = 0; i < declared.Length; i++)
                    reserved.Add(declared[i].Name);
                baseType = baseType.BaseType;
            }

            var used = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < entries.Count; i++)
            {
                BindEntry entry = entries[i];
                string baseName = ToFieldName(entry.NodeName);

                // 重名节点：用路径生成，保证唯一且可读（A/B/Text → aBText）
                if (entry.Key != entry.NodeName)
                    baseName = ToFieldName(entry.NodePath.Replace('/', '_'));

                // 撞上基类/自身成员：拼上组件类型名（节点 Name[Text] → nameText），既避开冲突又更好读
                if (reserved.Contains(baseName))
                    baseName = baseName + entry.ComponentType.Name;

                string candidate = baseName;
                int suffix = 2;
                while (!used.Add(candidate))
                {
                    candidate = baseName + "_" + suffix;
                    suffix++;
                }

                entry.FieldName = candidate;
            }
        }

        /// <summary>节点名 → 字段名：非法字符换成 _、首字母小写、避开关键字、数字开头加下划线。</summary>
        private static string ToFieldName(string nodeName)
        {
            var sb = new StringBuilder(nodeName.Length);
            for (int i = 0; i < nodeName.Length; i++)
            {
                char c = nodeName[i];
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            }

            if (sb.Length == 0)
                return "node";

            // 首字母小写（保留其余大小写，LoginButton -> loginButton）
            if (char.IsUpper(sb[0]))
                sb[0] = char.ToLowerInvariant(sb[0]);

            string result = sb.ToString();

            if (char.IsDigit(result[0]))
                result = "_" + result;

            if (Keywords.Contains(result))
                result = "_" + result;

            return result;
        }

        // ============================== 生成代码 ==============================

        private static string BuildCode(string prefabPath, Type panelType, List<BindEntry> entries)
        {
            var sb = new StringBuilder();
            string indent = string.IsNullOrEmpty(panelType.Namespace) ? string.Empty : "    ";

            sb.Append("// <auto-generated>\n");
            sb.Append("//   由 UI 绑定工具生成，请不要手改 —— 重新生成会把改动覆盖掉。\n");
            sb.Append("//   重新生成：菜单 Tools/UI/绑定/生成全部面板的绑定代码（或选中预制体单独生成）\n");
            sb.Append("//   源预制体：").Append(prefabPath).Append('\n');
            sb.Append("// </auto-generated>\n\n");
            sb.Append("using UnityEngine;\n");
            sb.Append("using UnityEngine.UI;\n\n");

            bool hasNamespace = !string.IsNullOrEmpty(panelType.Namespace);
            if (hasNamespace)
            {
                sb.Append("namespace ").Append(panelType.Namespace).Append("\n{\n");
            }

            sb.Append(indent).Append("public partial class ").Append(panelType.Name).Append('\n');
            sb.Append(indent).Append("{\n");

            // 字段
            for (int i = 0; i < entries.Count; i++)
            {
                BindEntry e = entries[i];
                sb.Append(indent).Append("    /// <summary>节点：").Append(e.NodePath).Append("</summary>\n");
                sb.Append(indent).Append("    public ").Append(e.ComponentType.Name).Append(' ').Append(e.FieldName).Append(";\n");
            }

            // 绑定方法
            sb.Append('\n');
            sb.Append(indent).Append("    /// <summary>由生成代码统一赋值，时机在 OnInit 之前。</summary>\n");
            sb.Append(indent).Append("    protected override void BindNodes()\n");
            sb.Append(indent).Append("    {\n");
            for (int i = 0; i < entries.Count; i++)
            {
                BindEntry e = entries[i];
                sb.Append(indent).Append("        ").Append(e.FieldName)
                  .Append(" = Get<").Append(e.ComponentType.Name).Append(">(\"").Append(e.Key).Append("\");\n");
            }
            sb.Append(indent).Append("    }\n");

            sb.Append(indent).Append("}\n");

            if (hasNamespace)
            {
                sb.Append("}\n");
            }

            return sb.ToString();
        }

        /// <summary>一条待生成的字段。</summary>
        private class BindEntry
        {
            public string NodePath;
            public string NodeName;
            public Type ComponentType;
            /// <summary>Get&lt;T&gt; 用的 key：短名唯一用短名，重名用相对路径。</summary>
            public string Key;
            public string FieldName;
        }
    }
}
