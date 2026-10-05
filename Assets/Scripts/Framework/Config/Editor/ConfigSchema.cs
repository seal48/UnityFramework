using System;
using System.Collections.Generic;
using System.Globalization;

namespace GameFramework.Config.EditorTools
{
    public enum ConfigFieldType { Int, Long, Float, Bool, String, Enum }

    /// <summary>字段归属：双端 / 只给客户端 / 只给服务端。</summary>
    public enum ConfigFieldGroup { Both, Client, Server }

    /// <summary>导出时选择导出哪一侧的数据。</summary>
    public enum ConfigSide { Client, Server }

    /// <summary>一个字段（Excel 里的一列）。</summary>
    public sealed class ConfigField
    {
        public string Name;        // C# 属性名，PascalCase
        public string RawType;     // Excel 里写的原始类型文本（含 []），参与 schemaHash
        public ConfigFieldType Type;
        public bool IsArray;       // int[] / string[] / enum:Xxx[] ...
        public string EnumName;    // Type == Enum 时有效
        public ConfigFieldGroup Group = ConfigFieldGroup.Both;
        public string Comment;
        public int Column;         // 在 sheet 里的 0-based 列号
        public int Index;          // 在 Fields 里的下标（数据行按下标存）

        public bool IsClientOnly { get { return Group == ConfigFieldGroup.Client; } }
        public bool IsServerOnly { get { return Group == ConfigFieldGroup.Server; } }
    }

    /// <summary>一条数据行，Values 按字段 Index 索引。</summary>
    public sealed class ConfigRow
    {
        public int RowNumber;      // Excel 行号（1-based），报错用
        public string[] Values;

        public string Get(int index)
        {
            if (index < 0 || index >= Values.Length) return string.Empty;
            return Values[index] ?? string.Empty;
        }
    }

    /// <summary>一张表的表头信息。</summary>
    public sealed class TableSchema
    {
        public string Name;        // 表名，取自 Excel 文件名
        public string SourceFile;  // Excel 文件名，报错用
        public string SheetName;
        public readonly List<ConfigField> Fields = new List<ConfigField>();

        /// <summary>是否有分端字段（没有就不用生成 #if）。</summary>
        public bool HasSideFields;

        /// <summary>客户端数据（双端 + 客户端字段）的结构哈希。</summary>
        public int ClientHash;

        /// <summary>服务端数据（双端 + 服务端字段）的结构哈希。</summary>
        public int ServerHash;

        /// <summary>主键 = 第一个字段，必须双端共有。</summary>
        public ConfigField KeyField { get { return Fields.Count > 0 ? Fields[0] : null; } }

        public List<ConfigField> FieldsOf(ConfigSide side)
        {
            List<ConfigField> list = new List<ConfigField>();
            for (int i = 0; i < Fields.Count; i++)
            {
                ConfigFieldGroup group = Fields[i].Group;
                if (group == ConfigFieldGroup.Both) list.Add(Fields[i]);
                else if (side == ConfigSide.Client && group == ConfigFieldGroup.Client) list.Add(Fields[i]);
                else if (side == ConfigSide.Server && group == ConfigFieldGroup.Server) list.Add(Fields[i]);
            }
            return list;
        }

        public int HashOf(ConfigSide side)
        {
            return side == ConfigSide.Client ? ClientHash : ServerHash;
        }
    }

    /// <summary>一个枚举定义（来自 __enums__.xlsx 的一个 sheet）。</summary>
    public sealed class EnumDefinition
    {
        public string Name;
        public readonly List<KeyValuePair<string, int>> Values = new List<KeyValuePair<string, int>>();
        private readonly Dictionary<string, int> byName = new Dictionary<string, int>();

        public void Add(string name, int value)
        {
            Values.Add(new KeyValuePair<string, int>(name, value));
            byName[name] = value;
        }

        public bool TryParse(string text, out int value)
        {
            if (byName.TryGetValue(text, out value)) return true;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }

    /// <summary>
    /// 表头解析。约定（与 Luban 一致，以后想换 Luban 不用改表）：
    ///   A 列是标记列，字段从 B 列开始
    ///   某行 A 列 == "##var"    -> B 起是字段名
    ///   某行 A 列 == "##type"   -> B 起是类型（int/long/float/bool/string/enum:Xxx，末尾加 [] 就是数组）
    ///   某行 A 列 == "##group"  -> B 起是归属（B=双端，C=只客户端，S=只服务端；留空按 B）
    ///   某行 A 列 == "##"       -> B 起是中文注释
    ///   其余 "##xxx" 行忽略
    ///   第一个不是 "##" 的行开始才是数据
    /// </summary>
    public static class SchemaParser
    {
        public const string VarMark = "##var";
        public const string TypeMark = "##type";
        public const string GroupMark = "##group";
        public const string CommentMark = "##";
        public const string CommentMarkLong = "##comment";

        /// <summary>数组在单元格里的分隔符（和 Luban 一致）。</summary>
        public const char ArraySeparator = '&';

        public static bool TryParseSchema(
            XlsxSheet sheet,
            string tableName,
            string sourceFile,
            IDictionary<string, EnumDefinition> enums,
            List<string> errors,
            out TableSchema schema,
            out int firstDataRow)
        {
            schema = new TableSchema();
            schema.Name = tableName;
            schema.SourceFile = sourceFile;
            schema.SheetName = sheet.Name;
            firstDataRow = -1;

            string[] names = null;
            string[] types = null;
            string[] groups = null;
            string[] comments = null;

            int columnCount = sheet.ColumnCount;
            for (int row = 0; row < sheet.RowCount; row++)
            {
                string mark = sheet.Get(row, 0).Trim();
                if (!mark.StartsWith("##", StringComparison.Ordinal))
                {
                    firstDataRow = row;
                    break;
                }

                if (mark == VarMark) names = ReadRow(sheet, row, columnCount);
                else if (mark == TypeMark) types = ReadRow(sheet, row, columnCount);
                else if (mark == GroupMark) groups = ReadRow(sheet, row, columnCount);
                else if (mark == CommentMark || mark == CommentMarkLong) comments = ReadRow(sheet, row, columnCount);
            }

            if (firstDataRow < 0)
            {
                errors.Add($"[{sourceFile}] 没有找到数据行（第一个不是 ## 开头的行才是数据）。");
                return false;
            }
            if (names == null)
            {
                errors.Add($"[{sourceFile}] 缺少字段名行（A 列应为 \"{VarMark}\"）。");
                return false;
            }
            if (types == null)
            {
                errors.Add($"[{sourceFile}] 缺少类型行（A 列应为 \"{TypeMark}\"）。");
                return false;
            }

            HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
            for (int col = 1; col < columnCount; col++)
            {
                string name = names.Length > col ? (names[col] ?? string.Empty).Trim() : string.Empty;
                if (name.Length == 0) continue;

                string rawType = types.Length > col ? (types[col] ?? string.Empty).Trim() : string.Empty;
                if (rawType.Length == 0)
                {
                    errors.Add($"[{sourceFile}] 字段 \"{name}\"（第 {col + 1} 列）没有写类型。");
                    continue;
                }

                ConfigFieldType type;
                string enumName;
                bool isArray;
                if (!TryParseType(rawType, enums, out type, out enumName, out isArray))
                {
                    errors.Add($"[{sourceFile}] 字段 \"{name}\" 的类型 \"{rawType}\" 不认识（支持 int/long/float/bool/string/enum:枚举名，末尾可加 [] 变数组）。");
                    continue;
                }

                if (!IsValidIdentifier(name))
                {
                    errors.Add($"[{sourceFile}] 字段名 \"{name}\" 不是合法的 C# 标识符。");
                    continue;
                }
                if (!used.Add(name))
                {
                    errors.Add($"[{sourceFile}] 字段名 \"{name}\" 重复。");
                    continue;
                }

                ConfigFieldGroup group;
                string groupText = groups != null && groups.Length > col ? (groups[col] ?? string.Empty).Trim() : string.Empty;
                if (!TryParseGroup(groupText, out group))
                {
                    errors.Add($"[{sourceFile}] 字段 \"{name}\" 的归属 \"{groupText}\" 不认识（B=双端，C=只客户端，S=只服务端，留空按 B）。");
                    continue;
                }

                ConfigField field = new ConfigField();
                field.Name = name;
                field.RawType = rawType;
                field.Type = type;
                field.IsArray = isArray;
                field.EnumName = enumName;
                field.Group = group;
                field.Comment = comments != null && comments.Length > col ? (comments[col] ?? string.Empty) : string.Empty;
                field.Column = col;
                field.Index = schema.Fields.Count;
                schema.Fields.Add(field);

                if (group != ConfigFieldGroup.Both) schema.HasSideFields = true;
            }

            if (schema.Fields.Count == 0)
            {
                errors.Add($"[{sourceFile}] 一个字段都没有解析出来。");
                return false;
            }

            ConfigField key = schema.KeyField;
            if (key.IsArray)
            {
                errors.Add($"[{sourceFile}] 主键 \"{key.Name}\" 不能是数组。");
                return false;
            }
            if (key.Group != ConfigFieldGroup.Both)
            {
                errors.Add($"[{sourceFile}] 主键 \"{key.Name}\" 必须是双端字段（##group 写 B），否则客户端/服务端只有一边能拿到它。");
                return false;
            }
            if (key.Type != ConfigFieldType.Int && key.Type != ConfigFieldType.Long && key.Type != ConfigFieldType.String)
            {
                errors.Add($"[{sourceFile}] 主键 \"{key.Name}\" 的类型只能是 int / long / string（第一个字段是主键）。");
                return false;
            }

            schema.ClientHash = ComputeSchemaHash(schema.FieldsOf(ConfigSide.Client));
            schema.ServerHash = ComputeSchemaHash(schema.FieldsOf(ConfigSide.Server));
            return true;
        }

        /// <summary>按字段顺序算 FNV-1a；客户端 / 服务端 / 导出工具三边算法必须一致。</summary>
        public static int ComputeSchemaHash(IList<ConfigField> fields)
        {
            unchecked
            {
                uint hash = 2166136261u;
                for (int i = 0; i < fields.Count; i++)
                {
                    string text = fields[i].Name + ":" + fields[i].RawType + ";";
                    for (int j = 0; j < text.Length; j++)
                    {
                        hash ^= text[j];
                        hash *= 16777619u;
                    }
                }
                return (int)hash;
            }
        }

        public static bool TryParseType(string rawType, IDictionary<string, EnumDefinition> enums, out ConfigFieldType type, out string enumName, out bool isArray)
        {
            type = ConfigFieldType.Int;
            enumName = null;
            isArray = false;

            string text = rawType.Trim();
            if (text.EndsWith("[]", StringComparison.Ordinal))
            {
                isArray = true;
                text = text.Substring(0, text.Length - 2).Trim();
            }

            if (text.StartsWith("enum:", StringComparison.OrdinalIgnoreCase))
            {
                string name = text.Substring(5).Trim();
                if (name.Length == 0) return false;
                if (enums != null && !enums.ContainsKey(name)) return false;

                type = ConfigFieldType.Enum;
                enumName = name;
                return true;
            }

            switch (text.ToLowerInvariant())
            {
                case "int": case "int32": type = ConfigFieldType.Int; return true;
                case "long": case "int64": type = ConfigFieldType.Long; return true;
                case "float": case "single": type = ConfigFieldType.Float; return true;
                case "bool": case "boolean": type = ConfigFieldType.Bool; return true;
                case "string": case "str": type = ConfigFieldType.String; return true;
                default: return false;
            }
        }

        public static bool TryParseGroup(string text, out ConfigFieldGroup group)
        {
            group = ConfigFieldGroup.Both;
            if (string.IsNullOrEmpty(text)) return true;

            switch (text.ToUpperInvariant())
            {
                case "B": case "BOTH": group = ConfigFieldGroup.Both; return true;
                case "C": case "CLIENT": group = ConfigFieldGroup.Client; return true;
                case "S": case "SERVER": group = ConfigFieldGroup.Server; return true;
                default: return false;
            }
        }

        private static string[] ReadRow(XlsxSheet sheet, int row, int columnCount)
        {
            string[] values = new string[columnCount];
            for (int col = 0; col < columnCount; col++)
                values[col] = sheet.Get(row, col);
            return values;
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