using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GameFramework.Config.EditorTools
{
    /// <summary>
    /// 配置表字符集扫描：收集所有「客户端可见 string 字段」里出现的字符，
    /// 输出一份字符清单文件，供字体子集化（fontTools / pyftsubset）使用。
    ///
    /// 原则：宁多勿少 —— 字符集覆盖不足 = 界面某个字渲染成方块。
    /// 固定保留 ASCII 可打印段（0x20~0x7E）+ 换行 + 常用全角标点，
    /// 保证数字 / 英文 / 标点永远不缺（子集化时别把拉丁裁掉）。
    /// </summary>
    public static class ConfigCharSet
    {
        /// <summary>输出文件名（放在配置表 Json 快照目录里，进 git、不打进包）。</summary>
        public const string OutputFileName = "used_chars.txt";

        /// <summary>常用全角标点（中文字体里必须有的），固定补进去。</summary>
        private const string CjkPunctuation =
            "，。！？：；、“”‘’（）《》【】—…·～￥％×÷＋－＝＜＞";

        /// <summary>
        /// 扫描全部表的客户端可见字符串字段（含数组元素），返回去重后的字符集（按码点排序）。
        /// </summary>
        public static List<char> Collect(IList<TableSchema> schemas, IList<List<object[]>> dataSets)
        {
            HashSet<char> set = new HashSet<char>();

            // 固定段：ASCII 可打印 + 常用全角标点
            // 注意：换行 / 制表符不是渲染字形（排版处理换行），不需要进字体子集
            for (char c = '\u0020'; c <= '\u007E'; c++)
                set.Add(c);
            for (int i = 0; i < CjkPunctuation.Length; i++)
                set.Add(CjkPunctuation[i]);

            for (int t = 0; t < schemas.Count && t < dataSets.Count; t++)
            {
                TableSchema schema = schemas[t];
                List<object[]> rows = dataSets[t];

                // 只看客户端能看到的 string 字段（Both / Client）；服务端字段客户端不显示
                List<ConfigField> fields = new List<ConfigField>();
                for (int f = 0; f < schema.Fields.Count; f++)
                {
                    ConfigField field = schema.Fields[f];
                    if (field.Type == ConfigFieldType.String && field.Group != ConfigFieldGroup.Server)
                        fields.Add(field);
                }
                if (fields.Count == 0)
                    continue;

                for (int r = 0; r < rows.Count; r++)
                {
                    object[] values = rows[r];
                    if (values == null)
                        continue;

                    for (int f = 0; f < fields.Count; f++)
                    {
                        ConfigField field = fields[f];
                        if (field.Index >= values.Length)
                            continue;

                        object value = values[field.Index];
                        if (value == null)
                            continue;

                        if (field.IsArray)
                        {
                            string[] array = value as string[];
                            if (array == null)
                                continue;
                            for (int a = 0; a < array.Length; a++)
                                AddText(set, array[a]);
                        }
                        else
                        {
                            AddText(set, value as string);
                        }
                    }
                }
            }

            List<char> result = new List<char>(set);
            result.Sort();
            return result;
        }

        /// <summary>
        /// 写入字符清单文件。带说明头；pyftsubset 用 --text-file 直接吃（注释行只含 ASCII，无副作用）。
        /// </summary>
        public static void WriteFile(string path, IList<char> chars)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("# 配置表文本字符集 —— 导出工具自动生成，用于字体子集化（pyftsubset --text-file）\n");
            sb.Append("# 收集范围：所有表里客户端可见的 string 字段（含数组元素）+ 固定 ASCII 段与常用全角标点\n");
            sb.Append("# 字符数：" + chars.Count + "（一行一个字符；缺字 = 界面方块，宁多勿少）\n");
            for (int i = 0; i < chars.Count; i++)
                sb.Append(chars[i]).Append('\n');

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static void AddText(HashSet<char> set, string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            for (int i = 0; i < text.Length; i++)
                set.Add(text[i]);
        }
    }
}
