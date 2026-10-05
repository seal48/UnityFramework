using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using GameFramework.Config;

namespace GameFramework.Config.EditorTools
{
    /// <summary>把校验通过的数据转成 二进制 / JSON 快照 / C# 代码。</summary>
    public static class ConfigGenerator
    {
        // ============================== 值转换 ==============================

        /// <summary>把 Excel 里的文本转成强类型值；失败时把原因写进 errors（含表名 / 行号 / 字段名 / 数组下标）。</summary>
        public static bool TryConvert(ConfigField field, string text, string context, IDictionary<string, EnumDefinition> enums, List<string> errors, out object value)
        {
            value = null;
            string raw = text == null ? string.Empty : text.Trim();
            string label = "字段 \"" + field.Name + "\"";

            if (!field.IsArray)
                return TryConvertScalar(field, raw, label, context, enums, errors, out value);

            string[] parts = raw.Length == 0 ? new string[0] : raw.Split(SchemaParser.ArraySeparator);
            object[] elements;
            if (!TryConvertElements(field, parts, label, context, enums, errors, out elements)) return false;

            switch (field.Type)
            {
                case ConfigFieldType.Int:
                {
                    int[] array = new int[elements.Length];
                    for (int i = 0; i < elements.Length; i++) array[i] = (int)elements[i];
                    value = array;
                    return true;
                }
                case ConfigFieldType.Long:
                {
                    long[] array = new long[elements.Length];
                    for (int i = 0; i < elements.Length; i++) array[i] = (long)elements[i];
                    value = array;
                    return true;
                }
                case ConfigFieldType.Float:
                {
                    float[] array = new float[elements.Length];
                    for (int i = 0; i < elements.Length; i++) array[i] = (float)elements[i];
                    value = array;
                    return true;
                }
                case ConfigFieldType.Bool:
                {
                    bool[] array = new bool[elements.Length];
                    for (int i = 0; i < elements.Length; i++) array[i] = (bool)elements[i];
                    value = array;
                    return true;
                }
                case ConfigFieldType.String:
                {
                    string[] array = new string[elements.Length];
                    for (int i = 0; i < elements.Length; i++) array[i] = (string)elements[i];
                    value = array;
                    return true;
                }
                case ConfigFieldType.Enum:
                {
                    int[] array = new int[elements.Length];
                    for (int i = 0; i < elements.Length; i++) array[i] = (int)elements[i];
                    value = array;
                    return true;
                }
            }

            errors.Add(context + " " + label + " 的类型未实现。");
            return false;
        }

        private static bool TryConvertElements(ConfigField field, string[] parts, string label, string context, IDictionary<string, EnumDefinition> enums, List<string> errors, out object[] elements)
        {
            elements = new object[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                object element;
                if (!TryConvertScalar(field, parts[i].Trim(), label + " 第 " + (i + 1) + " 项", context, enums, errors, out element)) return false;
                elements[i] = element;
            }
            return true;
        }

        private static bool TryConvertScalar(ConfigField field, string raw, string label, string context, IDictionary<string, EnumDefinition> enums, List<string> errors, out object value)
        {
            value = null;

            switch (field.Type)
            {
                case ConfigFieldType.Int:
                {
                    int parsed;
                    if (raw.Length == 0) { errors.Add(context + " " + label + " 是空的（int 必须有值）。"); return false; }
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    { errors.Add(context + " " + label + " 的值 \"" + raw + "\" 不是整数。"); return false; }
                    value = parsed;
                    return true;
                }
                case ConfigFieldType.Long:
                {
                    long parsed;
                    if (raw.Length == 0) { errors.Add(context + " " + label + " 是空的（long 必须有值）。"); return false; }
                    if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    { errors.Add(context + " " + label + " 的值 \"" + raw + "\" 不是长整数。"); return false; }
                    value = parsed;
                    return true;
                }
                case ConfigFieldType.Float:
                {
                    float parsed;
                    if (raw.Length == 0) { errors.Add(context + " " + label + " 是空的（float 必须有值）。"); return false; }
                    if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    { errors.Add(context + " " + label + " 的值 \"" + raw + "\" 不是小数。"); return false; }
                    value = parsed;
                    return true;
                }
                case ConfigFieldType.Bool:
                {
                    bool parsed;
                    if (!TryParseBool(raw, out parsed))
                    { errors.Add(context + " " + label + " 的值 \"" + raw + "\" 不是布尔（可写 true/false/是/否/1/0）。"); return false; }
                    value = parsed;
                    return true;
                }
                case ConfigFieldType.String:
                {
                    value = raw;
                    return true;
                }
                case ConfigFieldType.Enum:
                {
                    EnumDefinition def;
                    int parsed;
                    if (enums == null || !enums.TryGetValue(field.EnumName, out def) || !def.TryParse(raw, out parsed))
                    { errors.Add(context + " " + label + " 的值 \"" + raw + "\" 不是枚举 " + field.EnumName + " 的成员。"); return false; }
                    value = parsed;
                    return true;
                }
            }

            errors.Add(context + " " + label + " 的类型未实现。");
            return false;
        }

        private static bool TryParseBool(string raw, out bool value)
        {
            value = false;
            if (raw.Length == 0) return false;

            switch (raw.ToLowerInvariant())
            {
                case "true": case "1": case "是": case "y": case "yes": value = true; return true;
                case "false": case "0": case "否": case "n": case "no": value = false; return true;
                default: return false;
            }
        }

        // ============================== 二进制 ==============================

        /// <summary>按某一侧（客户端 / 服务端）的字段集导出：只写该侧要看的字段，并写入该侧的结构哈希。</summary>
        public static byte[] BuildBinary(TableSchema schema, List<object[]> rows, ConfigSide side)
        {
            List<ConfigField> fields = schema.FieldsOf(side);

            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(ConfigReader.Magic);
                writer.Write(ConfigReader.FormatVersion);
                writer.Write(schema.HashOf(side));
                writer.Write(rows.Count);

                for (int r = 0; r < rows.Count; r++)
                {
                    object[] values = rows[r];
                    for (int c = 0; c < fields.Count; c++)
                        WriteValue(writer, fields[c], values[fields[c].Index]);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        private static void WriteValue(BinaryWriter writer, ConfigField field, object value)
        {
            if (field.IsArray)
            {
                WriteArray(writer, field, (Array)value);
                return;
            }

            switch (field.Type)
            {
                case ConfigFieldType.Int: writer.Write((int)value); break;
                case ConfigFieldType.Long: writer.Write((long)value); break;
                case ConfigFieldType.Float: writer.Write((float)value); break;
                case ConfigFieldType.Bool: writer.Write((bool)value ? (byte)1 : (byte)0); break;
                case ConfigFieldType.String: writer.Write((string)value); break;
                case ConfigFieldType.Enum: writer.Write((int)value); break;
            }
        }

        /// <summary>数组先写 int32 元素个数，再依次写元素（与 ConfigReader.ReadXxxArray 对应）。</summary>
        private static void WriteArray(BinaryWriter writer, ConfigField field, Array array)
        {
            writer.Write(array.Length);
            for (int i = 0; i < array.Length; i++)
            {
                object element = array.GetValue(i);
                switch (field.Type)
                {
                    case ConfigFieldType.Int: writer.Write((int)element); break;
                    case ConfigFieldType.Long: writer.Write((long)element); break;
                    case ConfigFieldType.Float: writer.Write((float)element); break;
                    case ConfigFieldType.Bool: writer.Write((bool)element ? (byte)1 : (byte)0); break;
                    case ConfigFieldType.String: writer.Write((string)element); break;
                    case ConfigFieldType.Enum: writer.Write((int)element); break;
                }
            }
        }

        /// <summary>
        /// 把刚生成的二进制立刻读回来验证一遍：字段个数 / 顺序 / 数组长度对不上的话这里就会暴露，
        /// 而不是等到运行时才炸。正常返回 null，异常返回原因。
        /// </summary>
        public static string VerifyBinary(TableSchema schema, ConfigSide side, byte[] bytes)
        {
            List<ConfigField> fields = schema.FieldsOf(side);

            try
            {
                using (ConfigReader reader = new ConfigReader(bytes, schema.Name, schema.HashOf(side)))
                {
                    for (int r = 0; r < reader.RowCount; r++)
                    {
                        for (int c = 0; c < fields.Count; c++)
                            SkipValue(reader, fields[c]);

                        if (reader.Remaining < 0)
                            return "第 " + (r + 1) + " 行读过头了，数据比字段多。";
                    }

                    long left = reader.Remaining;
                    if (left != 0)
                        return "读完 " + reader.RowCount + " 行后还剩 " + left + " 字节，字段个数或顺序和 schema 对不上。";
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }

            return null;
        }

        private static void SkipValue(ConfigReader reader, ConfigField field)
        {
            switch (field.Type)
            {
                case ConfigFieldType.Int:
                    if (field.IsArray) reader.ReadIntArray(); else reader.ReadInt();
                    return;
                case ConfigFieldType.Long:
                    if (field.IsArray) reader.ReadLongArray(); else reader.ReadLong();
                    return;
                case ConfigFieldType.Float:
                    if (field.IsArray) reader.ReadFloatArray(); else reader.ReadFloat();
                    return;
                case ConfigFieldType.Bool:
                    if (field.IsArray) reader.ReadBoolArray(); else reader.ReadBool();
                    return;
                case ConfigFieldType.String:
                    if (field.IsArray) reader.ReadStringArray(); else reader.ReadString();
                    return;
                case ConfigFieldType.Enum:
                    if (field.IsArray) reader.ReadIntArray(); else reader.ReadInt();  // 枚举数组的编码就是 int32 数组
                    return;
            }
        }

        // ============================== JSON 快照（进 git，用来比对差异 / 排查，不打进包） ==============================

        /// <summary>输出完整表（所有字段，含分端字段），只作为人看的快照。</summary>
        public static string BuildJson(TableSchema schema, List<object[]> rows)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[\n");
            for (int r = 0; r < rows.Count; r++)
            {
                if (r > 0) sb.Append(",\n");
                sb.Append("  {");
                for (int c = 0; c < schema.Fields.Count; c++)
                {
                    if (c > 0) sb.Append(", ");
                    sb.Append('"').Append(schema.Fields[c].Name).Append("\": ");
                    AppendJsonValue(sb, schema.Fields[c], rows[r][schema.Fields[c].Index]);
                }
                sb.Append('}');
            }
            sb.Append("\n]\n");
            return sb.ToString();
        }

        private static void AppendJsonValue(StringBuilder sb, ConfigField field, object value)
        {
            if (field.IsArray)
            {
                Array array = (Array)value;
                sb.Append('[');
                for (int i = 0; i < array.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    AppendJsonScalar(sb, field, array.GetValue(i));
                }
                sb.Append(']');
                return;
            }

            AppendJsonScalar(sb, field, value);
        }

        private static void AppendJsonScalar(StringBuilder sb, ConfigField field, object value)
        {
            switch (field.Type)
            {
                case ConfigFieldType.Int: sb.Append(((int)value).ToString(CultureInfo.InvariantCulture)); break;
                case ConfigFieldType.Long: sb.Append(((long)value).ToString(CultureInfo.InvariantCulture)); break;
                case ConfigFieldType.Float: sb.Append(((float)value).ToString("R", CultureInfo.InvariantCulture)); break;
                case ConfigFieldType.Bool: sb.Append((bool)value ? "true" : "false"); break;
                case ConfigFieldType.Enum: sb.Append(((int)value).ToString(CultureInfo.InvariantCulture)); break;
                case ConfigFieldType.String: AppendJsonString(sb, (string)value); break;
            }
        }

        private static void AppendJsonString(StringBuilder sb, string text)
        {
            sb.Append('"');
            if (!string.IsNullOrEmpty(text))
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char ch = text[i];
                    switch (ch)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                            else sb.Append(ch);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        // ============================== C# 代码 ==============================

        /// <summary>
        /// 生成表代码。客户端和服务端共用同一份文件：
        ///   Unity 编译时（定义了 UNITY_5_3_OR_NEWER）只保留 双端 + 客户端 字段，SchemaHash 用 ClientHash；
        ///   ServerHost 编译时（没有该宏）只保留 双端 + 服务端 字段，SchemaHash 用 ServerHash。
        /// 表里没有分端字段时不生成任何 #if，保持输出干净。
        /// </summary>
        public static string GenerateTableCode(TableSchema schema, string nameSpace)
        {
            string rowClass = schema.Name + "Config";
            string tableClass = "Tb" + schema.Name;
            string keyType = KeyTypeName(schema.KeyField);
            bool sided = schema.HasSideFields;

            StringBuilder sb = new StringBuilder();
            Header(sb);
            sb.Append("using System.Collections.Generic;\n\n");
            sb.Append("namespace ").Append(nameSpace).Append("\n{\n");

            sb.Append("    /// <summary>").Append(schema.Name).Append(" 表的数据行（来源：").Append(schema.SourceFile).Append("）。</summary>\n");
            sb.Append("    public sealed class ").Append(rowClass).Append("\n    {\n");
            for (int i = 0; i < schema.Fields.Count; i++)
            {
                ConfigField field = schema.Fields[i];
                OpenSide(sb, field, sided);
                if (!string.IsNullOrEmpty(field.Comment))
                    sb.Append("        /// <summary>").Append(EscapeXml(field.Comment)).Append("</summary>\n");
                sb.Append("        public ").Append(FieldTypeName(field)).Append(' ').Append(field.Name).Append(";\n");
                CloseSide(sb, field, sided);
            }
            sb.Append("    }\n\n");

            sb.Append("    /// <summary>").Append(schema.Name).Append(" 表。</summary>\n");
            sb.Append("    public sealed class ").Append(tableClass).Append(" : ConfigTable<").Append(keyType).Append(", ").Append(rowClass).Append(">\n    {\n");
            sb.Append("        public const string FileName = \"").Append(schema.Name).Append("\";\n");
            if (sided)
            {
                sb.Append("#if UNITY_5_3_OR_NEWER\n");
                sb.Append("        public const int SchemaHash = ").Append(schema.ClientHash.ToString(CultureInfo.InvariantCulture)).Append(";\n");
                sb.Append("#else\n");
                sb.Append("        public const int SchemaHash = ").Append(schema.ServerHash.ToString(CultureInfo.InvariantCulture)).Append(";\n");
                sb.Append("#endif\n\n");
            }
            else
            {
                sb.Append("        public const int SchemaHash = ").Append(schema.ClientHash.ToString(CultureInfo.InvariantCulture)).Append(";\n\n");
            }

            sb.Append("        public static ").Append(tableClass).Append(" Read(byte[] bytes)\n        {\n");
            sb.Append("            ").Append(tableClass).Append(" table = new ").Append(tableClass).Append("();\n");
            sb.Append("            using (ConfigReader reader = new ConfigReader(bytes, FileName, SchemaHash))\n            {\n");
            sb.Append("                for (int i = 0; i < reader.RowCount; i++)\n                {\n");
            sb.Append("                    ").Append(rowClass).Append(" row = new ").Append(rowClass).Append("();\n");
            for (int i = 0; i < schema.Fields.Count; i++)
            {
                ConfigField field = schema.Fields[i];
                OpenSide(sb, field, sided);
                sb.Append("                    row.").Append(field.Name).Append(" = ").Append(ReadExpression(field)).Append(";\n");
                CloseSide(sb, field, sided);
            }
            sb.Append("                    table.Add(row);\n                }\n            }\n            return table;\n        }\n\n");

            sb.Append("        protected override ").Append(keyType).Append(" KeyOf(").Append(rowClass).Append(" row) { return row.").Append(schema.KeyField.Name).Append("; }\n");
            sb.Append("    }\n}\n");
            return sb.ToString();
        }

        /// <summary>分端字段：客户端编译时保留 C 字段，服务端编译时保留 S 字段。</summary>
        private static void OpenSide(StringBuilder sb, ConfigField field, bool sided)
        {
            if (!sided || field.Group == ConfigFieldGroup.Both) return;
            sb.Append(field.Group == ConfigFieldGroup.Client ? "#if UNITY_5_3_OR_NEWER\n" : "#if !UNITY_5_3_OR_NEWER\n");
        }

        private static void CloseSide(StringBuilder sb, ConfigField field, bool sided)
        {
            if (!sided || field.Group == ConfigFieldGroup.Both) return;
            sb.Append("#endif\n");
        }

        public static string GenerateEnumsCode(IList<EnumDefinition> enums, string nameSpace)
        {
            StringBuilder sb = new StringBuilder();
            Header(sb);
            sb.Append("namespace ").Append(nameSpace).Append("\n{\n");
            for (int i = 0; i < enums.Count; i++)
            {
                EnumDefinition def = enums[i];
                sb.Append("    /// <summary>").Append(def.Name).Append("（来源：__enums__.xlsx）。</summary>\n");
                sb.Append("    public enum ").Append(def.Name).Append("\n    {\n");
                for (int j = 0; j < def.Values.Count; j++)
                    sb.Append("        ").Append(def.Values[j].Key).Append(" = ").Append(def.Values[j].Value.ToString(CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("    }\n\n");
            }
            sb.Append("}\n");
            return sb.ToString();
        }

        public static string GenerateDatabaseCode(IList<TableSchema> tables, string nameSpace)
        {
            StringBuilder sb = new StringBuilder();
            Header(sb);
            sb.Append("using System;\nusing System.Collections.Generic;\n\n");
            sb.Append("namespace ").Append(nameSpace).Append("\n{\n");
            sb.Append("    public sealed partial class ConfigDatabase\n    {\n");
            sb.Append("        /// <summary>所有表的文件名（不含扩展名），客户端用它预加载。</summary>\n");
            sb.Append("        public static readonly string[] TableNames = { ");
            for (int i = 0; i < tables.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('"').Append(tables[i].Name).Append('"');
            }
            sb.Append(" };\n\n");

            for (int i = 0; i < tables.Count; i++)
                sb.Append("        public Tb").Append(tables[i].Name).Append(' ').Append(tables[i].Name).Append(" { get; private set; }\n");

            sb.Append("\n        partial void LoadTables(Func<string, byte[]> readFile, List<object> loaded)\n        {\n");
            for (int i = 0; i < tables.Count; i++)
            {
                string name = tables[i].Name;
                sb.Append("            ").Append(name).Append(" = Tb").Append(name).Append(".Read(readFile(Tb").Append(name).Append(".FileName));\n");
                sb.Append("            loaded.Add(").Append(name).Append(");\n");
            }
            sb.Append("        }\n\n");

            sb.Append("        partial void ClearTables()\n        {\n");
            for (int i = 0; i < tables.Count; i++)
                sb.Append("            ").Append(tables[i].Name).Append(" = null;\n");
            sb.Append("        }\n");
            sb.Append("    }\n}\n");
            return sb.ToString();
        }

        private static void Header(StringBuilder sb)
        {
            sb.Append("//------------------------------------------------------------------------------\n");
            sb.Append("// <auto-generated>\n");
            sb.Append("//     配置表导出工具生成，不要手改；改 Excel 后重新执行 Tools/配置表/导出全部表。\n");
            sb.Append("// </auto-generated>\n");
            sb.Append("//------------------------------------------------------------------------------\n");
        }

        private static string EscapeXml(string text)
        {
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        public static string FieldTypeName(ConfigField field)
        {
            string name;
            switch (field.Type)
            {
                case ConfigFieldType.Int: name = "int"; break;
                case ConfigFieldType.Long: name = "long"; break;
                case ConfigFieldType.Float: name = "float"; break;
                case ConfigFieldType.Bool: name = "bool"; break;
                case ConfigFieldType.String: name = "string"; break;
                case ConfigFieldType.Enum: name = field.EnumName; break;
                default: name = "int"; break;
            }
            return field.IsArray ? name + "[]" : name;
        }

        private static string KeyTypeName(ConfigField field)
        {
            return FieldTypeName(field);
        }

        private static string ReadExpression(ConfigField field)
        {
            if (field.IsArray)
            {
                switch (field.Type)
                {
                    case ConfigFieldType.Int: return "reader.ReadIntArray()";
                    case ConfigFieldType.Long: return "reader.ReadLongArray()";
                    case ConfigFieldType.Float: return "reader.ReadFloatArray()";
                    case ConfigFieldType.Bool: return "reader.ReadBoolArray()";
                    case ConfigFieldType.String: return "reader.ReadStringArray()";
                    case ConfigFieldType.Enum: return "reader.ReadEnumArray<" + field.EnumName + ">()";
                }
                return "reader.ReadIntArray()";
            }

            switch (field.Type)
            {
                case ConfigFieldType.Int: return "reader.ReadInt()";
                case ConfigFieldType.Long: return "reader.ReadLong()";
                case ConfigFieldType.Float: return "reader.ReadFloat()";
                case ConfigFieldType.Bool: return "reader.ReadBool()";
                case ConfigFieldType.String: return "reader.ReadString()";
                case ConfigFieldType.Enum: return "(" + field.EnumName + ")reader.ReadInt()";
            }
            return "reader.ReadInt()";
        }
    }
}