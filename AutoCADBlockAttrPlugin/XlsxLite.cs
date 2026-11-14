using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Packaging;
using System.Xml;

namespace AutoCADBlockAttrPlugin
{
    /// <summary>
    /// Minimal XLSX reader/writer based on the .NET Framework Packaging API.
    /// Uses inline strings when exporting and also reads Excel shared strings.
    /// No third-party DLL is required; compatible with .NET 3.5.
    /// </summary>
    internal static class XlsxLite
    {
        private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
        private const string OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
        private const string WorksheetRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet";

        internal static void Write(string path, IList<string> headers, IList<IList<string>> rows)
        {
            if (File.Exists(path)) File.Delete(path);
            using (Package package = Package.Open(path, FileMode.Create, FileAccess.ReadWrite))
            {
                Uri workbookUri = new Uri("/xl/workbook.xml", UriKind.Relative);
                Uri sheetUri = new Uri("/xl/worksheets/sheet1.xml", UriKind.Relative);
                PackagePart workbook = package.CreatePart(workbookUri,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
                PackagePart sheet = package.CreatePart(sheetUri,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
                package.CreateRelationship(workbookUri, TargetMode.Internal, OfficeRel);
                workbook.CreateRelationship(new Uri("worksheets/sheet1.xml", UriKind.Relative),
                    TargetMode.Internal, WorksheetRel);

                using (XmlWriter xw = XmlWriter.Create(workbook.GetStream(FileMode.Create, FileAccess.Write)))
                {
                    xw.WriteStartDocument();
                    xw.WriteStartElement("workbook", MainNs);
                    xw.WriteAttributeString("xmlns", "r", null, RelNs);
                    xw.WriteStartElement("sheets", MainNs);
                    xw.WriteStartElement("sheet", MainNs);
                    xw.WriteAttributeString("name", "属性数据");
                    xw.WriteAttributeString("sheetId", "1");
                    xw.WriteAttributeString("r", "id", RelNs, workbook.GetRelationshipsByType(WorksheetRel).GetEnumerator().MoveNext() ? "rId1" : "rId1");
                    xw.WriteEndElement();
                    xw.WriteEndElement();
                    xw.WriteEndElement();
                    xw.WriteEndDocument();
                }

                using (XmlWriter xw = XmlWriter.Create(sheet.GetStream(FileMode.Create, FileAccess.Write)))
                {
                    xw.WriteStartDocument();
                    xw.WriteStartElement("worksheet", MainNs);
                    xw.WriteStartElement("sheetData", MainNs);
                    WriteRow(xw, 1, headers);
                    for (int i = 0; i < rows.Count; i++)
                        WriteRow(xw, i + 2, rows[i]);
                    xw.WriteEndElement();
                    xw.WriteEndElement();
                    xw.WriteEndDocument();
                }
                package.Flush();
            }
        }

        private static void WriteRow(XmlWriter xw, int rowNumber, IList<string> values)
        {
            xw.WriteStartElement("row", MainNs);
            xw.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < values.Count; i++)
            {
                xw.WriteStartElement("c", MainNs);
                xw.WriteAttributeString("r", ColumnName(i + 1) + rowNumber.ToString(CultureInfo.InvariantCulture));
                xw.WriteAttributeString("t", "inlineStr");
                xw.WriteStartElement("is", MainNs);
                xw.WriteStartElement("t", MainNs);
                xw.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                xw.WriteString(values[i] ?? "");
                xw.WriteEndElement();
                xw.WriteEndElement();
                xw.WriteEndElement();
            }
            xw.WriteEndElement();
        }

        internal static List<List<string>> Read(string path)
        {
            List<List<string>> result = new List<List<string>>();
            using (Package package = Package.Open(path, FileMode.Open, FileAccess.Read))
            {
                Uri sheetUri = new Uri("/xl/worksheets/sheet1.xml", UriKind.Relative);
                PackagePart sheet = package.GetPart(sheetUri);
                List<string> shared = ReadSharedStrings(package);
                XmlDocument doc = new XmlDocument();
                using (Stream stream = sheet.GetStream(FileMode.Open, FileAccess.Read))
                    doc.Load(stream);
                XmlNamespaceManager ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("x", MainNs);
                XmlNodeList rows = doc.SelectNodes("//x:sheetData/x:row", ns);
                foreach (XmlNode rowNode in rows)
                {
                    SortedDictionary<int, string> cells = new SortedDictionary<int, string>();
                    foreach (XmlNode cell in rowNode.SelectNodes("x:c", ns))
                    {
                        XmlAttribute reference = cell.Attributes["r"];
                        if (reference == null) continue;
                        int col = ColumnNumber(reference.Value);
                        string type = cell.Attributes["t"] == null ? "" : cell.Attributes["t"].Value;
                        string value = "";
                        if (type == "inlineStr")
                        {
                            XmlNode text = cell.SelectSingleNode("x:is/x:t", ns);
                            value = text == null ? "" : text.InnerText;
                        }
                        else
                        {
                            XmlNode v = cell.SelectSingleNode("x:v", ns);
                            value = v == null ? "" : v.InnerText;
                            if (type == "s" && value.Length > 0)
                            {
                                int index;
                                if (int.TryParse(value, out index) && index >= 0 && index < shared.Count)
                                    value = shared[index];
                            }
                        }
                        cells[col] = value;
                    }
                    int max = cells.Count == 0 ? 0 : MaxKey(cells);
                    List<string> values = new List<string>();
                    for (int i = 1; i <= max; i++)
                        values.Add(cells.ContainsKey(i) ? cells[i] : "");
                    result.Add(values);
                }
            }
            return result;
        }

        private static List<string> ReadSharedStrings(Package package)
        {
            List<string> values = new List<string>();
            Uri uri = new Uri("/xl/sharedStrings.xml", UriKind.Relative);
            if (!package.PartExists(uri)) return values;
            XmlDocument doc = new XmlDocument();
            using (Stream stream = package.GetPart(uri).GetStream(FileMode.Open, FileAccess.Read))
                doc.Load(stream);
            XmlNamespaceManager ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("x", MainNs);
            foreach (XmlNode item in doc.SelectNodes("//x:si", ns))
                values.Add(item.InnerText);
            return values;
        }

        private static int MaxKey(SortedDictionary<int, string> values)
        {
            int max = 0;
            foreach (int key in values.Keys) if (key > max) max = key;
            return max;
        }

        private static string ColumnName(int number)
        {
            string name = "";
            while (number > 0)
            {
                int rem = (number - 1) % 26;
                name = (char)('A' + rem) + name;
                number = (number - 1) / 26;
            }
            return name;
        }

        private static int ColumnNumber(string reference)
        {
            int value = 0;
            for (int i = 0; i < reference.Length && char.IsLetter(reference[i]); i++)
                value = value * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
            return value;
        }
    }
}