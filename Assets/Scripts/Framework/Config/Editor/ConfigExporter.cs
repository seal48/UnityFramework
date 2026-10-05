using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace GameFramework.Config.EditorTools
{
    /// <summary>导出报告。失败时 Errors 里是"文件 / sheet / 行 / 列"级别的原因。</summary>
    public sealed class ConfigExportReport
    {
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Infos = new List<string>();

        public bool Success { get { return Errors.Count == 0; } }

        public string ToText(bool dryRun = false)
        {
            StringBuilder sb = new StringBuilder();
            if (!Success) sb.Append("[配置表] 导出失败");
            else sb.Append(dryRun ? "[配置表] 校验通过（未写文件）" : "[配置表] 导出完成");
            sb.Append('\n');
            for (int i = 0; i < Infos.Count; i++) sb.Append("  · ").Append(Infos[i]).Append('\n');
            for (int i = 0; i < Warnings.Count; i++) sb.Append("  ! ").Append(Warnings[i]).Append('\n');
            for (int i = 0; i < Errors.Count; i++) sb.Append("  x ").Append(Errors[i]).Append('\n');
            return sb.ToString();
        }
    }

    /// <summary>
    /// 配置表导出编排：读 Excel -&gt; 校验 -&gt; 生成 二进制 / JSON / C#。
    /// 这个类不引用任何 Unity API（只吃一个 projectRoot），所以以后想抽成独立 CLI 直接搬走就行。
    /// 校验不通过时一个文件都不写，避免留下半成品。
    /// </summary>
    public static class ConfigExporter
    {
        public const string NameSpace = "GameFramework.Config";
        public const string EnumFileName = "__enums__";

        public const string ExcelFolder = "/Assets/Config/Excel";
        public const string JsonFolder = "/Assets/Config/Json";
        public const string DataFolder = "/Assets/ConfigData";
        public const string ServerDataFolder = "/Assets/ConfigDataServer";
        public const string GeneratedFolder = "/Assets/Scripts/Framework/Config/Generated";

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly UTF8Encoding Utf8Bom = new UTF8Encoding(true);

        /// <summary>导出全部表。dryRun = true 时只校验、只报大小，不写任何文件（发版前先跑一遍用）。</summary>
        public static ConfigExportReport ExportAll(string projectRoot, bool dryRun = false)
        {
            ConfigExportReport report = new ConfigExportReport();
            if (string.IsNullOrEmpty(projectRoot))
            {
                report.Errors.Add("工程根目录为空。");
                return report;
            }

            string excelDir = projectRoot + ExcelFolder;
            string jsonDir = projectRoot + JsonFolder;
            string dataDir = projectRoot + DataFolder;
            string serverDataDir = projectRoot + ServerDataFolder;
            string codeDir = projectRoot + GeneratedFolder;

            if (!Directory.Exists(excelDir))
            {
                report.Errors.Add("找不到表目录 " + ExcelFolder + "。把 .xlsx 放进去，文件名就是表名。");
                return report;
            }

            Dictionary<string, EnumDefinition> enums = ReadEnums(excelDir, report);
            report.Infos.Add("枚举定义 " + enums.Count + " 个");

            string[] files = Directory.GetFiles(excelDir, "*.xlsx");
            Array.Sort(files, StringComparer.Ordinal);

            List<TableSchema> schemas = new List<TableSchema>();
            List<List<object[]>> dataSets = new List<List<object[]>>();
            int skipped = 0;

            for (int i = 0; i < files.Length; i++)
            {
                string path = files[i];
                string tableName = Path.GetFileNameWithoutExtension(path);
                if (tableName.StartsWith("_", StringComparison.Ordinal)) { skipped++; continue; }

                List<XlsxSheet> sheets;
                try { sheets = XlsxReader.ReadAllSheets(path); }
                catch (Exception ex) { report.Errors.Add("[" + tableName + ".xlsx] 读取失败：" + ex.Message); continue; }

                if (sheets.Count == 0) { report.Errors.Add("[" + tableName + ".xlsx] 里没有任何 sheet。"); continue; }
                if (sheets.Count > 1)
                    report.Warnings.Add("[" + tableName + ".xlsx] 有 " + sheets.Count + " 个 sheet，只读第一个（" + sheets[0].Name + "）。");

                XlsxSheet sheet = sheets[0];

                TableSchema schema;
                int firstDataRow;
                if (!SchemaParser.TryParseSchema(sheet, tableName, tableName + ".xlsx", enums, report.Errors, out schema, out firstDataRow))
                    continue;

                List<object[]> rows = ReadRows(sheet, schema, enums, firstDataRow, report);
                if (rows == null) continue;

                // 把刚生成的二进制读回来验一遍：字段个数 / 顺序 / 数组长度对不上就报错，
                // 放在这里是为了让下面的"有错就不写任何文件"能兜住它
                if (!VerifyData(schema, rows, report)) continue;

                report.Infos.Add(tableName + "：字段 " + schema.Fields.Count + " 个，数据 " + rows.Count + " 行");
                schemas.Add(schema);
                dataSets.Add(rows);
            }

            if (!report.Success)
            {
                report.Infos.Add("存在错误，本次没有写任何文件（避免写出半成品）。");
                return report;
            }

            // 字符集扫描：给字体子集化用（全表客户端可见 string 字段 + 固定 ASCII/全角标点）
            List<char> charSet = ConfigCharSet.Collect(schemas, dataSets);
            report.Infos.Add("字符集：" + charSet.Count + " 个字符");

            if (!dryRun)
            {
                Directory.CreateDirectory(jsonDir);
                Directory.CreateDirectory(dataDir);
                Directory.CreateDirectory(serverDataDir);
                Directory.CreateDirectory(codeDir);
            }

            List<string> keptData = new List<string>();
            List<string> keptServerData = new List<string>();
            List<string> keptCode = new List<string>();

            for (int i = 0; i < schemas.Count; i++)
            {
                TableSchema schema = schemas[i];
                List<object[]> rows = dataSets[i];

                byte[] clientBytes = ConfigGenerator.BuildBinary(schema, rows, ConfigSide.Client);
                byte[] serverBytes = ConfigGenerator.BuildBinary(schema, rows, ConfigSide.Server);


                if (dryRun)
                {
                    report.Infos.Add("（预览）" + schema.Name + "：客户端 " + clientBytes.Length + " 字节，服务端 " + serverBytes.Length + " 字节"
                        + (schema.HasSideFields ? "（有分端字段）" : string.Empty));
                    continue;
                }

                string dataPath = dataDir + "/" + schema.Name + ".bytes";
                File.WriteAllBytes(dataPath, clientBytes);
                keptData.Add(dataPath);

                string serverDataPath = serverDataDir + "/" + schema.Name + ".bytes";
                File.WriteAllBytes(serverDataPath, serverBytes);
                keptServerData.Add(serverDataPath);

                File.WriteAllText(jsonDir + "/" + schema.Name + ".json", ConfigGenerator.BuildJson(schema, rows), Utf8NoBom);

                string codePath = codeDir + "/" + schema.Name + ".g.cs";
                File.WriteAllText(codePath, ConfigGenerator.GenerateTableCode(schema, NameSpace), Utf8Bom);
                keptCode.Add(codePath);
            }

            if (dryRun)
            {
                report.Infos.Add("（预览模式：只校验，没有写任何文件）");
                return report;
            }

            List<EnumDefinition> enumList = SortedEnums(enums);
            if (enumList.Count > 0)
            {
                string enumCodePath = codeDir + "/Enums.g.cs";
                File.WriteAllText(enumCodePath, ConfigGenerator.GenerateEnumsCode(enumList, NameSpace), Utf8Bom);
                keptCode.Add(enumCodePath);
            }

            string dbCodePath = codeDir + "/ConfigDatabase.g.cs";
            File.WriteAllText(dbCodePath, ConfigGenerator.GenerateDatabaseCode(schemas, NameSpace), Utf8Bom);
            keptCode.Add(dbCodePath);

            // 字符集文件：字体子集化（pyftsubset --text-file）的输入
            string charsetPath = jsonDir + "/" + ConfigCharSet.OutputFileName;
            ConfigCharSet.WriteFile(charsetPath, charSet);
            report.Infos.Add("字符集文件 -> " + JsonFolder + "/" + ConfigCharSet.OutputFileName);

            RemoveStale(codeDir, "*.g.cs", keptCode, report);
            RemoveStale(dataDir, "*.bytes", keptData, report);
            RemoveStale(serverDataDir, "*.bytes", keptServerData, report);

            report.Infos.Add("已生成 " + schemas.Count + " 张表 / " + enumList.Count + " 个枚举");
            report.Infos.Add("数据：客户端 -> " + DataFolder + "，服务端 -> " + ServerDataFolder);
            report.Infos.Add("代码 -> " + GeneratedFolder + "，快照 -> " + JsonFolder);
            return report;
        }

        /// <summary>生成 → 立刻读回来验证。返回 false 表示这一侧的数据有问题（已写进 report.Errors）。</summary>
        private static bool VerifyData(TableSchema schema, List<object[]> rows, ConfigExportReport report)
        {
            ConfigSide[] sides = { ConfigSide.Client, ConfigSide.Server };
            for (int i = 0; i < sides.Length; i++)
            {
                ConfigSide side = sides[i];
                string problem = ConfigGenerator.VerifyBinary(schema, side, ConfigGenerator.BuildBinary(schema, rows, side));

                if (problem != null)
                {
                    report.Errors.Add("[" + schema.Name + "] " + (side == ConfigSide.Client ? "客户端" : "服务端")
                        + "数据自校验失败：" + problem);
                    return false;
                }
            }
            return true;
        }

        private static List<object[]> ReadRows(
            XlsxSheet sheet,
            TableSchema schema,
            IDictionary<string, EnumDefinition> enums,
            int firstDataRow,
            ConfigExportReport report)
        {
            List<object[]> result = new List<object[]>();
            HashSet<string> usedKeys = new HashSet<string>(StringComparer.Ordinal);
            bool hasError = false;

            for (int row = firstDataRow; row < sheet.RowCount; row++)
            {
                string context = "[" + schema.SourceFile + "/" + sheet.Name + " 第 " + (row + 1) + " 行]";

                bool empty = true;
                for (int c = 0; c < schema.Fields.Count; c++)
                {
                    if (sheet.Get(row, schema.Fields[c].Column).Trim().Length > 0) { empty = false; break; }
                }
                if (empty) continue;

                object[] values = new object[schema.Fields.Count];
                for (int c = 0; c < schema.Fields.Count; c++)
                {
                    ConfigField field = schema.Fields[c];
                    object value;
                    if (!ConfigGenerator.TryConvert(field, sheet.Get(row, field.Column), context, enums, report.Errors, out value))
                    {
                        hasError = true;
                        continue;
                    }
                    values[c] = value;
                }

                object key = values[0];
                if (key is string && ((string)key).Length == 0)
                {
                    report.Errors.Add(context + " 主键 \"" + schema.KeyField.Name + "\" 是空的。");
                    hasError = true;
                }
                else if (key != null)
                {
                    string keyText = Convert.ToString(key, CultureInfo.InvariantCulture);
                    if (!usedKeys.Add(keyText))
                    {
                        report.Errors.Add(context + " 主键 \"" + schema.KeyField.Name + "\" 重复：" + keyText + "。");
                        hasError = true;
                    }
                }

                result.Add(values);
            }

            return hasError ? null : result;
        }

        private static Dictionary<string, EnumDefinition> ReadEnums(string excelDir, ConfigExportReport report)
        {
            Dictionary<string, EnumDefinition> enums = new Dictionary<string, EnumDefinition>(StringComparer.Ordinal);
            string path = excelDir + "/" + EnumFileName + ".xlsx";
            if (!File.Exists(path)) return enums;

            List<XlsxSheet> sheets;
            try { sheets = XlsxReader.ReadAllSheets(path); }
            catch (Exception ex) { report.Errors.Add("[" + EnumFileName + ".xlsx] 读取失败：" + ex.Message); return enums; }

            for (int s = 0; s < sheets.Count; s++)
            {
                XlsxSheet sheet = sheets[s];
                string name = sheet.Name;
                if (name.StartsWith("_", StringComparison.Ordinal)) continue;

                if (name.StartsWith("__", StringComparison.Ordinal))
                    continue;

                if (!IsValidIdentifier(name))
                {
                    report.Errors.Add("[__enums__.xlsx/" + name + "] 枚举名不是合法的 C# 标识符。");
                    continue;
                }
                if (enums.ContainsKey(name))
                {
                    report.Errors.Add("[__enums__.xlsx] 枚举 \"" + name + "\" 重复定义。");
                    continue;
                }

                EnumDefinition def = new EnumDefinition();
                def.Name = name;

                for (int row = 1; row < sheet.RowCount; row++)
                {
                    string memberName = sheet.Get(row, 0).Trim();
                    string valueText = sheet.Get(row, 1).Trim();
                    if (memberName.Length == 0 && valueText.Length == 0) continue;

                    if (memberName.Length == 0)
                    {
                        report.Errors.Add("[__enums__.xlsx/" + name + "] 第 " + (row + 1) + " 行缺少成员名。");
                        continue;
                    }
                    if (!IsValidIdentifier(memberName))
                    {
                        report.Errors.Add("[__enums__.xlsx/" + name + "] 第 " + (row + 1) + " 行 \"" + memberName + "\" 不是合法的 C# 标识符。");
                        continue;
                    }

                    int value;
                    if (!int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    {
                        report.Errors.Add("[__enums__.xlsx/" + name + "] 第 " + (row + 1) + " 行 \"" + memberName + "\" 的值 \"" + valueText + "\" 不是整数。");
                        continue;
                    }

                    def.Add(memberName, value);
                }

                if (def.Values.Count == 0)
                {
                    report.Errors.Add("[__enums__.xlsx/" + name + "] 一个成员都没有。");
                    continue;
                }

                enums.Add(name, def);
            }

            return enums;
        }

        private static List<EnumDefinition> SortedEnums(Dictionary<string, EnumDefinition> enums)
        {
            List<string> names = new List<string>(enums.Keys);
            names.Sort(StringComparer.Ordinal);

            List<EnumDefinition> list = new List<EnumDefinition>();
            for (int i = 0; i < names.Count; i++) list.Add(enums[names[i]]);
            return list;
        }

        private static void RemoveStale(string folder, string pattern, List<string> keep, ConfigExportReport report)
        {
            if (!Directory.Exists(folder)) return;

            string[] existing = Directory.GetFiles(folder, pattern);
            for (int i = 0; i < existing.Length; i++)
            {
                // Directory.GetFiles 在 Windows 上给的是反斜杠路径，先统一成正斜杠再比，否则会把自己刚写的文件删掉
                string path = existing[i].Replace('\\', '/');

                bool found = false;
                for (int j = 0; j < keep.Count; j++)
                {
                    if (string.Equals(keep[j].Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                }
                if (found) continue;

                try
                {
                    File.Delete(existing[i]);

                    string meta = existing[i] + ".meta";
                    if (File.Exists(meta)) File.Delete(meta);

                    int index = path.LastIndexOf("/Assets/", StringComparison.Ordinal);
                    report.Warnings.Add("已清理旧产物：" + (index >= 0 ? path.Substring(index + 1) : path));
                }
                catch (Exception ex)
                {
                    report.Warnings.Add("清理旧产物失败：" + path + "（" + ex.Message + "）");
                }
            }
        }

        private static bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            char first = name[0];
            if (!char.IsLetter(first) && first != '_') return false;

            for (int i = 1; i < name.Length; i++)
            {
                char ch = name[i];
                if (!char.IsLetterOrDigit(ch) && ch != '_') return false;
            }
            return true;
        }
    }
}