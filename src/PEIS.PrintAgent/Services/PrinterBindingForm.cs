using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PEIS.Report.Contracts;

namespace PEIS.PrintAgent.Services;

/// <summary>
/// PEIS 打印代理 — 报表打印方式管理。
/// 无原生系统边框，具备圆角边框、高清 Logo、圆角按钮、柔和选中高亮，清爽不花哨。
/// </summary>
internal sealed class PrinterBindingForm : Form
{
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

    // ── 数据与依赖 ────────────────────────────────────────────────────────
    private readonly PrinterSelectionStore _store;
    private readonly IReadOnlyList<PrinterDescriptor> _installedPrinters;

    // ── UI 控件 ───────────────────────────────────────────────────────────
    private readonly DataGridView _grid;
    private readonly DataGridViewTextBoxColumn _djidCol;
    private readonly DataGridViewTextBoxColumn _descCol;
    private readonly DataGridViewComboBoxColumn _printerCol;
    private readonly DataGridViewComboBoxColumn _behaviorCol;
    private readonly DataGridViewComboBoxColumn _duplexCol;
    private readonly DataGridViewComboBoxColumn _orientationCol;
    private readonly DataGridViewTextBoxColumn _copiesCol;

    private readonly RoundedButton _quickAddBtn;
    private readonly RoundedButton _addBtn;
    private readonly RoundedButton _deleteBtn;
    private readonly RoundedButton _clearAllBtn;
    private readonly RoundedButton _okBtn;
    private readonly RoundedButton _cancelBtn;

    // ── 常量 ─────────────────────────────────────────────────────────────
    private const string ColDjid = "djid";
    private const string ColDesc = "desc";
    private const string ColPrinter = "printer";
    private const string ColBehavior = "behavior";
    private const string ColDuplex = "duplex";
    private const string ColOrientation = "orientation";
    private const string ColCopies = "copies";

    // 下拉选项常量映射
    private const string BehaviorAuto = "跟随默认 (Auto)";
    private const string BehaviorSilent = "直接静默出纸 (Silent)";
    private const string BehaviorPreview = "弹出预览确认 (Preview)";

    private const string DuplexAuto = "跟随默认 (Auto)";
    private const string DuplexSimplex = "强制单面 (Simplex)";
    private const string DuplexLong = "双面(长边翻转)";
    private const string DuplexShort = "双面(短边翻转)";

    private const string OrientAuto = "跟随默认 (Auto)";
    private const string OrientPortrait = "纵向 (Portrait)";
    private const string OrientLandscape = "横向 (Landscape)";

    // 常见体检 FastReport 专属单据预置字典
    private static readonly (string Djid, string Name)[] CommonPresetReports =
    [
        ("tjdjd", "体检导检单"),
        ("jktjbbd", "健康体检报告单"),
        ("zytjbbd", "职业体检报告单"),
        ("xmtm", "体检项目条码标签"),
        ("tjjkz", "体检健康证"),
        ("jzkdypz", "就诊卡打印凭证"),
        ("tjsfd", "体检收费单据"),
        ("zyjjzgzs", "职业禁忌证告知书")
    ];

    public PrinterBindingForm(PrinterSelectionStore store, IReadOnlyList<PrinterDescriptor> installedPrinters)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _installedPrinters = installedPrinters ?? throw new ArgumentNullException(nameof(installedPrinters));

        // ── 窗口基本属性：无原生边框、圆边、自适应 ────────────────────────
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(960, 580);
        MinimumSize = new Size(780, 440);
        ShowInTaskbar = true;
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        // ── 1. 顶部 Header (大图标与标题，去除系统原生重复标题栏) ─────────────
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 72,
            BackColor = Color.White,
            Padding = new Padding(16, 12, 16, 12)
        };
        headerPanel.MouseDown += (_, e) => DragWindow(e);

        // 底部细分隔线
        headerPanel.Paint += (_, pe) =>
        {
            using var pen = new Pen(Color.FromArgb(226, 232, 240), 1);
            pe.Graphics.DrawLine(pen, 0, headerPanel.Height - 1, headerPanel.Width, headerPanel.Height - 1);
        };

        // 大图标 Logo
        var logoBox = new PictureBox
        {
            Size = new Size(48, 48),
            Location = new Point(16, 12),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = AppIconHelper.LoadLogoImage()
        };
        logoBox.MouseDown += (_, e) => DragWindow(e);

        var titleLabel = new Label
        {
            Text = "PEIS 打印代理 — FastReport 报表打印方式配置",
            Font = new Font("Microsoft YaHei UI", 11.5F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(30, 41, 59),
            Location = new Point(74, 12),
            AutoSize = true
        };
        titleLabel.MouseDown += (_, e) => DragWindow(e);

        var subTitleLabel = new Label
        {
            Text = "按 FastReport 专属单据 (djid) 设置输出打印机、行为方式（静默/预览）、单双面、纸张方向与份数。",
            Font = new Font("Microsoft YaHei UI", 8.8F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(100, 116, 139),
            Location = new Point(74, 38),
            AutoSize = true
        };
        subTitleLabel.MouseDown += (_, e) => DragWindow(e);

        // 右上角优雅的关闭与最小化按钮
        var closeBtn = new Button
        {
            Text = "✕",
            Size = new Size(36, 32),
            Location = new Point(headerPanel.Width - 44, 12),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(100, 116, 139),
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand
        };
        closeBtn.FlatAppearance.BorderSize = 0;
        closeBtn.MouseEnter += (_, _) => { closeBtn.BackColor = Color.FromArgb(254, 226, 226); closeBtn.ForeColor = Color.FromArgb(220, 38, 38); };
        closeBtn.MouseLeave += (_, _) => { closeBtn.BackColor = Color.Transparent; closeBtn.ForeColor = Color.FromArgb(100, 116, 139); };
        closeBtn.Click += (_, _) => Close();

        var minBtn = new Button
        {
            Text = "─",
            Size = new Size(36, 32),
            Location = new Point(headerPanel.Width - 82, 12),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(100, 116, 139),
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand
        };
        minBtn.FlatAppearance.BorderSize = 0;
        minBtn.MouseEnter += (_, _) => { minBtn.BackColor = Color.FromArgb(241, 245, 249); };
        minBtn.MouseLeave += (_, _) => { minBtn.BackColor = Color.Transparent; };
        minBtn.Click += (_, _) => WindowState = FormWindowState.Minimized;

        headerPanel.Controls.Add(logoBox);
        headerPanel.Controls.Add(titleLabel);
        headerPanel.Controls.Add(subTitleLabel);
        headerPanel.Controls.Add(minBtn);
        headerPanel.Controls.Add(closeBtn);

        // ── 2. 工具栏 (全圆角按钮，去除生硬感) ──────────────────────────
        var toolPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = Color.FromArgb(248, 250, 252),
            Padding = new Padding(16, 6, 16, 6)
        };

        _quickAddBtn = new RoundedButton
        {
            Text = "⚡ 快速添加常用单据 ▾",
            Width = 172,
            Height = 32,
            CornerRadius = 6,
            NormalBackColor = Color.FromArgb(238, 242, 255),
            HoverBackColor = Color.FromArgb(224, 231, 255),
            PressedBackColor = Color.FromArgb(199, 210, 254),
            BorderColor = Color.FromArgb(199, 210, 254),
            ForeColor = Color.FromArgb(67, 56, 202)
        };

        _addBtn = new RoundedButton
        {
            Text = "➕ 新增自定义单据",
            Width = 135,
            Height = 32,
            CornerRadius = 6,
            NormalBackColor = Color.White,
            HoverBackColor = Color.FromArgb(241, 245, 249),
            PressedBackColor = Color.FromArgb(226, 232, 240),
            BorderColor = Color.FromArgb(203, 213, 225),
            ForeColor = Color.FromArgb(30, 41, 59)
        };

        _deleteBtn = new RoundedButton
        {
            Text = "🗑️ 删除选中单据",
            Width = 125,
            Height = 32,
            CornerRadius = 6,
            NormalBackColor = Color.White,
            HoverBackColor = Color.FromArgb(241, 245, 249),
            PressedBackColor = Color.FromArgb(226, 232, 240),
            BorderColor = Color.FromArgb(203, 213, 225),
            ForeColor = Color.FromArgb(30, 41, 59)
        };

        _clearAllBtn = new RoundedButton
        {
            Text = "🧹 清空全部",
            Width = 98,
            Height = 32,
            CornerRadius = 6,
            NormalBackColor = Color.FromArgb(254, 242, 242),
            HoverBackColor = Color.FromArgb(254, 226, 226),
            PressedBackColor = Color.FromArgb(254, 202, 202),
            BorderColor = Color.FromArgb(254, 202, 202),
            ForeColor = Color.FromArgb(185, 28, 28)
        };

        // 快捷单据下拉菜单
        var quickMenu = new ContextMenuStrip();
        foreach (var (djid, name) in CommonPresetReports)
        {
            var item = new ToolStripMenuItem($"{name} ({djid})", null, (_, _) => AddOrFocusRow(djid, name));
            quickMenu.Items.Add(item);
        }
        quickMenu.Items.Add(new ToolStripSeparator());
        quickMenu.Items.Add(new ToolStripMenuItem("一键添加全部常用单据", null, (_, _) => AddAllCommonPresets()));
        _quickAddBtn.Click += (_, _) => quickMenu.Show(_quickAddBtn, new Point(0, _quickAddBtn.Height));

        _quickAddBtn.Location = new Point(16, 6);
        _addBtn.Location = new Point(196, 6);
        _deleteBtn.Location = new Point(339, 6);
        _clearAllBtn.Location = new Point(472, 6);

        toolPanel.Controls.Add(_quickAddBtn);
        toolPanel.Controls.Add(_addBtn);
        toolPanel.Controls.Add(_deleteBtn);
        toolPanel.Controls.Add(_clearAllBtn);

        // ── 3. DataGridView 表格 (柔和现代选中高亮，隐藏刺眼行头) ────────────
        _djidCol = new DataGridViewTextBoxColumn
        {
            Name = ColDjid,
            HeaderText = "单据代码 (djid)",
            FillWeight = 16,
            SortMode = DataGridViewColumnSortMode.Automatic,
            ToolTipText = "FastReport 单据主键标识，如 tjdjd、jktjbbd、xmtm 等"
        };

        _descCol = new DataGridViewTextBoxColumn
        {
            Name = ColDesc,
            HeaderText = "单据名称 / 备注",
            FillWeight = 20,
            SortMode = DataGridViewColumnSortMode.Automatic,
            ToolTipText = "便于运维识别的中文名称，如体检导检单"
        };

        _printerCol = new DataGridViewComboBoxColumn
        {
            Name = ColPrinter,
            HeaderText = "绑定物理打印机",
            FillWeight = 28,
            FlatStyle = FlatStyle.Flat,
            DisplayStyleForCurrentCellOnly = true,
            SortMode = DataGridViewColumnSortMode.Automatic
        };
        foreach (var p in _installedPrinters)
            _printerCol.Items.Add(p.Name);

        _behaviorCol = new DataGridViewComboBoxColumn
        {
            Name = ColBehavior,
            HeaderText = "输出行为",
            FillWeight = 18,
            FlatStyle = FlatStyle.Flat,
            DisplayStyleForCurrentCellOnly = true,
            SortMode = DataGridViewColumnSortMode.Automatic
        };
        _behaviorCol.Items.AddRange(BehaviorAuto, BehaviorSilent, BehaviorPreview);

        _duplexCol = new DataGridViewComboBoxColumn
        {
            Name = ColDuplex,
            HeaderText = "单双面",
            FillWeight = 17,
            FlatStyle = FlatStyle.Flat,
            DisplayStyleForCurrentCellOnly = true,
            SortMode = DataGridViewColumnSortMode.Automatic
        };
        _duplexCol.Items.AddRange(DuplexAuto, DuplexSimplex, DuplexLong, DuplexShort);

        _orientationCol = new DataGridViewComboBoxColumn
        {
            Name = ColOrientation,
            HeaderText = "纸张方向",
            FillWeight = 16,
            FlatStyle = FlatStyle.Flat,
            DisplayStyleForCurrentCellOnly = true,
            SortMode = DataGridViewColumnSortMode.Automatic
        };
        _orientationCol.Items.AddRange(OrientAuto, OrientPortrait, OrientLandscape);

        _copiesCol = new DataGridViewTextBoxColumn
        {
            Name = ColCopies,
            HeaderText = "份数",
            FillWeight = 13,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            ToolTipText = "0 表示跟随接口请求；大于 0 表示强制固定份数"
        };

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false, // 隐藏刺眼的原生深色小方块行头，界面更加现代平整
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            EditMode = DataGridViewEditMode.EditOnEnter,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            GridColor = Color.FromArgb(226, 232, 240),
            EnableHeadersVisualStyles = false,
            RowTemplate = { Height = 32 }
        };

        // 表头美化：清爽现代浅灰底，深灰字
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 249);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(51, 65, 85);
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
        _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        _grid.ColumnHeadersHeight = 36;

        // 单元格与选中高亮优化：柔和浅天蓝 (#e0f2fe)，文字保持深色 (#0f172a)，告别生硬深蓝
        _grid.DefaultCellStyle.ForeColor = Color.FromArgb(30, 41, 59);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(224, 242, 254);
        _grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(15, 23, 42);

        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 252);
        _grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = Color.FromArgb(224, 242, 254);
        _grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = Color.FromArgb(15, 23, 42);

        _grid.Columns.AddRange(_djidCol, _descCol, _printerCol, _behaviorCol, _duplexCol, _orientationCol, _copiesCol);
        _grid.DataError += (_, e) => e.ThrowException = false;

        // ── 4. 底部面板 (状态提示 + 圆角保存/取消按钮) ──────────────────────────
        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            BackColor = Color.White,
            Padding = new Padding(16, 10, 16, 10)
        };
        bottomPanel.Paint += (_, pe) =>
        {
            using var pen = new Pen(Color.FromArgb(226, 232, 240), 1);
            pe.Graphics.DrawLine(pen, 0, 0, bottomPanel.Width, 0);
        };

        var hintText = new Label
        {
            Text = "💡 提示：接口发起纯预览查看时永远只显示窗口不出纸；专属配置仅在打印任务到达时自动生效。",
            Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(100, 116, 139),
            Dock = DockStyle.Left,
            AutoSize = false,
            Width = 560,
            TextAlign = ContentAlignment.MiddleLeft
        };

        _okBtn = new RoundedButton
        {
            Text = "💾 保存并应用",
            Size = new Size(116, 34),
            CornerRadius = 6,
            NormalBackColor = Color.FromArgb(79, 70, 229),
            HoverBackColor = Color.FromArgb(99, 102, 241),
            PressedBackColor = Color.FromArgb(67, 56, 202),
            BorderColor = Color.Transparent,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point),
            DialogResult = DialogResult.OK
        };

        _cancelBtn = new RoundedButton
        {
            Text = "取消",
            Size = new Size(82, 34),
            CornerRadius = 6,
            NormalBackColor = Color.White,
            HoverBackColor = Color.FromArgb(241, 245, 249),
            PressedBackColor = Color.FromArgb(226, 232, 240),
            BorderColor = Color.FromArgb(203, 213, 225),
            ForeColor = Color.FromArgb(71, 85, 105),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            DialogResult = DialogResult.Cancel
        };

        var rightButtonFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false
        };
        rightButtonFlow.Controls.Add(_cancelBtn);
        rightButtonFlow.Controls.Add(_okBtn);

        bottomPanel.Controls.Add(hintText);
        bottomPanel.Controls.Add(rightButtonFlow);

        AcceptButton = _okBtn;
        CancelButton = _cancelBtn;

        // 组装控件
        Controls.Add(_grid);
        Controls.Add(toolPanel);
        Controls.Add(headerPanel);
        Controls.Add(bottomPanel);

        // ── 事件绑定 ──────────────────────────────────────────────────────
        _addBtn.Click += (_, _) => OnAddRow();
        _deleteBtn.Click += (_, _) => OnDeleteSelected();
        _clearAllBtn.Click += (_, _) => OnClearAll();
        _okBtn.Click += (_, _) => OnOk();
        Load += OnLoad;

        // 圆角和边框自绘
        Resize += (_, _) => ApplyRoundCorners();
        Paint += (_, pe) => DrawFormBorder(pe.Graphics);
        ApplyRoundCorners();
    }

    private void DragWindow(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 2, 0);
        }
    }

    private void ApplyRoundCorners()
    {
        Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width, Height, 14, 14));
        Invalidate();
    }

    private void DrawFormBorder(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(203, 213, 225), 1.5f);
        g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    private void OnLoad(object? sender, EventArgs e)
    {
        LoadFromStore();
        UpdateButtonStates();
        _grid.SelectionChanged += (_, _) => UpdateButtonStates();
    }

    private void LoadFromStore()
    {
        _grid.Rows.Clear();
        var all = _store.GetAll();
        foreach (var (djid, pref) in all.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            AddRow(pref);
        }
    }

    private void AddRow(DjidPrintPreference pref)
    {
        var idx = _grid.Rows.Add();
        var row = _grid.Rows[idx];
        row.Cells[ColDjid].Value = pref.Djid;
        row.Cells[ColDesc].Value = pref.Description;

        // 打印机
        if (!string.IsNullOrWhiteSpace(pref.PrinterName) && _installedPrinters.Any(p =>
                string.Equals(p.Name, pref.PrinterName, StringComparison.OrdinalIgnoreCase)))
        {
            row.Cells[ColPrinter].Value = _installedPrinters
                .First(p => string.Equals(p.Name, pref.PrinterName, StringComparison.OrdinalIgnoreCase)).Name;
        }
        else if (!string.IsNullOrWhiteSpace(pref.PrinterName))
        {
            if (!_printerCol.Items.Contains(pref.PrinterName))
                _printerCol.Items.Add(pref.PrinterName);
            row.Cells[ColPrinter].Value = pref.PrinterName;
            row.Cells[ColPrinter].Style.ForeColor = Color.OrangeRed;
            row.Cells[ColPrinter].ToolTipText = $"警告：打印机 \"{pref.PrinterName}\" 当前未安装在本工作站。";
        }

        // 输出行为
        row.Cells[ColBehavior].Value = pref.PrintBehavior switch
        {
            "Silent" => BehaviorSilent,
            "Preview" => BehaviorPreview,
            _ => BehaviorAuto
        };

        // 单双面
        row.Cells[ColDuplex].Value = pref.Duplex switch
        {
            "Simplex" => DuplexSimplex,
            "DuplexLong" => DuplexLong,
            "DuplexShort" => DuplexShort,
            _ => DuplexAuto
        };

        // 纸张方向
        row.Cells[ColOrientation].Value = pref.Orientation switch
        {
            "Portrait" => OrientPortrait,
            "Landscape" => OrientLandscape,
            _ => OrientAuto
        };

        // 份数
        row.Cells[ColCopies].Value = pref.Copies > 0 ? pref.Copies.ToString() : "0";
    }

    private void OnAddRow()
    {
        var defaultPrinter = _installedPrinters.FirstOrDefault(x => x.IsDefault)?.Name
                             ?? _installedPrinters.FirstOrDefault()?.Name
                             ?? string.Empty;

        var pref = new DjidPrintPreference
        {
            Djid = string.Empty,
            Description = string.Empty,
            PrinterName = defaultPrinter,
            PrintBehavior = "Auto",
            Duplex = "Auto",
            Orientation = "Auto",
            Copies = 0
        };
        AddRow(pref);
        var newIdx = _grid.Rows.Count - 1;
        _grid.ClearSelection();
        _grid.Rows[newIdx].Selected = true;
        _grid.CurrentCell = _grid.Rows[newIdx].Cells[ColDjid];
        _grid.BeginEdit(true);
    }

    private void AddOrFocusRow(string djid, string name)
    {
        foreach (DataGridViewRow r in _grid.Rows)
        {
            if (string.Equals(r.Cells[ColDjid].Value as string, djid, StringComparison.OrdinalIgnoreCase))
            {
                _grid.ClearSelection();
                r.Selected = true;
                _grid.CurrentCell = r.Cells[ColDjid];
                return;
            }
        }

        var defaultPrinter = _installedPrinters.FirstOrDefault(x => x.IsDefault)?.Name
                             ?? _installedPrinters.FirstOrDefault()?.Name
                             ?? string.Empty;

        AddRow(new DjidPrintPreference
        {
            Djid = djid,
            Description = name,
            PrinterName = defaultPrinter,
            PrintBehavior = "Auto",
            Duplex = "Auto",
            Orientation = "Auto",
            Copies = 0
        });

        var idx = _grid.Rows.Count - 1;
        _grid.ClearSelection();
        _grid.Rows[idx].Selected = true;
        _grid.CurrentCell = _grid.Rows[idx].Cells[ColPrinter];
    }

    private void AddAllCommonPresets()
    {
        foreach (var (djid, name) in CommonPresetReports)
        {
            var exists = false;
            foreach (DataGridViewRow r in _grid.Rows)
            {
                if (string.Equals(r.Cells[ColDjid].Value as string, djid, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
            if (!exists)
            {
                var defaultPrinter = _installedPrinters.FirstOrDefault(x => x.IsDefault)?.Name
                                     ?? _installedPrinters.FirstOrDefault()?.Name
                                     ?? string.Empty;

                AddRow(new DjidPrintPreference
                {
                    Djid = djid,
                    Description = name,
                    PrinterName = defaultPrinter,
                    PrintBehavior = "Auto",
                    Duplex = "Auto",
                    Orientation = "Auto",
                    Copies = 0
                });
            }
        }
    }

    private void OnDeleteSelected()
    {
        var toDelete = _grid.SelectedRows.Cast<DataGridViewRow>()
            .OrderByDescending(r => r.Index)
            .ToList();
        foreach (var row in toDelete)
            _grid.Rows.Remove(row);
    }

    private void OnClearAll()
    {
        if (MessageBox.Show(
                "确定要清空全部单据的打印配置吗？\r\n此操作将删除所有单据的个性化打印机绑定与输出设置。",
                "确认清空",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes)
        {
            _grid.Rows.Clear();
        }
    }

    private void OnOk()
    {
        if (_grid.IsCurrentCellInEditMode)
            _grid.EndEdit();

        var list = new List<DjidPrintPreference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (DataGridViewRow row in _grid.Rows)
        {
            var djid = (row.Cells[ColDjid].Value as string ?? string.Empty).Trim();
            var desc = (row.Cells[ColDesc].Value as string ?? string.Empty).Trim();
            var printer = (row.Cells[ColPrinter].Value as string ?? string.Empty).Trim();
            var behaviorRaw = row.Cells[ColBehavior].Value as string;
            var duplexRaw = row.Cells[ColDuplex].Value as string;
            var orientRaw = row.Cells[ColOrientation].Value as string;
            var copiesRaw = (row.Cells[ColCopies].Value as string ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(djid) && string.IsNullOrWhiteSpace(printer))
                continue; // 忽略空行

            if (string.IsNullOrWhiteSpace(djid))
            {
                errors.Add($"第 {row.Index + 1} 行：单据代码 (djid) 不能为空。");
                continue;
            }

            if (!seen.Add(djid))
            {
                errors.Add($"第 {row.Index + 1} 行：单据代码 \"{djid}\" 重复，已保留首条。");
                continue;
            }

            _ = int.TryParse(copiesRaw, out var copies);
            if (copies < 0) copies = 0;

            var behavior = behaviorRaw switch
            {
                BehaviorSilent => "Silent",
                BehaviorPreview => "Preview",
                _ => "Auto"
            };

            var duplex = duplexRaw switch
            {
                DuplexSimplex => "Simplex",
                DuplexLong => "DuplexLong",
                DuplexShort => "DuplexShort",
                _ => "Auto"
            };

            var orientation = orientRaw switch
            {
                OrientPortrait => "Portrait",
                OrientLandscape => "Landscape",
                _ => "Auto"
            };

            list.Add(new DjidPrintPreference
            {
                Djid = djid,
                Description = desc,
                PrinterName = printer,
                PrintBehavior = behavior,
                Duplex = duplex,
                Orientation = orientation,
                Copies = copies
            });
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(
                "以下单据存在校验问题，请核对：\r\n\r\n" + string.Join("\r\n", errors),
                "输入校验",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        // 保存全部配置到持久化存储
        _store.SaveAll(list);
        DialogResult = DialogResult.OK;
    }

    private void UpdateButtonStates()
    {
        _deleteBtn.Enabled = _grid.SelectedRows.Count > 0;
        _clearAllBtn.Enabled = _grid.Rows.Count > 0;
    }
}
