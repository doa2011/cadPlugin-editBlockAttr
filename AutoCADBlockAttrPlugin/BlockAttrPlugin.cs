using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Drawing;
using System.Windows.Forms;

using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

// AutoCAD 2013 起把 Application 拆成两个类：
//   Autodesk.AutoCAD.ApplicationServices.Core.Application  (accoremgd.dll) -> DocumentManager
//   Autodesk.AutoCAD.ApplicationServices.Application      (acmgd.dll)      -> ShowModalDialog
// 2010~2012 两者都在 acmgd.dll 的 Application 类上。
// 多版本构建时由 build_all.bat 传入 ACAD_2013_PLUS 常量；VS 中打开主工程
// 不带该常量，默认按 2010 方式编译。
#if ACAD_2013_PLUS
using CadCore = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

[assembly: CommandClass(typeof(AutoCADBlockAttrPlugin.BlockAttrModCommand))]
// NETLOAD 时由 AutoCAD 自动调用 PluginLoadInfo.Initialize()，在命令行打印加载结果。
[assembly: ExtensionApplication(typeof(AutoCADBlockAttrPlugin.PluginLoadInfo))]

namespace AutoCADBlockAttrPlugin
{
    /// <summary>屏蔽 2010~2012 与 2013+ 之间 Application 类的拆分差异。</summary>
    internal static class CadHost
    {
        internal static Document ActiveDocument
        {
            get
            {
#if ACAD_2013_PLUS
                return CadCore.DocumentManager.MdiActiveDocument;
#else
                return CadApp.DocumentManager.MdiActiveDocument;
#endif
            }
        }

        internal static Editor ActiveEditor
        {
            get
            {
                Document doc = ActiveDocument;
                return doc == null ? null : doc.Editor;
            }
        }

        /// <summary>当前 AutoCAD 版本号（形如 18.0s / 24.3s），仅用于加载提示。</summary>
        internal static string AcadVersion
        {
            get
            {
#if ACAD_2013_PLUS
                object v = CadCore.GetSystemVariable("ACADVER");
#else
                object v = CadApp.GetSystemVariable("ACADVER");
#endif
                return v == null ? "" : v.ToString();
            }
        }

        internal static void ShowDialog(Form form)
        {
#if ACAD_2013_PLUS
            // 2013+ 的 Core.Application 不提供 ShowModalDialog；直接以 WinForms
            // 模态方式显示，命令线程同样会被阻塞，行为一致。
            form.ShowDialog();
#else
            CadApp.ShowModalDialog(form);
#endif
        }
    }

    /// <summary>
    /// NETLOAD 后（或执行 EditBlockAttHelp 时）在命令行打印加载结果、命令与用法简介。
    /// 编译目标年份不写死：从本程序集的 AutoCAD AssemblyRef 版本号反推，
    /// 因此 15 个年份 DLL 由同一份源码生成也能各自报出正确的目标版本。
    /// 全部只读，且由调用方包在 try/catch 中，提示失败绝不影响插件加载。
    /// </summary>
    internal static class LoadBanner
    {
        internal static void Write(Editor ed, bool justLoaded)
        {
            Version target = TargetAcadVersion();
            string targetYear = YearOf(target == null ? "" :
                                       target.Major + "." + target.Minor);
            string targetText = target == null ? "未知" : target.ToString();

            string acadVer = "";
            string currentYear = "";
            try
            {
                acadVer = CadHost.AcadVersion;
                currentYear = YearOf(VersionPrefix(acadVer));
            }
            catch { }

            Version self = Assembly.GetExecutingAssembly().GetName().Version;

            ed.WriteMessage("\n========== 块属性批量修改器 ==========");
            ed.WriteMessage("\n  " + (justLoaded ? "加载结果：成功" : "插件状态：已加载")
                + "（v" + self + "，" + (IntPtr.Size * 8) + " 位）");
            ed.WriteMessage("\n  编译目标：AutoCAD " + targetYear
                + "（程序集 " + targetText + "，" + FrameworkLabel() + "）");
            ed.WriteMessage("\n  当前 CAD：ACADVER="
                + (acadVer.Length > 0 ? acadVer : "未知")
                + (currentYear != "未知" ? "（AutoCAD " + currentYear + "）" : ""));
            ed.WriteMessage("\n  命令    ：EditBlockAtt      帮助：EditBlockAttHelp");

            if (currentYear != "未知" && targetYear != "未知"
                && currentYear != targetYear)
                ed.WriteMessage("\n  [!] 版本不匹配：本 DLL 面向 AutoCAD " + targetYear
                    + "，请改用 AutoCADBlockAttrPlugin_" + currentYear + ".dll");

            ed.WriteMessage("\n  用法    ：输入 EditBlockAtt → 选一个块参照 → 自动找出全部同名块");
            ed.WriteMessage("\n            属性栏每行一个属性 TAG，「统一修改为」已预填所选块当前值；");
            ed.WriteMessage("\n            勾选「顺序编号」按排序填 1、2、3…，未勾选则统一修改为栏中值，");
            ed.WriteMessage("\n            清空该栏表示该属性保持不变；选排序方式后点「应用修改」。");
            ed.WriteMessage("\n========================================");
        }

        /// <summary>读取本 DLL 引用的 acmgd / acdbmgd / accoremgd 版本号（编译目标）。</summary>
        private static Version TargetAcadVersion()
        {
            AssemblyName[] refs =
                Assembly.GetExecutingAssembly().GetReferencedAssemblies();
            foreach (AssemblyName an in refs)
            {
                string n = an.Name.ToLowerInvariant();
                if (n.StartsWith("ac") && n.EndsWith("mgd"))
                    return an.Version;
            }
            return null;
        }

        /// <summary>把 "18.0s" / "24.3s" 之类截成 "18.0" / "24.3"。</summary>
        private static string VersionPrefix(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = 0;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            return s.Substring(0, i).TrimEnd('.');
        }

        /// <summary>AutoCAD 托管程序集主.次版本 → 发行年份。</summary>
        private static string YearOf(string key)
        {
            switch (key)
            {
                case "18.0": return "2010";
                case "18.1": return "2011";
                case "18.2": return "2012";
                case "19.0": return "2013";
                case "19.1": return "2014";
                case "20.0": return "2015";
                case "20.1": return "2016";
                case "21.0": return "2017";
                case "22.0": return "2018";
                case "23.0": return "2019";
                case "23.1": return "2020";
                case "24.0": return "2021";
                case "24.1": return "2022";
                case "24.2": return "2023";
                case "24.3": return "2024";
                default: return "未知";
            }
        }

        private static string FrameworkLabel()
        {
#if ACAD_2013_PLUS
            return ".NET 4.x / CLR 4";
#else
            return ".NET 3.5 / CLR 2.0";
#endif
        }
    }

    /// <summary>NETLOAD 加载钩子：打印加载结果、命令名与用法简介。</summary>
    public class PluginLoadInfo : IExtensionApplication
    {
        public void Initialize()
        {
            try
            {
                Editor ed = CadHost.ActiveEditor;
                if (ed != null)
                    LoadBanner.Write(ed, true);
            }
            catch (System.Exception ex)
            {
                // 提示信息失败不能导致 NETLOAD 失败。
                try
                {
                    Editor ed = CadHost.ActiveEditor;
                    if (ed != null)
                        ed.WriteMessage("\n[块属性批量修改器] 已加载，但提示输出失败："
                            + ex.Message);
                }
                catch { }
            }
        }

        public void Terminate() { }
    }

    public class BlockAttrModCommand
    {
        /// <summary>随时重新查看加载信息与用法简介。</summary>
        [CommandMethod("EditBlockAttHelp")]
        public void EditBlockAttHelpCmd()
        {
            try
            {
                Editor ed = CadHost.ActiveEditor;
                if (ed != null)
                    LoadBanner.Write(ed, false);
            }
            catch { }
        }

        [CommandMethod("EditBlockAtt")]
        public void EditBlockAttCmd()
        {
            Document doc = CadHost.ActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            // 1. 选择一个块作为查找模板
            PromptEntityOptions peo =
                new PromptEntityOptions("\n请选择一个块参照: ");

            peo.SetRejectMessage("\n请选择块参照！");
            peo.AddAllowedClass(typeof(BlockReference), false);

            PromptEntityResult per = ed.GetEntity(peo);

            if (per.Status != PromptStatus.OK)
                return;

            ObjectId[] blockIds;
            string blockName;

            // 2. 获取块名称并查找所有同名块
            using (Transaction tr =
                db.TransactionManager.StartTransaction())
            {
                BlockReference selected =
                    tr.GetObject(per.ObjectId, OpenMode.ForRead)
                    as BlockReference;

                if (selected == null)
                    return;

                blockName = GetEffectiveBlockName(selected, tr);

                TypedValue[] values =
                {
                    new TypedValue((int)DxfCode.Start, "INSERT")
                };

                SelectionFilter filter =
                    new SelectionFilter(values);

                PromptSelectionResult psr =
                    ed.SelectAll(filter);

                if (psr.Status != PromptStatus.OK)
                    return;

                List<ObjectId> found = new List<ObjectId>();

                foreach (ObjectId id in psr.Value.GetObjectIds())
                {
                    BlockReference br =
                        tr.GetObject(id, OpenMode.ForRead)
                        as BlockReference;

                    if (br == null)
                        continue;

                    string name = GetEffectiveBlockName(br, tr);

                    if (string.Equals(
                        name,
                        blockName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(id);
                    }
                }

                blockIds = found.ToArray();

                tr.Commit();
            }

            if (blockIds.Length == 0)
            {
                ed.WriteMessage("\n没有找到同名块。");
                return;
            }

            ed.WriteMessage(
                "\n块名称：" + blockName +
                "，共找到：" + blockIds.Length + " 个。");

            // 3. 弹出修改窗口；把用户选中的那个块传进去，用于读取属性默认值
            using (AttrModForm form =
                new AttrModForm(blockIds, blockName, per.ObjectId))
            {
                CadHost.ShowDialog(form);
            }
        }

        // 获取真实块名称，兼容动态块
        private static string GetEffectiveBlockName(
            BlockReference br,
            Transaction tr)
        {
            ObjectId recordId = br.IsDynamicBlock
                ? br.DynamicBlockTableRecord
                : br.BlockTableRecord;

            BlockTableRecord record =
                tr.GetObject(recordId, OpenMode.ForRead)
                as BlockTableRecord;

            return record != null ? record.Name : br.Name;
        }
    }

    public partial class AttrModForm : Form
    {
        /// <summary>属性栏中的一行：TAG、所选块上的默认值、勾选框、统一修改输入框。</summary>
        private sealed class AttrRow
        {
            internal string Tag;
            internal string DefaultValue;
            internal CheckBox ChkSequential;
            internal TextBox TxtValue;
        }

        /// <summary>应用按钮实际执行的规则。</summary>
        private sealed class AttrRule
        {
            internal string Tag;
            internal bool Sequential;
            internal string Value;
        }

        private ObjectId[] _blockIds;
        private string _blockName;
        private ObjectId _sampleId;
        private List<AttrRow> _rows = new List<AttrRow>();
        private ToolTip _tip = new ToolTip();

        private RadioButton rbLeftRight;
        private RadioButton rbTopBottom;
        private Panel attrPanel;
        private Label lblCount;
        private Button btnApply;
        private Button btnCancel;
        private Panel _escapeConfirmPanel;

        public AttrModForm(ObjectId[] blockIds, string blockName, ObjectId sampleId)
        {
            _blockIds = blockIds;
            _blockName = blockName;
            _sampleId = sampleId;

            KeyPreview = true;
            KeyDown += AttrModForm_KeyDown;
            InitializeComponent();
            LoadAttributes();
            InitializeExtendedTabs();
        }

        private void InitializeComponent()
        {
            Text = "块属性批量修改器";
            Size = new Size(760, 560);
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = true;
            MinimizeBox = true;
            MinimumSize = new Size(1000, 700);

            Label lblBlock = new Label()
            {
                Text = "块名称：" + _blockName,
                Location = new Point(20, 15),
                AutoSize = true
            };

            lblCount = new Label()
            {
                Text = "同名块数量：" + _blockIds.Length,
                Location = new Point(20, 38),
                AutoSize = true
            };

            Label lblHint = new Label()
            {
                Text = "「统一修改为」已预填所选块的属性值，直接应用即可把全部同名块统一成该值；"
                     + "不需要修改的属性请清空该栏；勾选「顺序编号」则按排序依次填 1、2、3…",
                Location = new Point(20, 62),
                Size = new Size(710, 38)
            };

            GroupBox gbSort = new GroupBox()
            {
                Text = "块排序方式",
                Location = new Point(20, 105),
                Size = new Size(710, 58)
            };

            rbLeftRight = new RadioButton()
            {
                Text = "从左至右（X 升序，Y 降序）",
                Location = new Point(15, 23),
                Size = new Size(300, 20),
                Checked = true
            };

            rbTopBottom = new RadioButton()
            {
                Text = "从上到下（Y 降序，X 升序）",
                Location = new Point(330, 23),
                Size = new Size(300, 20)
            };

            gbSort.Controls.Add(rbLeftRight);
            gbSort.Controls.Add(rbTopBottom);

            // ---------------- 属性栏 ----------------
            GroupBox gbAttrs = new GroupBox()
            {
                Text = "属性栏（各属性独立设置，已预填所选块的默认值）",
                Location = new Point(20, 173),
                Size = new Size(710, 298)
            };

            gbAttrs.Controls.Add(new Label()
            {
                Text = "顺序编号",
                Location = new Point(12, 22),
                Size = new Size(90, 18)
            });
            gbAttrs.Controls.Add(new Label()
            {
                Text = "属性 TAG",
                Location = new Point(110, 22),
                Size = new Size(200, 18)
            });
            gbAttrs.Controls.Add(new Label()
            {
                Text = "统一修改为（清空则不修改该属性）",
                Location = new Point(322, 22),
                Size = new Size(360, 18)
            });

            attrPanel = new Panel()
            {
                Location = new Point(10, 44),
                Size = new Size(686, 242),
                AutoScroll = true,
                BorderStyle = BorderStyle.FixedSingle
            };
            gbAttrs.Controls.Add(attrPanel);

            btnApply = new Button()
            {
                Text = "应用修改",
                Location = new Point(555, 486),
                Size = new Size(85, 28)
            };

            btnCancel = new Button()
            {
                Text = "取消",
                Location = new Point(650, 486),
                Size = new Size(85, 28)
            };

            btnApply.Click += BtnApply_Click;
            btnCancel.Click += (s, e) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };

            Controls.Add(lblBlock);
            Controls.Add(lblCount);
            Controls.Add(lblHint);
            Controls.Add(gbSort);
            Controls.Add(gbAttrs);
            Controls.Add(btnApply);
            Controls.Add(btnCancel);

            _escapeConfirmPanel = new Panel();
            _escapeConfirmPanel.Size = new Size(360, 126);
            _escapeConfirmPanel.BorderStyle = BorderStyle.FixedSingle;
            _escapeConfirmPanel.BackColor = SystemColors.Control;
            _escapeConfirmPanel.Visible = false;
            _escapeConfirmPanel.Anchor = AnchorStyles.None;
            Label escapeText = new Label();
            escapeText.Text = "确定退出块属性批量修改器？\\r\\n再次按 ESC 退出，或选择继续使用。";
            escapeText.Location = new Point(18, 16);
            escapeText.Size = new Size(320, 48);
            Button continueButton = new Button();
            continueButton.Text = "继续使用";
            continueButton.Location = new Point(92, 78);
            continueButton.Size = new Size(82, 28);
            continueButton.Click += delegate { _escapeConfirmPanel.Visible = false; };
            Button exitButton = new Button();
            exitButton.Text = "退出";
            exitButton.Location = new Point(188, 78);
            exitButton.Size = new Size(82, 28);
            exitButton.Click += delegate { Close(); };
            _escapeConfirmPanel.Controls.Add(escapeText);
            _escapeConfirmPanel.Controls.Add(continueButton);
            _escapeConfirmPanel.Controls.Add(exitButton);
            Controls.Add(_escapeConfirmPanel);
            _escapeConfirmPanel.BringToFront();
        }

        private void AttrModForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Escape) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (_escapeConfirmPanel.Visible)
            {
                Close();
                return;
            }
            _escapeConfirmPanel.Left = (ClientSize.Width - _escapeConfirmPanel.Width) / 2;
            _escapeConfirmPanel.Top = (ClientSize.Height - _escapeConfirmPanel.Height) / 2;
            _escapeConfirmPanel.Visible = true;
            _escapeConfirmPanel.BringToFront();
        }

        /// <summary>读取所选块的全部属性 TAG 与当前值，预填到「统一修改为」栏。</summary>
        private void LoadAttributes()
        {
            Document doc = CadHost.ActiveDocument;

            using (Transaction tr =
                doc.TransactionManager.StartTransaction())
            {
                ObjectId readId = _sampleId;
                if (readId.IsNull && _blockIds.Length > 0)
                    readId = _blockIds[0];

                BlockReference sample =
                    tr.GetObject(readId, OpenMode.ForRead)
                    as BlockReference;

                if (sample != null)
                {
                    foreach (ObjectId attId in sample.AttributeCollection)
                    {
                        AttributeReference ar =
                            tr.GetObject(attId, OpenMode.ForRead)
                            as AttributeReference;

                        if (ar == null || string.IsNullOrEmpty(ar.Tag))
                            continue;

                        string tag = ar.Tag;
                        bool exists = _rows.Exists(delegate(AttrRow r)
                        {
                            return string.Equals(r.Tag, tag,
                                StringComparison.OrdinalIgnoreCase);
                        });
                        if (exists)
                            continue;

                        _rows.Add(new AttrRow
                        {
                            Tag = tag,
                            DefaultValue = ar.TextString ?? ""
                        });
                    }
                }

                tr.Commit();
            }

            if (_rows.Count == 0)
            {
                Label empty = new Label()
                {
                    Text = "该块没有可修改的属性！",
                    Location = new Point(8, 8),
                    AutoSize = true
                };
                attrPanel.Controls.Add(empty);
                btnApply.Enabled = false;
                return;
            }

            int y = 4;
            foreach (AttrRow row in _rows)
            {
                AttrRow current = row;

                CheckBox chk = new CheckBox()
                {
                    Text = "顺序编号",
                    Location = new Point(5, y),
                    Size = new Size(95, 22),
                    Checked = false
                };

                Label lblTag = new Label()
                {
                    Text = current.Tag,
                    Location = new Point(108, y + 3),
                    Size = new Size(200, 18),
                    AutoEllipsis = true
                };
                _tip.SetToolTip(lblTag, current.Tag);

                // 「统一修改为」默认填入所选块的当前值；清空该栏表示不修改此属性。
                TextBox txtValue = new TextBox()
                {
                    Text = current.DefaultValue,
                    Location = new Point(320, y),
                    Size = new Size(352, 22)
                };
                _tip.SetToolTip(txtValue, current.DefaultValue);

                // 勾选「顺序编号」后该行走顺序填充；取消勾选时恢复预填值，
                // 避免误把该属性清空或漏改。
                chk.CheckedChanged += delegate(object s, EventArgs e)
                {
                    txtValue.Enabled = !chk.Checked;
                    if (chk.Checked)
                        txtValue.Tag = txtValue.Text;
                    else if (txtValue.Tag != null)
                        txtValue.Text = txtValue.Tag.ToString();
                };

                current.ChkSequential = chk;
                current.TxtValue = txtValue;

                attrPanel.Controls.Add(chk);
                attrPanel.Controls.Add(lblTag);
                attrPanel.Controls.Add(txtValue);

                y += 28;
            }
        }

        /// <summary>按属性栏设置批量修改同名块的属性。</summary>
        private void BtnApply_Click(object sender, EventArgs e)
        {
            // 1. 汇总规则：勾选 -> 顺序编号；未勾选 -> 本行「统一修改为」的内容。
            //    本行留空（被用户清空）表示该属性保持不变，不参与修改。
            List<AttrRule> rules = new List<AttrRule>();

            foreach (AttrRow row in _rows)
            {
                AttrRule rule = new AttrRule();
                rule.Tag = row.Tag;
                rule.Sequential = row.ChkSequential.Checked;

                if (!rule.Sequential)
                {
                    rule.Value = row.TxtValue.Text ?? "";
                    if (rule.Value.Length == 0)
                        continue;   // 未勾选且清空了文字 -> 不改该属性
                }
                else
                {
                    rule.Value = "";
                }

                rules.Add(rule);
            }

            if (rules.Count == 0)
            {
                MessageBox.Show(
                    "没有可执行的修改：请在「统一修改为」栏保留或填写内容，或勾选「顺序编号」。",
                    "提示",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            Document doc = CadHost.ActiveDocument;
            int modified = 0;
            int missing = 0;

            try
            {
                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr =
                    doc.TransactionManager.StartTransaction())
                {
                    List<BlockSortData> blocks =
                        new List<BlockSortData>();

                    foreach (ObjectId id in _blockIds)
                    {
                        BlockReference br =
                            tr.GetObject(id, OpenMode.ForRead)
                            as BlockReference;

                        if (br == null)
                            continue;

                        Point3d pos = br.Position;

                        blocks.Add(new BlockSortData
                        {
                            Id = id,
                            X = pos.X,
                            Y = pos.Y
                        });
                    }

                    if (rbLeftRight.Checked)
                    {
                        blocks = blocks
                            .OrderBy(b => b.X)
                            .ThenByDescending(b => b.Y)
                            .ToList();
                    }
                    else
                    {
                        blocks = blocks
                            .OrderByDescending(b => b.Y)
                            .ThenBy(b => b.X)
                            .ToList();
                    }

                    // 每个顺序编号属性各自维护一个计数器，按排序后的块递增。
                    Dictionary<string, int> counters =
                        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (AttrRule rule in rules)
                    {
                        if (rule.Sequential)
                            counters[rule.Tag] = 1;
                    }

                    foreach (BlockSortData bd in blocks)
                    {
                        BlockReference br =
                            tr.GetObject(bd.Id, OpenMode.ForRead)
                            as BlockReference;

                        if (br == null)
                            continue;

                        // 块参照只需 ForRead；真正要写的是 AttributeReference。
                        Dictionary<string, AttributeReference> attrs =
                            new Dictionary<string, AttributeReference>(
                                StringComparer.OrdinalIgnoreCase);

                        foreach (ObjectId attId in br.AttributeCollection)
                        {
                            AttributeReference ar =
                                tr.GetObject(attId, OpenMode.ForWrite)
                                as AttributeReference;

                            if (ar == null || string.IsNullOrEmpty(ar.Tag))
                                continue;

                            if (!attrs.ContainsKey(ar.Tag))
                                attrs.Add(ar.Tag, ar);
                        }

                        foreach (AttrRule rule in rules)
                        {
                            AttributeReference ar;
                            if (!attrs.TryGetValue(rule.Tag, out ar))
                            {
                                missing++;
                                continue;
                            }

                            string newValue;
                            if (rule.Sequential)
                            {
                                newValue = counters[rule.Tag].ToString();
                                counters[rule.Tag] = counters[rule.Tag] + 1;
                            }
                            else
                            {
                                newValue = rule.Value;
                            }

                            if (!string.Equals(ar.TextString, newValue,
                                               StringComparison.Ordinal))
                            {
                                ar.TextString = newValue;
                                modified++;
                            }
                        }
                    }

                    tr.Commit();
                }

                int sequentialCount = 0;
                foreach (AttrRule rule in rules)
                {
                    if (rule.Sequential)
                        sequentialCount++;
                }

                MessageBox.Show(
                    "属性修改完成！\n\n" +
                    "块名称：" + _blockName + "\n" +
                    "同名块数量：" + _blockIds.Length + " 个\n" +
                    "顺序编号属性：" + sequentialCount + " 个\n" +
                    "统一修改属性：" + (rules.Count - sequentialCount) + " 个\n" +
                    "成功写入：" + modified + " 处\n" +
                    "块中缺少该属性：" + missing + " 处",
                    "修改结果",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                DialogResult = DialogResult.OK;
                Close();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(
                    "修改失败：\n" + ex.GetType().Name + ": " + ex.Message,
                    "错误",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private struct BlockSortData
        {
            public ObjectId Id;
            public double X;
            public double Y;
        }
    }
}
