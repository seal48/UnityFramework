using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace GameFramework.Config.EditorTools
{
    /// <summary>一张 sheet，展开成"行列矩阵"，已按单元格的 r 属性补齐空位（Excel 会省略空单元格）。</summary>
    public sealed class XlsxSheet
    {
        private readonly List<List<string>> rows = new List<List<string>>();

        public string Name { get; set; }

        public int RowCount { get { return rows.Count; } }

        public string Get(int row, int col)
        {
            if (row < 0 || row >= rows.Count) return string.Empty;
            List<string> line = rows[row];
            if (col < 0 || col >= line.Count) return string.Empty;
            return line[col] ?? string.Empty;
        }

        public int ColumnCount
        {
            get
            {
                int max = 0;
                for (int i = 0; i < rows.Count; i++)
                    if (rows[i].Count > max) max = rows[i].Count;
                return max;
            }
        }

        internal void Set(int row, int col, string value)
        {
            while (rows.Count <= row) rows.Add(new List<string>());
            List<string> line = rows[row];
            while (line.Count <= col) line.Add(string.Empty);
            line[col] = value;
        }
    }

    /// <summary>
    /// 零依赖读取 .xlsx（本质是 zip + OpenXML）。只实现配置表需要的部分：
    /// 共享字符串、单元格值、行列定位。
    /// 不计算公式（读 Excel 存下来的缓存值）、不处理富文本样式、不处理合并单元格。
    /// </summary>
    public static class XlsxReader
    {
        private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace DocRels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        public static List<XlsxSheet> ReadAllSheets(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                string[] shared = ReadSharedStrings(zip);
                List<KeyValuePair<string, string>> sheets = ReadSheetList(zip);

                List<XlsxSheet> result = new List<XlsxSheet>();
                for (int i = 0; i < sheets.Count; i++)
                {
                    XlsxSheet sheet = new XlsxSheet();
                    sheet.Name = sheets[i].Key;
                    ReadSheet(zip, sheets[i].Value, shared, sheet);
                    result.Add(sheet);
                }
                return result;
            }
        }

        private static string[] ReadSharedStrings(ZipArchive zip)
        {
            ZipArchiveEntry entry = zip.GetEntry("xl/sharedStrings.xml");
            if (entry == null) return new string[0];

            List<string> list = new List<string>();
            using (Stream stream = entry.Open())
            {
                XDocument doc = XDocument.Load(stream);
                if (doc.Root != null)
                {
                    foreach (XElement si in doc.Root.Elements(Main + "si"))
                    {
                        StringBuilder sb = new StringBuilder();
                        foreach (XElement t in si.Descendants(Main + "t"))
                            sb.Append(t.Value);
                        list.Add(sb.ToString());
                    }
                }
            }
            return list.ToArray();
        }

        private static List<KeyValuePair<string, string>> ReadSheetList(ZipArchive zip)
        {
            Dictionary<string, string> rels = new Dictionary<string, string>();
            ZipArchiveEntry relEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
            if (relEntry != null)
            {
                using (Stream stream = relEntry.Open())
                {
                    XDocument doc = XDocument.Load(stream);
                    if (doc.Root != null)
                    {
                        foreach (XElement rel in doc.Root.Elements())
                        {
                            string id = (string)rel.Attribute("Id");
                            string target = (string)rel.Attribute("Target");
                            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(target))
                                rels[id] = target;
                        }
                    }
                }
            }

            List<KeyValuePair<string, string>> sheets = new List<KeyValuePair<string, string>>();
            ZipArchiveEntry workbook = zip.GetEntry("xl/workbook.xml");
            if (workbook == null) return sheets;

            using (Stream stream = workbook.Open())
            {
                XDocument doc = XDocument.Load(stream);
                XElement container = doc.Root == null ? null : doc.Root.Element(Main + "sheets");
                if (container == null) return sheets;

                foreach (XElement sheet in container.Elements(Main + "sheet"))
                {
                    string name = (string)sheet.Attribute("name");
                    string rid = (string)sheet.Attribute(DocRels + "id");

                    string target;
                    if (string.IsNullOrEmpty(rid) || !rels.TryGetValue(rid, out target))
                        continue;

                    string entryPath = target.Replace('\\', '/');
                    if (entryPath.StartsWith("/", StringComparison.Ordinal))
                        entryPath = entryPath.TrimStart('/');
                    else if (!entryPath.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                        entryPath = "xl/" + entryPath;

                    sheets.Add(new KeyValuePair<string, string>(name, entryPath));
                }
            }
            return sheets;
        }

        private static void ReadSheet(ZipArchive zip, string entryPath, string[] shared, XlsxSheet sheet)
        {
            ZipArchiveEntry entry = zip.GetEntry(entryPath);
            if (entry == null) return;

            using (Stream stream = entry.Open())
            {
                XDocument doc = XDocument.Load(stream);
                XElement data = doc.Root == null ? null : doc.Root.Element(Main + "sheetData");
                if (data == null) return;

                int nextRow = 0;
                foreach (XElement rowEl in data.Elements(Main + "row"))
                {
                    int rowIndex = nextRow;
                    string rowRef = (string)rowEl.Attribute("r");
                    int parsedRow;
                    if (!string.IsNullOrEmpty(rowRef) && int.TryParse(rowRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedRow))
                        rowIndex = parsedRow - 1;
                    nextRow = rowIndex + 1;

                    int nextCol = 0;
                    foreach (XElement cell in rowEl.Elements(Main + "c"))
                    {
                        int colIndex = nextCol;
                        string cellRef = (string)cell.Attribute("r");
                        if (!string.IsNullOrEmpty(cellRef))
                        {
                            int parsedCol = ColumnFromRef(cellRef);
                            if (parsedCol >= 0) colIndex = parsedCol;
                        }
                        nextCol = colIndex + 1;

                        sheet.Set(rowIndex, colIndex, ReadCellValue(cell, shared));
                    }
                }
            }
        }

        private static string ReadCellValue(XElement cell, string[] shared)
        {
            string type = (string)cell.Attribute("t");

            if (type == "inlineStr")
            {
                XElement inline = cell.Element(Main + "is");
                if (inline == null) return string.Empty;
                StringBuilder sb = new StringBuilder();
                foreach (XElement t in inline.Descendants(Main + "t"))
                    sb.Append(t.Value);
                return sb.ToString();
            }

            XElement value = cell.Element(Main + "v");
            if (value == null) return string.Empty;

            string raw = value.Value ?? string.Empty;

            if (type == "s")
            {
                int index;
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
                    && index >= 0 && index < shared.Length)
                    return shared[index];
                return string.Empty;
            }

            if (type == "b") return raw == "1" ? "true" : "false";

            return raw;
        }

        /// <summary>把 "B12" 里的列号解析成 0-based 下标；解析不出来返回 -1。</summary>
        private static int ColumnFromRef(string cellRef)
        {
            int column = 0;
            bool any = false;
            for (int i = 0; i < cellRef.Length; i++)
            {
                char ch = cellRef[i];
                if (ch >= 'A' && ch <= 'Z') { column = column * 26 + (ch - 'A' + 1); any = true; }
                else if (ch >= 'a' && ch <= 'z') { column = column * 26 + (ch - 'a' + 1); any = true; }
                else break;
            }
            return any ? column - 1 : -1;
        }
    }
}