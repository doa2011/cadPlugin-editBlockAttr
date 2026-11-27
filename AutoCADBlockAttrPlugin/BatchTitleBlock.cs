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

        internal static CatalogEntry ReadCatalog(string file, Options options)
        {
            CatalogEntry entry = new CatalogEntry();
            entry.File = file;
            SplitDrawingFileName(file, out entry.Number, out entry.Title);
            if (String.IsNullOrEmpty(entry.Title)) { entry.Title = Path.GetFileNameWithoutExtension(file); entry.Number = ""; }
            try {
                using (Database db = new Database(false,true)) {
                    db.ReadDwgFile(file,FileShare.Read,true,"");
                    using (Transaction tr = db.TransactionManager.StartTransaction()) {
                        List<Item> items = GetBlocks(db,tr,null);
                        HashSet<string> numbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        HashSet<string> titles = new HashSet<string>(StringComparer.Ordinal);
                        HashSet<string> totals = new HashSet<string>();
                        int count = 0; int incomplete = 0;
                        foreach (Item item in items) {
                            Options rule = options.Rules.FirstOrDefault(x=>String.Equals(x.BlockName,item.Name,StringComparison.OrdinalIgnoreCase));
                            if (rule == null) continue;
                            BlockReference br = tr.GetObject(item.Id,OpenMode.ForRead) as BlockReference;
                            Dictionary<string,string> attrs = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                            foreach (ObjectId aid in br.AttributeCollection) {
                                AttributeReference ar = tr.GetObject(aid,OpenMode.ForRead) as AttributeReference;
                                if (ar != null && !attrs.ContainsKey(ar.Tag)) attrs.Add(ar.Tag,ar.TextString.Trim());
                            }
                            string n,t,p,total;
                            if (!attrs.TryGetValue(rule.DrawingTag,out n) || !attrs.TryGetValue(rule.TitleTag,out t) || !attrs.TryGetValue(rule.PageTag,out p) || !attrs.TryGetValue(rule.TotalTag,out total)) { incomplete++; continue; }
                            count++; if (n.Length>0) numbers.Add(n); if(t.Length>0) titles.Add(t); if(total.Length>0) totals.Add(total);
                        }
                        if(count==0) entry.Warning="未读取有效角标，使用文件名，页数留空";
                        else {
                            if(numbers.Count==1) entry.Number=numbers.First();
                            if(titles.Count==1) entry.Title=titles.First();
                            entry.Pages=count.ToString();
                            if(numbers.Count!=1 || titles.Count!=1) entry.Warning="图名或图号缺失/不一致，需确认；";
                            if(totals.Any(x=>x!=entry.Pages)) entry.Warning+="角标总页数与实际数量不一致；";
                            if(incomplete>0) { entry.Pages=""; entry.Warning+="存在缺失属性的角标，页数待核查；"; }
                        }
                        tr.Commit();
                    }
                }
            } catch(System.Exception ex) { entry.Pages=""; entry.Warning="读取失败："+ex.Message; }
            if(String.IsNullOrEmpty(entry.Number)) entry.Warning+=" 图号为空；";
            return entry;
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


        public BatchTitleBlockForm() : this(false) { }

        public BatchTitleBlockForm(bool catalogOnly)
        {
            Text = catalogOnly ? "图纸目录 - 选择文件及角标" : "批量角标修改 - AutoCAD DWG";
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
            Button catalog = new Button { Text = "生成图纸目录", Width = 125 }; catalog.Click += delegate { if(files.Count==0) { MessageBox.Show("请先选择 DWG 文件"); return; } using(CatalogForm form=new CatalogForm(files,ReadOptions())) form.ShowDialog(this); };
            bar.Controls.Add(catalog); bar.Controls.Add(pick); bar.Controls.Add(remove); bar.Controls.Add(clear); bar.Controls.Add(detect); if (!catalogOnly) { bar.Controls.Add(inspect); bar.Controls.Add(write); }
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
            status.Text = catalogOnly ? "选择 DWG、确认角标映射，再生成目录；不会修改原始图纸。" : "先选择 DWG，指定块名与四个实际 TAG，读取预览后再确认写入。输出目录为空时覆盖原图，执行前确认。";
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

    internal sealed class CatalogEntry
    {
        internal string File="", Number="", Title="", Pages="", Size="A3", Note="", Warning="";
    }

    internal sealed class CatalogForm : Form
    {
        private readonly DataGridView data = new DataGridView();
        private readonly TextBox project = new TextBox(), task = new TextBox(), edition = new TextBox(), stage = new TextBox(), date = new TextBox();
        private readonly CheckBox pdf = new CheckBox { Text="同时导出 PDF（需 Excel）", AutoSize=true };
        internal CatalogForm(IEnumerable<string> files, BatchTitleBlockProcessor.Options options)
        {
            Text="图纸目录生成（只读 DWG）"; Width=1160; Height=700; StartPosition=FormStartPosition.CenterParent;
            TableLayoutPanel panel=new TableLayoutPanel { Dock=DockStyle.Fill, RowCount=3, ColumnCount=1 };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute,78)); panel.RowStyles.Add(new RowStyle(SizeType.Percent,100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute,45)); Controls.Add(panel);
            FlowLayoutPanel fields=new FlowLayoutPanel { Dock=DockStyle.Fill,AutoScroll=true };
            string[] labels={"工程名称","任务编号","版本号","设计阶段","完成时间"};
            TextBox[] boxes={project,task,edition,stage,date};
            for(int i=0;i<boxes.Length;i++){ fields.Controls.Add(new Label {Text=labels[i],AutoSize=true}); boxes[i].Width=i==0?260:100; fields.Controls.Add(boxes[i]); }
            stage.Text="施工图"; date.Text=DateTime.Now.ToString("yyyy.MM"); panel.Controls.Add(fields,0,0);
            data.Dock=DockStyle.Fill; data.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill; data.AllowUserToAddRows=true;
            foreach(string title in new string[]{"图号","图纸名称","页数","图幅","备注","核查提示","来源"}) data.Columns.Add(title,title);
            data.Columns[5].ReadOnly=true; data.Columns[6].ReadOnly=true;
            foreach(DataGridViewColumn col in data.Columns) col.SortMode=DataGridViewColumnSortMode.NotSortable;
            panel.Controls.Add(data,0,1);
            FlowLayoutPanel actions=new FlowLayoutPanel {Dock=DockStyle.Fill};
            Button sort=new Button {Text="按图号排序",Width=125}; sort.Click+=delegate { SortRows(); };
            Button save=new Button {Text="生成 A3 目录",Width=150}; save.Click+=Save;
            actions.Controls.Add(sort); actions.Controls.Add(save); actions.Controls.Add(pdf); panel.Controls.Add(actions,0,2);
            foreach(string file in files){ CatalogEntry e=BatchTitleBlockProcessor.ReadCatalog(file,options); data.Rows.Add(e.Number,e.Title,e.Pages,e.Size,e.Note,e.Warning,e.File); }
            SortRows();
        }
        private List<CatalogEntry> Entries()
        {
            data.EndEdit(); List<CatalogEntry> entries=new List<CatalogEntry>();
            foreach(DataGridViewRow row in data.Rows) {
                if(row.IsNewRow) continue;
                string[] v=new string[7]; for(int i=0;i<7;i++) v[i]=Convert.ToString(row.Cells[i].Value).Trim();
                if(v[0]=="" && v[1]=="" && v[6]=="") continue;
                entries.Add(new CatalogEntry {Number=v[0],Title=v[1],Pages=v[2],Size=v[3]==""?"A3":v[3],Note=v[4],Warning=v[5],File=v[6]});
            }
            entries.Sort(delegate(CatalogEntry a,CatalogEntry b){ int n=Compare(a.Number,b.Number); return n!=0?n:StringComparer.OrdinalIgnoreCase.Compare(a.File,b.File); });
            return entries;
        }
        private void SortRows(){ List<CatalogEntry> entries=Entries(); data.Rows.Clear(); foreach(CatalogEntry e in entries) data.Rows.Add(e.Number,e.Title,e.Pages,e.Size,e.Note,e.Warning,e.File); }
        internal static int Compare(string a,string b)
        {
            if(a.Length==0 || b.Length==0) return a.Length==b.Length?0:a.Length==0?1:-1;
            string[] roman={"Ⅰ","Ⅱ","Ⅲ","Ⅳ","Ⅴ","Ⅵ","Ⅶ","Ⅷ","Ⅸ","Ⅹ","Ⅺ","Ⅻ"};
            for(int i=0;i<roman.Length;i++){ a=a.Replace(roman[i],(i+1).ToString()); b=b.Replace(roman[i],(i+1).ToString()); }
            string[] aa=System.Text.RegularExpressions.Regex.Split(a,@"([0-9]+)"), bb=System.Text.RegularExpressions.Regex.Split(b,@"([0-9]+)");
            for(int i=0;i<Math.Min(aa.Length,bb.Length);i++){
                string x=aa[i],y=bb[i]; int n;
                if(i%2==1){ x=x.TrimStart('0'); y=y.TrimStart('0'); n=x.Length.CompareTo(y.Length); if(n==0)n=String.CompareOrdinal(x,y); }
                else n=StringComparer.OrdinalIgnoreCase.Compare(x,y);
                if(n!=0)return n;
            }
            return aa.Length.CompareTo(bb.Length);
        }
        private void Save(object sender,EventArgs args)
        {
            List<CatalogEntry> entries=Entries(); if(entries.Count==0){MessageBox.Show("没有目录记录");return;}
            foreach(CatalogEntry e in entries){int n;if(e.Pages!="" && (!Int32.TryParse(e.Pages,out n)||n<=0)){MessageBox.Show("页数必须为空或正整数："+e.Title);return;}}
            using(SaveFileDialog dlg=new SaveFileDialog {Filter="Excel 工作簿 (*.xlsx)|*.xlsx",FileName="图纸目录.xlsx",OverwritePrompt=true}){
                if(dlg.ShowDialog(this)!=DialogResult.OK)return;
                try {
                    CatalogWorkbook.Write(dlg.FileName,entries,new string[]{project.Text,task.Text,edition.Text,stage.Text,date.Text});
                    List<string> warnings=new List<string>();
                    foreach(CatalogEntry e in entries){ if(e.Warning!="")warnings.Add(e.File+" "+e.Warning); if(e.Number=="")warnings.Add(e.Title+"：图号为空"); }
                    foreach(var g in entries.Where(x=>x.Number!="").GroupBy(x=>x.Number,StringComparer.OrdinalIgnoreCase)) if(g.Count()>1)warnings.Add("重复图号："+g.Key);
                    if(pdf.Checked){try{CatalogWorkbook.Pdf(dlg.FileName);}catch(System.Exception ex){warnings.Add("PDF 未生成："+ex.Message);}}
                    if(warnings.Count>0) System.IO.File.WriteAllLines(Path.Combine(Path.GetDirectoryName(dlg.FileName),Path.GetFileNameWithoutExtension(dlg.FileName)+"_异常记录.txt"),warnings.ToArray(),System.Text.Encoding.UTF8);
                    if(MessageBox.Show("已生成 "+entries.Count+" 条记录，共 "+((entries.Count+36)/37)+" 页。\r\n异常："+warnings.Count+"\r\n"+String.Join("\r\n",warnings.Take(10).ToArray())+"\r\n是否打开文件夹？","目录生成完成",MessageBoxButtons.YesNo)==DialogResult.Yes)
                        System.Diagnostics.Process.Start("explorer.exe","\""+Path.GetDirectoryName(dlg.FileName)+"\"");
                }catch(System.Exception ex){MessageBox.Show("目录生成失败："+ex.Message);}
            }
        }
    }

    internal static class CatalogWorkbook
    {
        private const string Ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string Rel="http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
        private static string Esc(string s){return System.Security.SecurityElement.Escape(s??"");}
        private static void Put(System.IO.Packaging.PackagePart p,string xml){using(StreamWriter w=new StreamWriter(p.GetStream(FileMode.Create,FileAccess.Write),new System.Text.UTF8Encoding(false)))w.Write(xml);}
        private static string Cell(int col,int row,string v,int style){return "<c r=\""+(char)('A'+col)+row+"\" s=\""+style+"\" t=\"inlineStr\"><is><t xml:space=\"preserve\">"+Esc(v)+"</t></is></c>";}
        private static string Row(int r,string[] values,double height,int style)
        {
            System.Text.StringBuilder b=new System.Text.StringBuilder("<row r=\""+r+"\" ht=\""+height.ToString(System.Globalization.CultureInfo.InvariantCulture)+"\" customHeight=\"1\">");
            for(int i=0;i<values.Length;i++) b.Append(Cell(i,r,values[i],style==3 && i==1?4:style));
            return b.Append("</row>").ToString();
        }
        internal static void Write(string path,List<CatalogEntry> entries,string[] meta)
        {
            int pages=(entries.Count+36)/37; string temp=Path.Combine(Path.GetDirectoryName(path),Guid.NewGuid().ToString("N")+".xlsx");
            try{
            using(System.IO.Packaging.Package pkg=System.IO.Packaging.Package.Open(temp,FileMode.CreateNew,FileAccess.ReadWrite)){
                var wb=pkg.CreatePart(new Uri("/xl/workbook.xml",UriKind.Relative),"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
                pkg.CreateRelationship(new Uri("xl/workbook.xml",UriKind.Relative),System.IO.Packaging.TargetMode.Internal,Rel+"officeDocument");
                var st=pkg.CreatePart(new Uri("/xl/styles.xml",UriKind.Relative),"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
                wb.CreateRelationship(new Uri("styles.xml",UriKind.Relative),System.IO.Packaging.TargetMode.Internal,Rel+"styles");
                Put(st,"<styleSheet xmlns=\""+Ns+"\"><fonts count=\"3\"><font><sz val=\"12\"/><name val=\"宋体\"/></font><font><b/><sz val=\"20\"/><name val=\"宋体\"/></font><font><b/><sz val=\"12\"/><name val=\"宋体\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border><border><left style=\"thin\"/><right style=\"thin\"/><top style=\"thin\"/><bottom style=\"thin\"/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"5\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf><xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" shrinkToFit=\"1\"/></xf><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" shrinkToFit=\"1\"/></xf></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
                System.Text.StringBuilder sheets=new System.Text.StringBuilder(), names=new System.Text.StringBuilder();
                for(int p=0;p<pages;p++){
                    string name="目录"+(p+1), loc="worksheets/sheet"+(p+1)+".xml";
                    var part=pkg.CreatePart(new Uri("/xl/"+loc,UriKind.Relative),"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
                    var relation=wb.CreateRelationship(new Uri(loc,UriKind.Relative),System.IO.Packaging.TargetMode.Internal,Rel+"worksheet");
                    sheets.Append("<sheet name=\""+name+"\" sheetId=\""+(p+1)+"\" r:id=\""+relation.Id+"\"/>");
                    names.Append("<definedName name=\"_xlnm.Print_Area\" localSheetId=\""+p+"\">'"+name+"'!$A$1:$G$41</definedName>");
                    System.Text.StringBuilder x=new System.Text.StringBuilder("<worksheet xmlns=\""+Ns+"\"><sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr><cols>");
                    double[] widths={6,34,6,12,9,10,10};
                    for(int i=0;i<widths.Length;i++)x.Append("<col min=\""+(i+1)+"\" max=\""+(i+1)+"\" width=\""+widths[i]+"\" customWidth=\"1\"/>");
                    x.Append("</cols><sheetData>");
                    x.Append(Row(1,new string[]{"图  纸  目  录    共 "+pages+" 页   第 "+(p+1)+" 页","","","","","",""},27,1));
                    x.Append(Row(2,new string[]{"工程名称",meta[0],"","任务编号",meta[1],"版本号",meta[2]},18,3));
                    x.Append(Row(3,new string[]{"","","","设计阶段",meta[3],"完成时间",meta[4]},18,3));
                    x.Append(Row(4,new string[]{"序号","图纸名称","页数","图号","","图幅","备注"},18,2));
                    for(int i=0;i<37;i++){
                        int index=p*37+i; CatalogEntry e=index<entries.Count?entries[index]:null;
                        x.Append(Row(i+5,e==null?new string[]{"","","","","","",""}:new string[]{(index+1).ToString(),e.Title,e.Pages,e.Number,"",e.Size,e.Note},15.75,3));
                    }
                    x.Append("</sheetData><mergeCells><mergeCell ref=\"A1:G1\"/><mergeCell ref=\"A2:A3\"/><mergeCell ref=\"B2:C3\"/>");
                    for(int i=4;i<=41;i++)x.Append("<mergeCell ref=\"D"+i+":E"+i+"\"/>");
                    x.Append("</mergeCells><printOptions horizontalCentered=\"1\"/><pageMargins left=\"0.3\" right=\"0.3\" top=\"0.3\" bottom=\"0.3\" header=\"0.1\" footer=\"0.1\"/><pageSetup paperSize=\"8\" orientation=\"landscape\" fitToWidth=\"1\" fitToHeight=\"1\"/></worksheet>");
                    Put(part,x.ToString());
                }
                Put(wb,"<workbook xmlns=\""+Ns+"\" xmlns:r=\""+Rel.TrimEnd('/')+"\"><sheets>"+sheets+"</sheets><definedNames>"+names+"</definedNames></workbook>");
            }
            if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
            }finally{if(File.Exists(temp))File.Delete(temp);}
        }
        internal static void Pdf(string path)
        {
            Type t=Type.GetTypeFromProgID("Excel.Application"); if(t==null)throw new InvalidOperationException("未安装 Excel");
            object app=null,books=null,book=null; var get=System.Reflection.BindingFlags.GetProperty; var call=System.Reflection.BindingFlags.InvokeMethod;
            try{app=Activator.CreateInstance(t); books=t.InvokeMember("Workbooks",get,null,app,null);
                book=books.GetType().InvokeMember("Open",call,null,books,new object[]{path});
                book.GetType().InvokeMember("ExportAsFixedFormat",call,null,book,new object[]{0,Path.ChangeExtension(path,"pdf")});
            }finally{
                if(book!=null){try{book.GetType().InvokeMember("Close",call,null,book,new object[]{false});}catch{} System.Runtime.InteropServices.Marshal.FinalReleaseComObject(book);}
                if(books!=null)System.Runtime.InteropServices.Marshal.FinalReleaseComObject(books);
                if(app!=null){try{t.InvokeMember("Quit",call,null,app,null);}catch{} System.Runtime.InteropServices.Marshal.FinalReleaseComObject(app);}
            }
        }
    }
}
