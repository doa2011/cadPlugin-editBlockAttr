using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Drawing;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCADBlockAttrPlugin
{
    /// <summary>
    /// 动态块参数批量工作区。每行一个块参照，每列一个动态参数；
    /// 表格支持直接编辑、XLSX 导入预览和导出。
    /// </summary>
    internal sealed class DynamicBlockTab
    {
        private sealed class ParameterInfo
        {
            internal string Name;
            internal string TypeName;
            internal string Units;
            internal bool ReadOnly;
            internal object[] Allowed;
        }

        private readonly ObjectId[] _blockIds;
        private readonly string _blockName;
        private readonly List<ParameterInfo> _parameters = new List<ParameterInfo>();
        private readonly Dictionary<string, string> _original =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ObjectId> _ids =
            new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
        private DataGridView _grid;
        private DataGridView _attributeGrid;
        private Label _status;
        private readonly List<string> _attributeTags = new List<string>();
        private RadioButton _sortLeftRight;
        private RadioButton _sortTopBottom;
        private readonly Dictionary<string, int> _columns =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private DynamicBlockTab(ObjectId[] blockIds, string blockName)
        {
            _blockIds = blockIds ?? new ObjectId[0];
            _blockName = blockName ?? "";
        }

        internal static void Build(Control page, ObjectId[] blockIds, string blockName)
        {
            new DynamicBlockTab(blockIds, blockName).Initialize(page);
        }

        private void Initialize(Control page)
        {
            Panel toolbar = new Panel();
            toolbar.Dock = DockStyle.Top;
            toolbar.Height = 58;

            Button export = new Button();
            export.Text = "导出 XLSX";
            export.SetBounds(10, 8, 100, 30);
            export.Click += delegate { ExportXlsx(); };

            Button import = new Button();
            import.Text = "导入并预览";
            import.SetBounds(118, 8, 110, 30);
            import.Click += delegate { ImportXlsx(); };

            Button apply = new Button();
            apply.Text = "确认写入 CAD";
            apply.SetBounds(236, 8, 120, 30);
            apply.Click += delegate { ApplyChanges(); };

            _status = new Label();
            _status.AutoEllipsis = true;
            _status.SetBounds(570, 8, 600, 34);
            _status.Text = "每行一个动态块，每列一个动态参数；空白单元格不修改。";

            _sortLeftRight = new RadioButton();
            _sortLeftRight.Text = "从左至右";
            _sortLeftRight.SetBounds(375, 14, 90, 24);
            _sortLeftRight.Checked = true;

            _sortTopBottom = new RadioButton();
            _sortTopBottom.Text = "从上到下";
            _sortTopBottom.SetBounds(465, 14, 90, 24);

            toolbar.Controls.Add(export);
            toolbar.Controls.Add(import);
            toolbar.Controls.Add(apply);
            toolbar.Controls.Add(_sortLeftRight);
            toolbar.Controls.Add(_sortTopBottom);
            toolbar.Controls.Add(_status);

            _grid = new DataGridView();
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToOrderColumns = true;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _grid.MultiSelect = true;
            _grid.ScrollBars = ScrollBars.Both;
            _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            _grid.CellEndEdit += delegate { RefreshHighlights(); };

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.SplitterDistance = 390;
            split.Panel1MinSize = 160;
            split.Panel2MinSize = 150;
            split.Panel1.Controls.Add(_grid);

            Panel attributeToolbar = new Panel();
            attributeToolbar.Dock = DockStyle.Top;
            attributeToolbar.Height = 38;
            Label attributeTitle = new Label();
            attributeTitle.Text = "块属性预览（只读）";
            attributeTitle.SetBounds(10, 10, 180, 22);
            Button exportAttributes = new Button();
            exportAttributes.Text = "导出块属性 XLSX";
            exportAttributes.SetBounds(190, 4, 140, 28);
            exportAttributes.Click += delegate { ExportAttributesXlsx(); };
            attributeToolbar.Controls.Add(attributeTitle);
            attributeToolbar.Controls.Add(exportAttributes);

            _attributeGrid = new DataGridView();
            _attributeGrid.Dock = DockStyle.Fill;
            _attributeGrid.ReadOnly = true;
            _attributeGrid.AllowUserToAddRows = false;
            _attributeGrid.AllowUserToDeleteRows = false;
            _attributeGrid.AllowUserToOrderColumns = false;
            _attributeGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
            _attributeGrid.RowHeadersVisible = false;
            _attributeGrid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _attributeGrid.MultiSelect = true;
            _attributeGrid.ScrollBars = ScrollBars.Both;
            split.Panel2.Controls.Add(_attributeGrid);
            split.Panel2.Controls.Add(attributeToolbar);

            page.Controls.Add(split);
            page.Controls.Add(toolbar);

            LoadParametersAndRows();

            _sortLeftRight.CheckedChanged += delegate
            {
                if (_sortLeftRight.Checked) SortGridRows();
            };
            _sortTopBottom.CheckedChanged += delegate
            {
                if (_sortTopBottom.Checked) SortGridRows();
            };
            SortGridRows();
            LoadAttributePreview();
        }

        private void LoadParametersAndRows()
        {
            Document doc = CadHost.ActiveDocument;
            if (doc == null)
            {
                _status.Text = "没有活动图纸。";
                return;
            }

            using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                BlockReference sample = null;
                foreach (ObjectId id in _blockIds)
                {
                    sample = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (sample != null && sample.IsDynamicBlock) break;
                    sample = null;
                }

                if (sample == null)
                {
                    tr.Commit();
                    _status.Text = "当前选择的块不是动态块。请选择动态块后重新执行 EditBlockAtt。";
                    _grid.Enabled = false;
                    return;
                }

                foreach (DynamicBlockReferenceProperty prop in sample.DynamicBlockReferencePropertyCollection)
                {
                    if (prop == null || String.IsNullOrEmpty(prop.PropertyName)) continue;
                    // 统一过滤 AutoCAD 动态块内部 Origin 辅助参数，兼容空格/下划线/连字符。
                    if (IsOriginPropertyName(prop.PropertyName)) continue;
                    ParameterInfo info = new ParameterInfo();
                    info.Name = prop.PropertyName;
                    object value = prop.Value;
                    info.TypeName = value == null ? "未知" : value.GetType().Name;
                    info.Units = prop.UnitsType.ToString();
                    info.ReadOnly = prop.ReadOnly;
                    try { info.Allowed = prop.GetAllowedValues(); }
                    catch { info.Allowed = null; }
                    _parameters.Add(info);
                }

                foreach (ObjectId id in _blockIds)
                {
                    BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null || !br.IsDynamicBlock) continue;
                    string handle = br.Handle.ToString();
                    _ids[handle] = id;
                }
                tr.Commit();
            }

            if (_parameters.Count == 0)
            {
                _status.Text = "没有读取到动态参数。";
                _grid.Enabled = false;
                return;
            }

            DataGridViewTextBoxColumn handleCol = new DataGridViewTextBoxColumn();
            handleCol.Name = "Handle";
            handleCol.HeaderText = "Handle";
            handleCol.ReadOnly = true;
            handleCol.Frozen = true;
            handleCol.Width = 95;
            _grid.Columns.Add(handleCol);

            DataGridViewTextBoxColumn nameCol = new DataGridViewTextBoxColumn();
            nameCol.Name = "BlockName";
            nameCol.HeaderText = "块名";
            nameCol.ReadOnly = true;
            nameCol.Frozen = true;
            nameCol.Width = 150;
            _grid.Columns.Add(nameCol);

            for (int i = 0; i < _parameters.Count; i++)
            {
                ParameterInfo info = _parameters[i];
                DataGridViewTextBoxColumn col = new DataGridViewTextBoxColumn();
                col.Name = "P" + i.ToString(CultureInfo.InvariantCulture);
                col.HeaderText = info.Name;
                col.Width = 135;
                col.ReadOnly = info.ReadOnly;
                col.SortMode = DataGridViewColumnSortMode.NotSortable;
                string allowed = info.Allowed == null ? "" :
                    String.Join(", ", info.Allowed.Select(delegate(object v)
                    {
                        return Convert.ToString(v, CultureInfo.InvariantCulture);
                    }).ToArray());
                col.ToolTipText = "类型：" + info.TypeName + "；单位：" + info.Units +
                    "；只读：" + (info.ReadOnly ? "是" : "否") +
                    (allowed.Length == 0 ? "" : "；允许值：" + allowed);
                _grid.Columns.Add(col);
                _columns[info.Name] = i + 2;
            }

            using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                foreach (KeyValuePair<string, ObjectId> item in _ids)
                {
                    BlockReference br = tr.GetObject(item.Value, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    Dictionary<string, DynamicBlockReferenceProperty> props = ReadProperties(br);
                    int rowIndex = _grid.Rows.Add();
                    DataGridViewRow row = _grid.Rows[rowIndex];
                    row.Cells[0].Value = item.Key;
                    row.Cells[1].Value = _blockName;
                    foreach (ParameterInfo info in _parameters)
                    {
                        DynamicBlockReferenceProperty prop;
                        string value = "";
                        if (props.TryGetValue(info.Name, out prop))
                            value = FormatValue(prop.Value);
                        row.Cells[_columns[info.Name]].Value = value;
                        _original[MakeKey(item.Key, info.Name)] = value;
                    }
                }
                tr.Commit();
            }

            _status.Text = "动态块：" + _blockName + "；动态块数量：" + _grid.Rows.Count +
                "；参数列：" + _parameters.Count +
                "。可直接编辑单元格，或导入 Excel 后确认写入。";
            RefreshHighlights();
        }

        /// <summary>
        /// 按块参照插入点排序动态块表格。默认从左至右；可切换为从上到下。
        /// 同一列/行内分别按 Y 降序/X 升序排列。
        /// </summary>
        private void SortGridRows()
        {
            if (_grid == null || _grid.Rows.Count < 2) return;

            Document doc = CadHost.ActiveDocument;
            if (doc == null) return;

            Dictionary<string, Point3d> positions =
                new Dictionary<string, Point3d>(StringComparer.OrdinalIgnoreCase);

            using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                foreach (KeyValuePair<string, ObjectId> item in _ids)
                {
                    BlockReference br = tr.GetObject(item.Value, OpenMode.ForRead) as BlockReference;
                    if (br != null) positions[item.Key] = br.Position;
                }
                tr.Commit();
            }

            List<KeyValuePair<Point3d, object[]>> ordered =
                new List<KeyValuePair<Point3d, object[]>>();

            foreach (DataGridViewRow row in _grid.Rows)
            {
                string handle = Convert.ToString(row.Cells[0].Value) ?? "";
                Point3d position;
                if (!positions.TryGetValue(handle, out position))
                    position = Point3d.Origin;

                object[] values = new object[_grid.Columns.Count];
                for (int c = 0; c < _grid.Columns.Count; c++)
                    values[c] = row.Cells[c].Value;

                ordered.Add(new KeyValuePair<Point3d, object[]>(position, values));
            }

            if (_sortTopBottom != null && _sortTopBottom.Checked)
            {
                ordered = ordered
                    .OrderByDescending(item => item.Key.Y)
                    .ThenBy(item => item.Key.X)
                    .ToList();
            }
            else
            {
                ordered = ordered
                    .OrderBy(item => item.Key.X)
                    .ThenByDescending(item => item.Key.Y)
                    .ToList();
            }

            _grid.Rows.Clear();
            foreach (KeyValuePair<Point3d, object[]> item in ordered)
                _grid.Rows.Add(item.Value);
            RefreshHighlights();
            LoadAttributePreview();
        }

        private static bool IsOriginPropertyName(string propertyName)
        {
            if (String.IsNullOrEmpty(propertyName)) return false;
            string normalized = new string(propertyName.Trim()
                .Where(delegate(char ch) { return !Char.IsWhiteSpace(ch) && ch != '_' && ch != '-'; })
                .ToArray());
            return String.Equals(normalized, "Origin", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, DynamicBlockReferenceProperty> ReadProperties(BlockReference br)
        {
            Dictionary<string, DynamicBlockReferenceProperty> result =
                new Dictionary<string, DynamicBlockReferenceProperty>(StringComparer.OrdinalIgnoreCase);
            foreach (DynamicBlockReferenceProperty prop in br.DynamicBlockReferencePropertyCollection)
                if (prop != null && !String.IsNullOrEmpty(prop.PropertyName) &&
                    !IsOriginPropertyName(prop.PropertyName) && !result.ContainsKey(prop.PropertyName))
                    result.Add(prop.PropertyName, prop);
            return result;
        }

        private static string FormatValue(object value)
        {
            if (value == null) return "";
            if (value is double) return ((double)value).ToString("G", CultureInfo.InvariantCulture);
            if (value is float) return ((float)value).ToString("G", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string MakeKey(string handle, string property)
        {
            return handle + "|" + property;
        }

        private void RefreshHighlights()
        {
            if (_grid == null) return;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                string handle = Convert.ToString(row.Cells[0].Value);
                foreach (ParameterInfo info in _parameters)
                {
                    int col = _columns[info.Name];
                    string key = MakeKey(handle, info.Name);
                    string current = Convert.ToString(row.Cells[col].Value) ?? "";
                    string original;
                    bool changed = _original.TryGetValue(key, out original) &&
                        !String.Equals(current, original, StringComparison.Ordinal);
                    row.Cells[col].Style.BackColor = changed ? Color.LightYellow : Color.White;
                }
            }
        }

        private void LoadAttributePreview()
        {
            if (_attributeGrid == null) return;
            Document doc = CadHost.ActiveDocument;
            if (doc == null) return;

            _attributeTags.Clear();
            using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in _blockIds)
                {
                    BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    foreach (ObjectId attId in br.AttributeCollection)
                    {
                        AttributeReference ar = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                        if (ar == null || String.IsNullOrEmpty(ar.Tag)) continue;
                        if (!_attributeTags.Contains(ar.Tag, StringComparer.OrdinalIgnoreCase))
                            _attributeTags.Add(ar.Tag);
                    }
                }
                tr.Commit();
            }

            _attributeGrid.Columns.Clear();
            _attributeGrid.Rows.Clear();
            _attributeGrid.Columns.Add("Handle", "Handle");
            _attributeGrid.Columns.Add("BlockName", "块名");
            foreach (string tag in _attributeTags)
            {
                DataGridViewTextBoxColumn col = new DataGridViewTextBoxColumn();
                col.Name = "A_" + _attributeGrid.Columns.Count.ToString(CultureInfo.InvariantCulture);
                col.HeaderText = tag;
                col.ReadOnly = true;
                col.SortMode = DataGridViewColumnSortMode.NotSortable;
                _attributeGrid.Columns.Add(col);
            }

            using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                // 以动态参数表当前的空间排序为准，确保上下两张表一一对应。
                foreach (DataGridViewRow dynamicRow in _grid.Rows)
                {
                    string handle = Convert.ToString(dynamicRow.Cells[0].Value) ?? "";
                    ObjectId id;
                    if (!_ids.TryGetValue(handle, out id)) continue;
                    BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;

                    Dictionary<string, string> values =
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectId attId in br.AttributeCollection)
                    {
                        AttributeReference ar = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                        if (ar != null && !String.IsNullOrEmpty(ar.Tag) && !values.ContainsKey(ar.Tag))
                            values.Add(ar.Tag, ar.TextString ?? "");
                    }

                    int rowIndex = _attributeGrid.Rows.Add();
                    DataGridViewRow row = _attributeGrid.Rows[rowIndex];
                    row.Cells[0].Value = handle;
                    row.Cells[1].Value = _blockName;
                    for (int i = 0; i < _attributeTags.Count; i++)
                    {
                        string value;
                        row.Cells[i + 2].Value = values.TryGetValue(_attributeTags[i], out value) ? value : "";
                    }
                }
                tr.Commit();
            }
        }

        private void ExportAttributesXlsx()
        {
            if (_attributeGrid == null || _attributeGrid.Columns.Count < 2)
            {
                MessageBox.Show("没有可导出的块属性。", "块属性导出");
                return;
            }

            SaveFileDialog dlg = new SaveFileDialog();
            dlg.Filter = "Excel 工作簿 (*.xlsx)|*.xlsx";
            dlg.FileName = "块属性预览.xlsx";
            if (dlg.ShowDialog() != DialogResult.OK) return;
            try
            {
                List<string> headers = new List<string>();
                foreach (DataGridViewColumn col in _attributeGrid.Columns)
                    headers.Add(col.HeaderText);
                List<IList<string>> rows = new List<IList<string>>();
                foreach (DataGridViewRow row in _attributeGrid.Rows)
                {
                    List<string> values = new List<string>();
                    foreach (DataGridViewCell cell in row.Cells)
                        values.Add(Convert.ToString(cell.Value) ?? "");
                    rows.Add(values);
                }
                XlsxLite.Write(dlg.FileName, headers, rows);
                MessageBox.Show("已导出 " + rows.Count + " 个块的属性预览。", "块属性导出");
            }
            catch (Exception ex)
            {
                MessageBox.Show("块属性导出失败：" + ex.Message, "块属性导出");
            }
        }

        private void ExportXlsx()
        {
            SaveFileDialog dlg = new SaveFileDialog();
            dlg.Filter = "Excel 工作簿 (*.xlsx)|*.xlsx";
            dlg.FileName = "动态块参数.xlsx";
            if (dlg.ShowDialog() != DialogResult.OK) return;
            try
            {
                List<string> headers = new List<string>();
                headers.Add("Handle");
                headers.Add("块名");
                foreach (ParameterInfo info in _parameters) headers.Add(info.Name);
                List<IList<string>> rows = new List<IList<string>>();
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    List<string> values = new List<string>();
                    for (int c = 0; c < _grid.Columns.Count; c++)
                        values.Add(Convert.ToString(row.Cells[c].Value) ?? "");
                    rows.Add(values);
                }
                XlsxLite.Write(dlg.FileName, headers, rows);
                MessageBox.Show("已导出 " + rows.Count + " 个动态块、" +
                    _parameters.Count + " 个参数列。", "动态块参数导出");
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出失败：" + ex.Message, "动态块参数");
            }
        }

        private void ImportXlsx()
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "Excel 工作簿 (*.xlsx)|*.xlsx";
            if (dlg.ShowDialog() != DialogResult.OK) return;
            try
            {
                List<List<string>> table = XlsxLite.Read(dlg.FileName);
                if (table.Count < 2 || table[0].Count < 2 ||
                    !String.Equals(table[0][0], "Handle", StringComparison.OrdinalIgnoreCase) ||
                    !(String.Equals(table[0][1], "块名", StringComparison.OrdinalIgnoreCase) ||
                      String.Equals(table[0][1], "BlockName", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("格式错误：前两列必须为 Handle、块名。");

                Dictionary<string, int> sourceColumns =
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int c = 2; c < table[0].Count; c++)
                    if (!String.IsNullOrEmpty(table[0][c]) && !IsOriginPropertyName(table[0][c]))
                        sourceColumns[table[0][c]] = c;

                Dictionary<string, int> targetRows =
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (DataGridViewRow row in _grid.Rows)
                    targetRows[Convert.ToString(row.Cells[0].Value)] = row.Index;

                int updated = 0;
                int invalid = 0;
                for (int r = 1; r < table.Count; r++)
                {
                    List<string> line = table[r];
                    if (line.Count < 2 || String.IsNullOrEmpty(line[0])) continue;
                    int rowIndex;
                    if (!targetRows.TryGetValue(line[0], out rowIndex) ||
                        !String.Equals(line[1], _blockName, StringComparison.OrdinalIgnoreCase))
                    { invalid++; continue; }

                    foreach (ParameterInfo info in _parameters)
                    {
                        int sourceCol;
                        if (!sourceColumns.TryGetValue(info.Name, out sourceCol) ||
                            sourceCol >= line.Count || String.IsNullOrEmpty(line[sourceCol])) continue;
                        if (info.ReadOnly) { invalid++; continue; }
                        _grid.Rows[rowIndex].Cells[_columns[info.Name]].Value = line[sourceCol];
                        updated++;
                    }
                }
                RefreshHighlights();
                _status.Text = "导入预览：填入/更新 " + updated + " 个单元格；无效块或只读参数 " +
                    invalid + " 项。黄色单元格表示待修改，确认后写入 CAD。";
            }
            catch (Exception ex)
            {
                MessageBox.Show("导入失败：" + ex.Message, "动态块参数");
            }
        }

        private void ApplyChanges()
        {
            List<string> errors = new List<string>();
            int requested = 0;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                string handle = Convert.ToString(row.Cells[0].Value);
                foreach (ParameterInfo info in _parameters)
                {
                    string key = MakeKey(handle, info.Name);
                    string oldValue;
                    if (!_original.TryGetValue(key, out oldValue)) continue;
                    string newValue = Convert.ToString(row.Cells[_columns[info.Name]].Value) ?? "";
                    if (String.Equals(oldValue, newValue, StringComparison.Ordinal)) continue;
                    if (info.ReadOnly)
                    {
                        errors.Add(handle + " / " + info.Name + "：只读参数");
                        continue;
                    }
                    requested++;
                }
            }

            if (requested == 0)
            {
                MessageBox.Show(errors.Count == 0 ? "没有待写入的修改。" :
                    String.Join("\r\n", errors.ToArray()), "动态块参数");
                return;
            }
            if (MessageBox.Show("确认写入 " + requested + " 个动态参数？\r\n只读或无效项将跳过。",
                "确认写入", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            Document doc = CadHost.ActiveDocument;
            int changed = 0;
            using (DocumentLock dl = doc.LockDocument())
            using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    string handle = Convert.ToString(row.Cells[0].Value);
                    ObjectId id;
                    if (!_ids.TryGetValue(handle, out id)) continue;
                    BlockReference br = tr.GetObject(id, OpenMode.ForWrite) as BlockReference;
                    if (br == null || !br.IsDynamicBlock) continue;
                    Dictionary<string, DynamicBlockReferenceProperty> props = ReadProperties(br);

                    foreach (ParameterInfo info in _parameters)
                    {
                        string key = MakeKey(handle, info.Name);
                        string oldValue;
                        if (!_original.TryGetValue(key, out oldValue)) continue;
                        string newText = Convert.ToString(row.Cells[_columns[info.Name]].Value) ?? "";
                        if (String.Equals(oldValue, newText, StringComparison.Ordinal)) continue;

                        DynamicBlockReferenceProperty prop;
                        if (!props.TryGetValue(info.Name, out prop) || prop.ReadOnly)
                        {
                            errors.Add(handle + " / " + info.Name + "：参数不存在或只读");
                            continue;
                        }
                        if (!String.Equals(FormatValue(prop.Value), oldValue, StringComparison.Ordinal))
                        {
                            errors.Add(handle + " / " + info.Name + "：CAD 中的原值已变化，跳过以避免覆盖");
                            continue;
                        }
                        try
                        {
                            object converted = ConvertPropertyValue(newText, prop.Value, info);
                            prop.Value = converted;
                            changed++;
                        }
                        catch (Exception ex)
                        {
                            errors.Add(handle + " / " + info.Name + "：" + ex.Message);
                        }
                    }
                }
                tr.Commit();
            }

            using (Transaction tr = doc.TransactionManager.StartTransaction())
            {
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    string handle = Convert.ToString(row.Cells[0].Value);
                    ObjectId id;
                    if (!_ids.TryGetValue(handle, out id)) continue;
                    BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    Dictionary<string, DynamicBlockReferenceProperty> props = ReadProperties(br);
                    foreach (ParameterInfo info in _parameters)
                    {
                        DynamicBlockReferenceProperty prop;
                        if (props.TryGetValue(info.Name, out prop))
                        {
                            string value = FormatValue(prop.Value);
                            row.Cells[_columns[info.Name]].Value = value;
                            _original[MakeKey(handle, info.Name)] = value;
                        }
                    }
                }
                tr.Commit();
            }

            RefreshHighlights();
            _status.Text = "写入结束：成功修改 " + changed + " 项；异常/跳过 " + errors.Count + " 项。";
            if (errors.Count > 0)
                MessageBox.Show("成功修改：" + changed + " 项\r\n异常/跳过：" + errors.Count +
                    " 项\r\n\r\n" + String.Join("\r\n", errors.Take(20).ToArray()),
                    "动态块参数写入结果");
            else
                MessageBox.Show("成功修改 " + changed + " 个动态参数。", "动态块参数");
        }

        private static object ConvertPropertyValue(string text, object current, ParameterInfo info)
        {
            Type targetType = current == null ? typeof(string) : current.GetType();
            if (info.Allowed != null && info.Allowed.Length > 0)
            {
                foreach (object allowed in info.Allowed)
                    if (String.Equals(FormatValue(allowed), text, StringComparison.OrdinalIgnoreCase))
                        return allowed;
                throw new InvalidOperationException("输入值不在该参数允许值列表中。");
            }
            if (targetType == typeof(string)) return text;
            if (targetType == typeof(bool)) return Boolean.Parse(text);
            if (targetType.IsEnum) return Enum.Parse(targetType, text, true);
            return Convert.ChangeType(text, targetType, CultureInfo.InvariantCulture);
        }
    }
}
