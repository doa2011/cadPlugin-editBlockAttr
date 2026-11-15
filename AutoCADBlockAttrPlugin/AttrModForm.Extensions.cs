using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCADBlockAttrPlugin
{
    public partial class AttrModForm
    {
        private ComboBox _angleTitleName;
        private ComboBox _angleDrawingNo;
        private ComboBox _anglePageNo;
        private ComboBox _anglePageTotal;
        private TextBox _angleTitleValue;
        private TextBox _angleDrawingNoValue;
        private TextBox _anglePageTotalValue;
        private DataGridView _xlsxPreview;
        private readonly List<XlsxChange> _xlsxChanges = new List<XlsxChange>();

        private sealed class XlsxChange
        {
            internal ObjectId Id;
            internal string Handle;
            internal string Tag;
            internal string OldValue;
            internal string NewValue;
        }

        private void InitializeExtendedTabs()
        {
            List<Control> original = new List<Control>();
            foreach (Control control in Controls) original.Add(control);

            Size = new System.Drawing.Size(1200, 800);
            MinimumSize = new System.Drawing.Size(1000, 700);
            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            TabPage angle = new TabPage("角标修改");
            TabPage dynamic = new TabPage("动态块参数");
            tabs.TabPages.Add(angle);
            tabs.TabPages.Add(dynamic);

            foreach (Control control in original)
                Controls.Remove(control);

            BuildAngleTab(angle);
            DynamicBlockTab.Build(dynamic, _blockIds, _blockName);
            Controls.Add(tabs);
        }

        private void BuildAngleTab(Control page)
        {
            Label intro = new Label();
            intro.Text = "选择角标块后自动读取当前 DWG 文件名。文件名格式：图号 空格 图名。";
            intro.Location = new System.Drawing.Point(20, 18);
            intro.Size = new System.Drawing.Size(700, 30);
            page.Controls.Add(intro);

            _angleTitleName = AddAngleCombo(page, "图名属性 TAG", 65);
            _angleDrawingNo = AddAngleCombo(page, "图号属性 TAG", 110);
            _anglePageNo = AddAngleCombo(page, "第几页属性 TAG", 155);
            _anglePageTotal = AddAngleCombo(page, "共几页属性 TAG", 200);

            GroupBox sort = new GroupBox();
            sort.Text = "页码排序";
            sort.Location = new System.Drawing.Point(20, 250);
            sort.Size = new System.Drawing.Size(700, 65);
            RadioButton left = new RadioButton();
            left.Text = "从左至右（X 升序，Y 降序）";
            left.Location = new System.Drawing.Point(18, 27);
            left.Size = new System.Drawing.Size(280, 22);
            left.Checked = true;
            RadioButton top = new RadioButton();
            top.Text = "从上到下（Y 降序，X 升序）";
            top.Location = new System.Drawing.Point(350, 27);
            top.Size = new System.Drawing.Size(280, 22);
            sort.Controls.Add(left);
            sort.Controls.Add(top);
            page.Controls.Add(sort);

            Label titleLabel = new Label();
            titleLabel.Text = "图名（自动读取，可修改）";
            titleLabel.Location = new System.Drawing.Point(25, 335);
            titleLabel.Size = new System.Drawing.Size(180, 22);
            _angleTitleValue = new TextBox();
            _angleTitleValue.Location = new System.Drawing.Point(210, 332);
            _angleTitleValue.Size = new System.Drawing.Size(450, 24);
            Label numberLabel = new Label();
            numberLabel.Text = "图号（自动读取，可修改）";
            numberLabel.Location = new System.Drawing.Point(25, 375);
            numberLabel.Size = new System.Drawing.Size(180, 22);
            _angleDrawingNoValue = new TextBox();
            _angleDrawingNoValue.Location = new System.Drawing.Point(210, 372);
            _angleDrawingNoValue.Size = new System.Drawing.Size(450, 24);
            page.Controls.Add(titleLabel);
            page.Controls.Add(_angleTitleValue);
            page.Controls.Add(numberLabel);
            page.Controls.Add(_angleDrawingNoValue);

            Label totalLabel = new Label();
            totalLabel.Text = "共几页（自动读取，可修改）";
            totalLabel.Location = new System.Drawing.Point(25, 415);
            totalLabel.Size = new System.Drawing.Size(180, 22);
            _anglePageTotalValue = new TextBox();
            _anglePageTotalValue.Location = new System.Drawing.Point(210, 412);
            _anglePageTotalValue.Size = new System.Drawing.Size(120, 24);
            page.Controls.Add(totalLabel);
            page.Controls.Add(_anglePageTotalValue);

            Label fileInfo = new Label();
            fileInfo.Name = "AngleFileInfo";
            fileInfo.Location = new System.Drawing.Point(20, 455);
            fileInfo.Size = new System.Drawing.Size(700, 55);
            UpdateAngleFileInfo(fileInfo);
            page.Controls.Add(fileInfo);

            Button apply = new Button();
            apply.Text = "自动填写角标";
            apply.Location = new System.Drawing.Point(20, 525);
            apply.Size = new System.Drawing.Size(130, 34);
            apply.Click += delegate { ApplyAngleValues(left.Checked); };
            page.Controls.Add(apply);

            Label note = new Label();
            note.Text = "提示：四个字段分别映射到块的实际属性 TAG；映射不能为空且不能重复。“共几页”默认读取匹配块数量，也可手动修改后统一写入。";
            note.Location = new System.Drawing.Point(20, 575);
            note.Size = new System.Drawing.Size(700, 55);
            page.Controls.Add(note);

            PopulateAngleTags();
        }

        private ComboBox AddAngleCombo(Control page, string label, int y)
        {
            Label l = new Label();
            l.Text = label;
            l.Location = new System.Drawing.Point(25, y + 4);
            l.Size = new System.Drawing.Size(130, 22);
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Location = new System.Drawing.Point(165, y);
            c.Size = new System.Drawing.Size(350, 24);
            page.Controls.Add(l);
            page.Controls.Add(c);
            return c;
        }

        private void UpdateAngleFileInfo(Label label)
        {
            string full = CadHost.ActiveDocument == null ? "" : CadHost.ActiveDocument.Name;
            string file = Path.GetFileNameWithoutExtension(full);
            string no;
            string title;
            SplitName(file, out no, out title);
            label.Text = "当前 DWG：" + file + "\r\n文件名解析规则：图号 空格 图名；总页数默认读取匹配角标块数量，可手动调整。";
            if (_angleDrawingNoValue != null) _angleDrawingNoValue.Text = no;
            if (_anglePageTotalValue != null) _anglePageTotalValue.Text = _blockIds.Length.ToString();
            if (_angleTitleValue != null) _angleTitleValue.Text = title;
        }

        private static void SplitName(string file, out string no, out string title)
        {
            int p = file.IndexOf(' ');
            if (p < 0) { no = file.Trim(); title = ""; }
            else { no = file.Substring(0, p).Trim(); title = file.Substring(p + 1).Trim(); }
        }

        private void PopulateAngleTags()
        {
            ComboBox[] boxes = { _angleTitleName, _angleDrawingNo, _anglePageNo, _anglePageTotal };
            string[] keys = { "TITLE", "DWG", "PAGE", "TOTAL" };
            foreach (ComboBox box in boxes)
            {
                box.Items.Clear();
                box.Items.Add("");
                foreach (AttrRow row in _rows) box.Items.Add(row.Tag);
                box.SelectedIndex = 0;
            }
            for (int i = 0; i < boxes.Length; i++)
            {
                foreach (AttrRow row in _rows)
                {
                    string tag = row.Tag.ToUpperInvariant();
                    bool match = i == 0
                        ? (tag.Contains("TITLE") || tag.Contains("NAME") || row.Tag.Contains("图名"))
                        : i == 1
                            ? (tag.Contains("DWG") || tag.Contains("NUMBER") || tag == "NO" || row.Tag.Contains("图号"))
                            : i == 2
                                ? (tag.Contains("PAGE") || (tag.Contains("SHEET") && !tag.Contains("TOTAL")) || row.Tag.Contains("页"))
                                : (tag.Contains("TOTAL") || tag.Contains("COUNT") || tag.Contains("SHEETS") || row.Tag.Contains("共"));
                    if (match) { boxes[i].SelectedItem = row.Tag; break; }
                }
            }
        }

        private void ApplyAngleValues(bool leftToRight)
        {
            string[] tags = {
                SelectedTag(_angleTitleName), SelectedTag(_angleDrawingNo),
                SelectedTag(_anglePageNo), SelectedTag(_anglePageTotal)
            };
            if (tags.Any(delegate(string t) { return string.IsNullOrEmpty(t); }))
            {
                MessageBox.Show("请完成图名、图号、第几页、共几页四项属性 TAG 映射。", "角标设置");
                return;
            }
            if (tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4)
            {
                MessageBox.Show("四个字段不能映射到重复的属性 TAG。", "角标设置");
                return;
            }

            string file = Path.GetFileNameWithoutExtension(CadHost.ActiveDocument.Name);
            string drawingNo;
            string drawingTitle;
            SplitName(file, out drawingNo, out drawingTitle);
            if (_angleDrawingNoValue != null) drawingNo = _angleDrawingNoValue.Text;
            if (_angleTitleValue != null) drawingTitle = _angleTitleValue.Text;
            int pageTotal;
            if (_anglePageTotalValue == null ||
                !int.TryParse(_anglePageTotalValue.Text, out pageTotal) || pageTotal < 1)
            {
                MessageBox.Show("“共几页”必须填写大于 0 的整数。", "角标设置");
                return;
            }
            Document doc = CadHost.ActiveDocument;
            int changed = 0;
            int missing = 0;
            int count = 0;
            using (DocumentLock dl = doc.LockDocument())
            using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                List<AngleSortItem> blocks = new List<AngleSortItem>();
                foreach (ObjectId id in _blockIds)
                {
                    BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br != null)
                        blocks.Add(new AngleSortItem { Id = id, X = br.Position.X, Y = br.Position.Y });
                }
                if (leftToRight)
                    blocks = blocks.OrderBy(b => b.X).ThenByDescending(b => b.Y).ToList();
                else
                    blocks = blocks.OrderByDescending(b => b.Y).ThenBy(b => b.X).ToList();

                count = blocks.Count;
                for (int i = 0; i < blocks.Count; i++)
                {
                    BlockReference br = tr.GetObject(blocks[i].Id, OpenMode.ForRead) as BlockReference;
                    Dictionary<string, AttributeReference> attrs = ReadAttributes(br, tr, OpenMode.ForWrite);
                    string[] values = { drawingTitle, drawingNo, (i + 1).ToString(), pageTotal.ToString() };
                    for (int k = 0; k < tags.Length; k++)
                    {
                        AttributeReference ar;
                        if (!attrs.TryGetValue(tags[k], out ar)) { missing++; continue; }
                        if (!string.Equals(ar.TextString, values[k], StringComparison.Ordinal))
                        {
                            ar.TextString = values[k];
                            changed++;
                        }
                    }
                }
                tr.Commit();
            }
            MessageBox.Show("角标修改完成。\r\n块数量：" + count + "\r\n实际修改：" +
                changed + " 处\r\n缺少属性：" + missing + " 处", "角标修改");
        }

        private static string SelectedTag(ComboBox box)
        {
            return box.SelectedItem == null ? "" : box.SelectedItem.ToString();
        }

        private struct AngleSortItem
        {
            internal ObjectId Id;
            internal double X;
            internal double Y;
        }

        private void BuildXlsxPanel(Control page)
        {
            GroupBox group = new GroupBox();
            group.Text = "Excel 交互（Handle 定位；空白单元格不修改；输入 [[CLEAR]] 可清空属性）";
            group.Location = new System.Drawing.Point(20, 525);
            group.Size = new System.Drawing.Size(710, 125);

            Button export = new Button();
            export.Text = "导出 XLSX";
            export.Location = new System.Drawing.Point(12, 22);
            export.Size = new System.Drawing.Size(100, 26);
            export.Click += delegate { ExportXlsx(); };

            Button import = new Button();
            import.Text = "导入并预览";
            import.Location = new System.Drawing.Point(120, 22);
            import.Size = new System.Drawing.Size(110, 26);
            import.Click += delegate { ImportXlsx(); };

            Button apply = new Button();
            apply.Text = "确认写入 CAD";
            apply.Location = new System.Drawing.Point(238, 22);
            apply.Size = new System.Drawing.Size(115, 26);
            apply.Click += delegate { ApplyXlsx(); };

            _xlsxPreview = new DataGridView();
            _xlsxPreview.Location = new System.Drawing.Point(12, 54);
            _xlsxPreview.Size = new System.Drawing.Size(680, 62);
            _xlsxPreview.ReadOnly = true;
            _xlsxPreview.AllowUserToAddRows = false;
            _xlsxPreview.AllowUserToDeleteRows = false;
            _xlsxPreview.RowHeadersVisible = false;
            _xlsxPreview.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
            group.Controls.Add(export);
            group.Controls.Add(import);
            group.Controls.Add(apply);
            group.Controls.Add(_xlsxPreview);
            page.Controls.Add(group);
        }

        private static Dictionary<string, AttributeReference> ReadAttributes(
            BlockReference br, Transaction tr, OpenMode mode)
        {
            Dictionary<string, AttributeReference> attrs =
                new Dictionary<string, AttributeReference>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId attId in br.AttributeCollection)
            {
                AttributeReference ar = tr.GetObject(attId, mode) as AttributeReference;
                if (ar != null && !string.IsNullOrEmpty(ar.Tag) && !attrs.ContainsKey(ar.Tag))
                    attrs.Add(ar.Tag, ar);
            }
            return attrs;
        }

        private void ExportXlsx()
        {
            SaveFileDialog dlg = new SaveFileDialog();
            dlg.Filter = "Excel 工作簿 (*.xlsx)|*.xlsx";
            dlg.FileName = "块属性数据.xlsx";
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                List<string> headers = new List<string>();
                headers.Add("Handle");
                headers.Add("BlockName");
                foreach (AttrRow row in _rows) headers.Add(row.Tag);
                List<IList<string>> rows = new List<IList<string>>();
                Document doc = CadHost.ActiveDocument;
                using (Transaction tr = doc.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in _blockIds)
                    {
                        BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (br == null) continue;
                        Dictionary<string, AttributeReference> attrs = ReadAttributes(br, tr, OpenMode.ForRead);
                        List<string> values = new List<string>();
                        values.Add(br.Handle.ToString());
                        values.Add(_blockName);
                        foreach (AttrRow row in _rows)
                        {
                            AttributeReference ar;
                            values.Add(attrs.TryGetValue(row.Tag, out ar) ? ar.TextString : "");
                        }
                        rows.Add(values);
                    }
                    tr.Commit();
                }
                XlsxLite.Write(dlg.FileName, headers, rows);
                MessageBox.Show("已导出 " + rows.Count + " 个块的属性。", "Excel 导出");
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("Excel 导出失败：" + ex.Message, "错误");
            }
        }

        private void ImportXlsx()
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "Excel 工作簿 (*.xlsx)|*.xlsx";
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                List<List<string>> table = XlsxLite.Read(dlg.FileName);
                if (table.Count < 2 || table[0].Count < 2 ||
                    !string.Equals(table[0][0], "Handle", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(table[0][1], "BlockName", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("格式错误：前两列必须为 Handle、BlockName。");

                Dictionary<string, ObjectId> selected = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                Document doc = CadHost.ActiveDocument;
                using (Transaction tr = doc.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in _blockIds)
                    {
                        BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (br != null) selected[br.Handle.ToString()] = id;
                    }
                    tr.Commit();
                }

                Dictionary<string, int> columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int c = 2; c < table[0].Count; c++)
                    if (table[0][c].Length > 0) columns[table[0][c]] = c;

                _xlsxChanges.Clear();
                int invalid = 0;
                using (Transaction tr = doc.TransactionManager.StartTransaction())
                {
                    for (int r = 1; r < table.Count; r++)
                    {
                        List<string> line = table[r];
                        if (line.Count < 2 || line[0].Length == 0) continue;
                        ObjectId id;
                        if (!selected.TryGetValue(line[0], out id) ||
                            !string.Equals(line[1], _blockName, StringComparison.OrdinalIgnoreCase))
                        { invalid++; continue; }

                        BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                        Dictionary<string, AttributeReference> attrs = ReadAttributes(br, tr, OpenMode.ForRead);
                        foreach (KeyValuePair<string, int> column in columns)
                        {
                            if (column.Value >= line.Count || line[column.Value].Length == 0) continue;
                            AttributeReference ar;
                            if (!attrs.TryGetValue(column.Key, out ar)) { invalid++; continue; }
                            string value = line[column.Value] == "[[CLEAR]]" ? "" : line[column.Value];
                            if (string.Equals(ar.TextString, value, StringComparison.Ordinal)) continue;
                            _xlsxChanges.Add(new XlsxChange {
                                Id = id, Handle = line[0], Tag = column.Key,
                                OldValue = ar.TextString, NewValue = value
                            });
                        }
                    }
                    tr.Commit();
                }

                _xlsxPreview.Columns.Clear();
                _xlsxPreview.Rows.Clear();
                _xlsxPreview.Columns.Add("Handle", "Handle");
                _xlsxPreview.Columns.Add("Tag", "属性 TAG");
                _xlsxPreview.Columns.Add("Old", "原值");
                _xlsxPreview.Columns.Add("New", "新值");
                foreach (XlsxChange change in _xlsxChanges)
                    _xlsxPreview.Rows.Add(change.Handle, change.Tag, change.OldValue, change.NewValue);
                MessageBox.Show("预览完成：待修改 " + _xlsxChanges.Count + " 处；无效块或缺失属性 " +
                    invalid + " 处。检查预览后再确认写入。", "Excel 导入预览");
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("Excel 导入失败：" + ex.Message, "错误");
            }
        }

        private void ApplyXlsx()
        {
            if (_xlsxChanges.Count == 0)
            {
                MessageBox.Show("没有待写入的修改。", "提示");
                return;
            }
            if (MessageBox.Show("确认写入 " + _xlsxChanges.Count + " 处属性修改？",
                "确认写入", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            Document doc = CadHost.ActiveDocument;
            int changed = 0;
            using (DocumentLock dl = doc.LockDocument())
            using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                foreach (XlsxChange change in _xlsxChanges)
                {
                    BlockReference br = tr.GetObject(change.Id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    Dictionary<string, AttributeReference> attrs = ReadAttributes(br, tr, OpenMode.ForWrite);
                    AttributeReference ar;
                    if (!attrs.TryGetValue(change.Tag, out ar)) continue;
                    // Prevent stale previews from overwriting edits made after import.
                    if (!string.Equals(ar.TextString, change.OldValue, StringComparison.Ordinal))
                        continue;
                    if (!string.Equals(ar.TextString, change.NewValue, StringComparison.Ordinal))
                    {
                        ar.TextString = change.NewValue;
                        changed++;
                    }
                }
                tr.Commit();
            }
            _xlsxChanges.Clear();
            MessageBox.Show("写入完成，实际修改 " + changed + " 处。", "Excel 导入");
        }
    }
}