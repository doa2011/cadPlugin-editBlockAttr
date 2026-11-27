using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;

namespace AutoCADBlockAttrPlugin
{
    /// <summary>在 AutoCAD 内处理未打开的 DWG：每个文件独立排序/计数，另存到输出目录。</summary>
    public static class BatchTitleBlockProcessor
    {
        public sealed class Options
        {
            public string BlockName = "";
            public List<Options> Rules = new List<Options>();
            public int Start = 1;
            public int Total = 0;
            public string SaveVersion = "2010";
            public string TitleTag = "";
            public string DrawingTag = "";
            public string PageTag = "";
            public string TotalTag = "";
            public bool LeftToRight = true;
            public bool AllowPartial = false;
        }

        public sealed class Result
        {
            public string Input;
            public string Output;
            public string DrawingNo;
            public string Title;
            public int Count;
            public int Changed;
            public int Missing;
            public string Status;
            public string Error;
        }

        private sealed class Item
        {
            public ObjectId Id;
            public double X;
            public double Y;
            public string Handle;
            public string Name;
        }

        public static string[] ReadTags(string file, string blockName)
        {
            HashSet<string> tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (Database db = new Database(false, true))
            {
                db.ReadDwgFile(file, FileShare.Read, true, "");
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (Item item in GetBlocks(db, tr, blockName))
                    {
                        BlockReference br = tr.GetObject(item.Id, OpenMode.ForRead) as BlockReference;
                        foreach (ObjectId id in br.AttributeCollection)
                        {
                            AttributeReference ar = tr.GetObject(id, OpenMode.ForRead) as AttributeReference;
                            if (ar != null && !String.IsNullOrEmpty(ar.Tag)) tags.Add(ar.Tag);
                        }
                    }
                    tr.Commit();
                }
            }
            return tags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>从首个 DWG 的顶层块属性自动推荐角标块及四项 TAG；无可靠映射则返回 null。</summary>
        public static Dictionary<string, HashSet<string>> Scan(string file)
        {

            using (Database db = new Database(false, true))
            {
                db.ReadDwgFile(file, FileShare.Read, true, "");
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                    Dictionary<string, HashSet<string>> candidates =
                        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectId btrId in bt)
                    {
                        BlockTableRecord space = tr.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord;
                        if (space == null || (!space.IsLayout && btrId != bt[BlockTableRecord.ModelSpace])) continue;
                        foreach (ObjectId id in space)
                        {
                            BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                            if (br == null) continue;
                            ObjectId effective = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                            BlockTableRecord definition = tr.GetObject(effective, OpenMode.ForRead) as BlockTableRecord;
                            string name = definition == null ? br.Name : definition.Name;
                            HashSet<string> tags;
                            if (!candidates.TryGetValue(name, out tags))
                            {
                                tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                candidates[name] = tags;
                            }
                            foreach (ObjectId aid in br.AttributeCollection)
                            {
                                AttributeReference ar = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                                if (ar != null && !String.IsNullOrEmpty(ar.Tag)) tags.Add(ar.Tag);
                            }
                        }
                    }
                    tr.Commit();
                    return candidates;
                }
            }
        }

        public static Options Guess(string name, HashSet<string> tags)
        {
            Options best = null;
            int bestScore = -1;
            foreach (KeyValuePair<string, HashSet<string>> pair in new Dictionary<string, HashSet<string>> {{name,tags}})
                    {
                        if (pair.Value.Count < 4) continue;
                        Options guess = new Options();
                        guess.BlockName = pair.Key;
                        int score = 0;
                        HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        string[] proposed = new string[4];
                        for (int kind = 3; kind >= 0; kind--)
                        {
                            int localScore = 0;
                            foreach (string tag in pair.Value)
                            {
                                if (used.Contains(tag)) continue;
                                int val = TagScore(tag, kind);
                                if (val > localScore)
                                {
                                    localScore = val;
                                    proposed[kind] = tag;
                                }
                            }
                            if (localScore > 0)
                            {
                                score += localScore;
                                used.Add(proposed[kind]);
                            }
                        }
                        if (used.Count != 4) continue;
                        if (pair.Key.IndexOf("图框") >= 0 || pair.Key.IndexOf("图签") >= 0 ||
                            pair.Key.IndexOf("角标") >= 0 || pair.Key.ToUpperInvariant().Contains("TITLE"))
                            score += 3;
                        if (score <= bestScore) continue;
                        guess.TitleTag = proposed[0];
                        guess.DrawingTag = proposed[1];
                        guess.PageTag = proposed[2];
                        guess.TotalTag = proposed[3];
                        best = guess;
                        bestScore = score;
                    }
            return best;
        }

        private static int TagScore(string tag, int kind)
        {
            string t = tag.ToUpperInvariant();
            bool total = t.Contains("TOTAL") || t.Contains("COUNT") ||
                t.Contains("SHEETS") || t.Contains("共");
            if (kind == 3) return total ? 5 : 0;
            if (kind == 2)
                return !total && (t.Contains("PAGE") || t.Contains("SHEET") ||
                    t.Contains("页")) ? 4 : 0;
            if (kind == 1)
                return t.Contains("DWG") || t.Contains("DRAWING") || t.Contains("图号") ||
                    t == "NO" || t.Contains("图纸编号") ? 4 : 0;
            return t.Contains("TITLE") || t.Contains("NAME") || t.Contains("图名") ? 4 : 0;
        }

        public static Result Preview(string file, Options options)
        {
            return Process(file, null, options);
        }

        public static Result Write(string file, string output, Options options)
        {
            if (!String.Equals(Path.GetFullPath(file), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
                return Process(file, output, options);
            string temp = Path.Combine(Path.GetDirectoryName(file), Guid.NewGuid().ToString("N") + ".dwg");
            Result result = Process(file, temp, options);
            result.Output = file;
            try {
                if (result.Status == "成功") File.Replace(temp, file, null);
            } catch (System.Exception ex) { result.Status = "失败"; result.Error = ex.Message; result.Changed = 0; }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (System.Exception) {} }
            return result;
        }

        private static Result Process(string file, string output, Options options)
        {
            Result result = new Result();
            result.Input = file;
            result.Output = output ?? "";
            result.Status = "待写入";
            try
            {
                if (!File.Exists(file)) throw new FileNotFoundException("DWG 不存在", file);
                if (options.Rules.Count == 0)
                    throw new InvalidOperationException("必须指定角标块名称，避免修改其他块。");
                foreach (Options rule in options.Rules)
                {
                string[] tags = { rule.TitleTag, rule.DrawingTag, rule.PageTag, rule.TotalTag };
                if (tags.Any(t => String.IsNullOrEmpty(t)) ||
                    tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4)
                    throw new InvalidOperationException("四个属性 TAG 必须填写且互不相同。");
                }

                SplitDrawingFileName(file, out result.DrawingNo, out result.Title);
                if (String.IsNullOrEmpty(result.DrawingNo) || String.IsNullOrEmpty(result.Title))
                    throw new InvalidOperationException("DWG 文件名须为：图号 空格 图名；为避免清空图名，已跳过。");
                using (Database db = new Database(false, true))
                {
                    db.ReadDwgFile(file, FileShare.Read, true, "");
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        List<Item> items = GetBlocks(db, tr, null).Where(x => options.Rules.Any(rule => String.Equals(rule.BlockName,x.Name,StringComparison.OrdinalIgnoreCase))).ToList();
                        if (options.LeftToRight)
                            items = items.OrderBy(x => x.X).ThenByDescending(x => x.Y)
                                .ThenBy(x => x.Handle, StringComparer.Ordinal).ToList();
                        else
                            items = items.OrderByDescending(x => x.Y).ThenBy(x => x.X)
                                .ThenBy(x => x.Handle, StringComparer.Ordinal).ToList();
                        result.Count = items.Count;
                        if (items.Count == 0)
                            throw new InvalidOperationException("文件中找不到指定的角标块：" + options.BlockName);
                        for (int i = 0; i < items.Count; i++)
                        {
                            Options rule = options.Rules.First(x => String.Equals(x.BlockName,items[i].Name,StringComparison.OrdinalIgnoreCase));
                            string[] tags = { rule.TitleTag, rule.DrawingTag, rule.PageTag, rule.TotalTag };
                            BlockReference br = tr.GetObject(items[i].Id, OpenMode.ForRead) as BlockReference;
                            Dictionary<string, AttributeReference> found =
                                new Dictionary<string, AttributeReference>(StringComparer.OrdinalIgnoreCase);
                            foreach (ObjectId aid in br.AttributeCollection)
                            {
                                AttributeReference ar = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                                if (ar != null && !String.IsNullOrEmpty(ar.Tag) && !found.ContainsKey(ar.Tag))
                                    found.Add(ar.Tag, ar);
                            }
                            string[] values = { result.Title, result.DrawingNo,
                                (i + options.Start).ToString(), (options.Total > 0 ? options.Total : items.Count).ToString() };
                            for (int k = 0; k < tags.Length; k++)
                            {
                                AttributeReference ar;
                                if (!found.TryGetValue(tags[k], out ar))
                                {
                                    result.Missing++;
                                    continue;
                                }
                                if (!String.Equals(ar.TextString, values[k], StringComparison.Ordinal))
                                {
                                    result.Changed++;
                                    if (output != null)
                                    {
                                        ar.UpgradeOpen();
                                        ar.TextString = values[k];
                                    }
                                }
                            }
                        }
                        if (result.Missing > 0 && !options.AllowPartial)
                            throw new InvalidOperationException("有 " + result.Missing + " 个目标属性缺失；已阻止部分写入。");
                        tr.Commit();
                    }
                    if (output != null)
                    {
                        string dir = Path.GetDirectoryName(output);
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        if (File.Exists(output))
                            throw new IOException("输出文件已存在，避免覆盖：" + output);
                        DwgVersion version = (DwgVersion)Enum.Parse(typeof(DwgVersion), options.SaveVersion == "2018" ? "AC1032" : options.SaveVersion == "2013" ? "AC1027" : "AC1024");
                        db.SaveAs(output, version);
                        result.Status = "成功";
                    }
                    else result.Status = "可处理";
                }
            }
            catch (System.Exception ex)
            {
                result.Status = "失败";
                result.Error = ex.Message;
                result.Changed = 0;
            }
            return result;
        }

        private static List<Item> GetBlocks(Database db, Transaction tr, string name)
        {
            List<Item> result = new List<Item>();
            BlockTable bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            foreach (ObjectId btrId in bt)
            {
                BlockTableRecord space = tr.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord;
                if (space == null || (!space.IsLayout && btrId != bt[BlockTableRecord.ModelSpace])) continue;
                foreach (ObjectId id in space)
                {
                    BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    ObjectId effectiveId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                    BlockTableRecord definition = tr.GetObject(effectiveId, OpenMode.ForRead) as BlockTableRecord;
                    string actual = definition == null ? br.Name : definition.Name;
                    if (name != null && !String.Equals(actual, name, StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(new Item { Id = id, Name = actual, X = br.Position.X, Y = br.Position.Y, Handle = br.Handle.ToString() });
                }
            }
            return result;
        }

        public static void SplitDrawingFileName(string file, out string number, out string title)
        {
            string name = Path.GetFileNameWithoutExtension(file).Trim();
            int i = name.IndexOf(' ');
            if (i < 0) { number = name; title = ""; }
            else { number = name.Substring(0, i).Trim(); title = name.Substring(i + 1).Trim(); }
        }
    }

    public sealed class BatchTitleBlockForm : Form
    {
        private readonly DataGridView grid = new DataGridView();
        private readonly DataGridView rules = new DataGridView();
        
        private readonly RadioButton left = new RadioButton();
        private readonly RadioButton top = new RadioButton();
        private readonly TextBox output = new TextBox();
        private readonly ComboBox saveVersion = new ComboBox();
        private readonly Label status = new Label();
        private readonly List<string> files = new List<string>();
        private readonly CheckBox partial = new CheckBox();
        private string previewSignature = "";


        public BatchTitleBlockForm()
        {
            Text = "批量角标修改 - AutoCAD DWG";
            Width = 1120; Height = 740; MinimumSize = new Size(900, 630);
            StartPosition = FormStartPosition.CenterParent;
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill; layout.ColumnCount = 1;
            layout.RowCount = 4;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 300));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            Controls.Add(layout);

            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Fill; bar.WrapContents = false; bar.AutoScroll = true;
            Button pick = new Button(); pick.Text = "选择多个 DWG"; pick.Width = 125; pick.Click += PickFiles;
            Button detect = new Button(); detect.Text = "预读全部块"; detect.Width = 125; detect.Click += DetectFirstFile;
            Button inspect = new Button(); inspect.Text = "读取预览"; inspect.Width = 100; inspect.Click += PreviewFiles;
            Button write = new Button(); write.Text = "确认批量写入"; write.Width = 135; write.Click += WriteFiles;
            Button remove = new Button { Text = "移除选中文件", Width = 125 };
            remove.Click += RemoveFiles;
            Button clear = new Button { Text = "清空列表", Width = 100 };
            clear.Click += delegate { if (files.Count > 0 && MessageBox.Show("清空待处理列表？不会删除原始文件。", "确认", MessageBoxButtons.YesNo) == DialogResult.Yes) { files.Clear(); RefreshFileList(); } };
            bar.Controls.Add(pick); bar.Controls.Add(remove); bar.Controls.Add(clear); bar.Controls.Add(detect); bar.Controls.Add(inspect); bar.Controls.Add(write);
            layout.Controls.Add(bar, 0, 0);

            FlowLayoutPanel settings = new FlowLayoutPanel { Dock = DockStyle.Fill };
            left.Text = "从左到右"; left.Checked = true;
            top.Text = "从上到下"; partial.Text = "允许缺失属性";
            settings.Controls.Add(left); settings.Controls.Add(top); settings.Controls.Add(partial);
            Button folder = new Button { Text = "输出文件夹", Width = 100 };
            folder.Click += PickOutput; settings.Controls.Add(folder);
            output.Width = 180; settings.Controls.Add(output);
            saveVersion.DropDownStyle = ComboBoxStyle.DropDownList; saveVersion.Width = 100;
            foreach (string year in new string[] {"2010","2013","2018"})
            {
                string version = year == "2018" ? "AC1032" : year == "2013" ? "AC1027" : "AC1024";
                if (Enum.IsDefined(typeof(DwgVersion),version) && Convert.ToInt32(Enum.Parse(typeof(DwgVersion),version)) <= (int)DwgVersion.Current) saveVersion.Items.Add(year);
            }
            saveVersion.SelectedIndex = 0;
            settings.Controls.Add(new Label { Text = "保存版本", AutoSize = true }); settings.Controls.Add(saveVersion);
            Button clearOutput = new Button { Text = "清空输出", Width = 80 }; clearOutput.Click += delegate { output.Clear(); }; settings.Controls.Add(clearOutput);
            rules.Dock = DockStyle.Fill; rules.AllowUserToAddRows = false;
            rules.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            rules.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = "启用" });
            rules.Columns.Add("Block", "块名"); rules.Columns[1].ReadOnly = true;
            rules.Columns.Add("Tags", "实际 TAG"); rules.Columns[2].ReadOnly = true;
            foreach (string label in new string[] {"图名","图号","第几页","共几页"}) rules.Columns.Add(label,label);
            TableLayoutPanel upper = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            upper.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
            upper.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            upper.Controls.Add(settings,0,0); upper.Controls.Add(rules,0,1);
            layout.Controls.Add(upper,0,1);

            grid.Dock = DockStyle.Fill; grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false; grid.ReadOnly = true;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = true;
            grid.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Delete) { RemoveFiles(sender,e); e.Handled = true; e.SuppressKeyPress = true; } };
            grid.Columns.Add("File", "DWG 文件");
            grid.Columns.Add("Drawing", "图号");
            grid.Columns.Add("Title", "图名");
            grid.Columns.Add("Count", "页数");
            grid.Columns.Add("Changed", "待修改属性");
            grid.Columns.Add("Missing", "缺失属性");
            grid.Columns.Add("Status", "状态");
            grid.Columns.Add("Details", "提示");
            foreach (DataGridViewColumn column in grid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            layout.Controls.Add(grid, 0, 2);
            status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleLeft;
            status.Text = "先选择 DWG，指定块名与四个实际 TAG，读取预览后再确认写入。输出目录为空时覆盖原图，执行前确认。";
            layout.Controls.Add(status, 0, 3);
        }

        private void PickFiles(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "AutoCAD DWG (*.dwg)|*.dwg"; dlg.Multiselect = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                foreach (string file in dlg.FileNames)
                    if (!files.Contains(file, StringComparer.OrdinalIgnoreCase)) files.Add(file);
                RefreshFileList();
            }
        }

        private void RemoveFiles(object sender, EventArgs e)
        {
            List<int> indices = new List<int>();
            foreach (DataGridViewRow row in grid.SelectedRows)
                if (row.Index >= 0 && row.Index < files.Count) indices.Add(row.Index);
            if (indices.Count == 0) return;
            foreach (int index in indices.Distinct().OrderByDescending(x => x)) files.RemoveAt(index);
            RefreshFileList();
        }

        private void RefreshFileList()
        {
            grid.Rows.Clear();
            foreach (string file in files)
            {
                int index = grid.Rows.Add(Path.GetFileName(file));
                grid.Rows[index].Cells[0].ToolTipText = file;
            }
            previewSignature = "";
            if (files.Count == 0) { rules.Rows.Clear(); status.Text = "未选择文件。"; return; }
            DetectFirstFile(this, EventArgs.Empty);
        }

        private void DetectFirstFile(object sender, EventArgs e)
        {
            if (files.Count == 0) { MessageBox.Show("请先选择 DWG 文件。"); return; }
            Dictionary<string, object[]> previous = new Dictionary<string, object[]>(StringComparer.OrdinalIgnoreCase);
            rules.EndEdit();
            foreach (DataGridViewRow row in rules.Rows) { object[] values = new object[7]; for (int i=0;i<7;i++) values[i]=row.Cells[i].Value; previous[Convert.ToString(values[1])] = values; }
            rules.Rows.Clear(); previewSignature = "";
            Dictionary<string, HashSet<string>> all = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            List<string> errors = new List<string>();
            foreach (string file in files)
            {
                try {
                    foreach (KeyValuePair<string, HashSet<string>> pair in BatchTitleBlockProcessor.Scan(file))
                    {
                        if (!all.ContainsKey(pair.Key)) all[pair.Key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        all[pair.Key].UnionWith(pair.Value);
                    }
                } catch (System.Exception ex) { errors.Add(Path.GetFileName(file)+": "+ex.Message); }
            }
            foreach (KeyValuePair<string, HashSet<string>> pair in all.OrderBy(x=>x.Key))
            {
                if (pair.Value.Count == 0) continue;
                BatchTitleBlockProcessor.Options guess = BatchTitleBlockProcessor.Guess(pair.Key,pair.Value);
                object[] old;
                if (previous.TryGetValue(pair.Key,out old)) { old[2]=String.Join(", ",pair.Value.ToArray()); rules.Rows.Add(old); continue; }
                rules.Rows.Add(false,pair.Key,String.Join(", ",pair.Value.ToArray()),
                    guess == null ? "" : guess.TitleTag, guess == null ? "" : guess.DrawingTag,
                    guess == null ? "" : guess.PageTag, guess == null ? "" : guess.TotalTag);
            }
            status.Text = "预读完成，请勾选角标并确认 TAG。";
            if (errors.Count > 0) MessageBox.Show(String.Join("\r\n",errors.ToArray()));
        }

        private void PickOutput(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) output.Text = dlg.SelectedPath;
            }
        }

        private BatchTitleBlockProcessor.Options ReadOptions()
        {
            BatchTitleBlockProcessor.Options options = new BatchTitleBlockProcessor.Options();
            rules.EndEdit();
            foreach (DataGridViewRow row in rules.Rows)
            {
                if (!Object.Equals(row.Cells[0].Value,true)) continue;
                BatchTitleBlockProcessor.Options rule = new BatchTitleBlockProcessor.Options();
                rule.BlockName = Convert.ToString(row.Cells[1].Value);
                rule.TitleTag = Convert.ToString(row.Cells[3].Value).Trim();
                rule.DrawingTag = Convert.ToString(row.Cells[4].Value).Trim();
                rule.PageTag = Convert.ToString(row.Cells[5].Value).Trim();
                rule.TotalTag = Convert.ToString(row.Cells[6].Value).Trim();
                options.Rules.Add(rule);
            }
            options.SaveVersion = Convert.ToString(saveVersion.SelectedItem);
            options.LeftToRight = left.Checked;
            options.AllowPartial = partial.Checked;
            return options;
        }

        private string CurrentSignature()
        {
            BatchTitleBlockProcessor.Options options = ReadOptions();
            return String.Join("|", files.Select(f=>f+":"+File.GetLastWriteTimeUtc(f).Ticks).ToArray()) + "|" +
                String.Join("|", options.Rules.Select(r=>r.BlockName+":"+r.TitleTag+":"+r.DrawingTag+":"+r.PageTag+":"+r.TotalTag).ToArray()) +
                options.LeftToRight.ToString()+options.AllowPartial.ToString()+options.SaveVersion;
        }

        private void PreviewFiles(object sender, EventArgs e)
        {
            if (files.Count == 0) { MessageBox.Show("请先选择 DWG 文件。"); return; }
            BatchTitleBlockProcessor.Options options = ReadOptions();
            grid.Rows.Clear();
            int ok = 0;
            foreach (string file in files)
            {
                BatchTitleBlockProcessor.Result r = BatchTitleBlockProcessor.Preview(file, options);
                grid.Rows.Add(Path.GetFileName(file), r.DrawingNo, r.Title, r.Count,
                    r.Changed, r.Missing, r.Status, r.Error);
                if (r.Status == "可处理") ok++;
            }
            previewSignature = CurrentSignature();
            status.Text = "预览完成：" + ok + "/" + files.Count + " 个文件可以处理。";
        }

        private void WriteFiles(object sender, EventArgs e)
        {
            if (files.Count == 0) { MessageBox.Show("请先选择 DWG 文件。"); return; }
            if (previewSignature != CurrentSignature())
            {
                MessageBox.Show("文件、角标块名、TAG 或排序设置发生变化；请先读取最新预览再确认写入。");
                return;
            }
            bool overwrite = String.IsNullOrEmpty(output.Text.Trim());
            string dir = overwrite ? "" : Path.GetFullPath(output.Text.Trim());
            if (overwrite && MessageBox.Show("输出文件夹为空，将覆盖原始 DWG。确认继续？", "覆盖保存警告",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            if (!overwrite && files.Any(f => String.Equals(Path.GetFullPath(f),Path.Combine(dir,Path.GetFileName(f)),StringComparison.OrdinalIgnoreCase)))
            { MessageBox.Show("输出目录包含原始文件，请选择其他目录或清空输出目录后确认覆盖。"); return; }
            BatchTitleBlockProcessor.Options options = ReadOptions();
            grid.Rows.Clear();
            int ok = 0;
            foreach (string file in files)
            {
                string dest = overwrite ? file : Path.Combine(dir, Path.GetFileName(file));
                BatchTitleBlockProcessor.Result r = BatchTitleBlockProcessor.Write(file, dest, options);
                grid.Rows.Add(Path.GetFileName(file), r.DrawingNo, r.Title, r.Count,
                    r.Changed, r.Missing, r.Status, r.Error);
                if (r.Status == "成功") ok++;
                grid.Refresh();
            }
            status.Text = "批量处理完成：" + ok + "/" + files.Count + " 个文件成功；其余见错误列。";
            if (ok > 0 && MessageBox.Show(status.Text + "\r\n是否打开保存文件夹？", "处理完成", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                try { foreach (string folder in (overwrite ? files.Select(f=>Path.GetDirectoryName(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : new string[] {dir})) System.Diagnostics.Process.Start("explorer.exe", "\"" + folder + "\""); }
                catch (System.Exception ex) { MessageBox.Show(ex.Message); }
            }
            else if (ok == 0) MessageBox.Show(status.Text);
        }
    }
}
