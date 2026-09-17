using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Extensions.Options;

namespace PEIS.PrintAgent.Services;

/// <summary>
/// 设置服务器 URL 的可视化弹窗。
/// 具备精致圆角、无冗余原生标题栏、带 Logo、支持拖拽移动与连通性测试。
/// </summary>
internal sealed class ServerUrlForm : Form
{
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

    private readonly IOptions<AgentOptions> _options;
    private readonly TextBox _urlTextBox;
    private readonly Label _statusLabel;
    private readonly RoundedButton _testBtn;
    private readonly RoundedButton _saveBtn;
    private readonly RoundedButton _cancelBtn;

    public ServerUrlForm(IOptions<AgentOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // 窗体属性：无原生标题栏、圆角、屏幕居中
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(520, 290);
        BackColor = Color.White;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        // ── 1. 顶部 Header (Logo + 标题 + 关闭按钮) ──────────────────────────
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 64,
            BackColor = Color.White,
            Padding = new Padding(16, 10, 16, 10)
        };
        headerPanel.MouseDown += (_, e) => DragWindow(e);

        var logoBox = new PictureBox
        {
            Size = new Size(40, 40),
            Location = new Point(16, 12),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = AppIconHelper.LoadLogoImage()
        };
        logoBox.MouseDown += (_, e) => DragWindow(e);

        var titleLabel = new Label
        {
            Text = "PEIS 打印代理 — 服务器地址设置",
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(30, 41, 59),
            Location = new Point(66, 12),
            AutoSize = true
        };
        titleLabel.MouseDown += (_, e) => DragWindow(e);

        var subTitleLabel = new Label
        {
            Text = "配置 API 服务地址，Agent 将连接到此服务器接收打印与桌面交付任务。",
            Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(100, 116, 139),
            Location = new Point(66, 36),
            AutoSize = true
        };
        subTitleLabel.MouseDown += (_, e) => DragWindow(e);

        // 关闭按钮
        var closeBtn = new Button
        {
            Text = "✕",
            Size = new Size(32, 32),
            Location = new Point(headerPanel.Width - 44, 12),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(100, 116, 139),
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand
        };
        closeBtn.FlatAppearance.BorderSize = 0;
        closeBtn.MouseEnter += (_, _) => { closeBtn.BackColor = Color.FromArgb(254, 226, 226); closeBtn.ForeColor = Color.FromArgb(220, 38, 38); };
        closeBtn.MouseLeave += (_, _) => { closeBtn.BackColor = Color.Transparent; closeBtn.ForeColor = Color.FromArgb(100, 116, 139); };
        closeBtn.Click += (_, _) => Close();

        headerPanel.Controls.Add(logoBox);
        headerPanel.Controls.Add(titleLabel);
        headerPanel.Controls.Add(subTitleLabel);
        headerPanel.Controls.Add(closeBtn);

        // ── 2. 中间内容区域 ──────────────────────────────────────────────
        var bodyPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 12, 24, 8)
        };

        var urlTitle = new Label
        {
            Text = "服务器地址 (ServerUrl)：",
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(51, 65, 85),
            Location = new Point(24, 10),
            AutoSize = true
        };

        _urlTextBox = new TextBox
        {
            Location = new Point(24, 36),
            Width = 472,
            Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Regular, GraphicsUnit.Point),
            Text = _options.Value.ServerUrl ?? "http://127.0.0.1:82"
        };

        _testBtn = new RoundedButton
        {
            Text = "🔌 测试连通性",
            Location = new Point(24, 76),
            Size = new Size(116, 32),
            CornerRadius = 6,
            NormalBackColor = Color.White,
            HoverBackColor = Color.FromArgb(241, 245, 249),
            PressedBackColor = Color.FromArgb(226, 232, 240),
            BorderColor = Color.FromArgb(203, 213, 225),
            ForeColor = Color.FromArgb(51, 65, 85)
        };
        _testBtn.Click += async (_, _) => await OnTestConnectionAsync();

        _statusLabel = new Label
        {
            Location = new Point(148, 80),
            Size = new Size(346, 26),
            Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(100, 116, 139),
            Text = "点击测试按钮验证 Agent 与服务器是否互通",
            TextAlign = ContentAlignment.MiddleLeft
        };

        bodyPanel.Controls.Add(urlTitle);
        bodyPanel.Controls.Add(_urlTextBox);
        bodyPanel.Controls.Add(_testBtn);
        bodyPanel.Controls.Add(_statusLabel);

        // ── 3. 底部操作栏 ──────────────────────────────────────────────
        var footerPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            BackColor = Color.FromArgb(248, 250, 252),
            Padding = new Padding(16, 10, 16, 10)
        };
        footerPanel.Paint += (_, pe) =>
        {
            using var pen = new Pen(Color.FromArgb(226, 232, 240), 1);
            pe.Graphics.DrawLine(pen, 0, 0, footerPanel.Width, 0);
        };

        _saveBtn = new RoundedButton
        {
            Text = "💾 保存配置",
            Size = new Size(104, 32),
            CornerRadius = 6,
            NormalBackColor = Color.FromArgb(79, 70, 229),
            HoverBackColor = Color.FromArgb(99, 102, 241),
            PressedBackColor = Color.FromArgb(67, 56, 202),
            BorderColor = Color.Transparent,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point)
        };
        _saveBtn.Click += (_, _) => OnSave();

        _cancelBtn = new RoundedButton
        {
            Text = "取消",
            Size = new Size(76, 32),
            CornerRadius = 6,
            NormalBackColor = Color.White,
            HoverBackColor = Color.FromArgb(241, 245, 249),
            PressedBackColor = Color.FromArgb(226, 232, 240),
            BorderColor = Color.FromArgb(203, 213, 225),
            ForeColor = Color.FromArgb(71, 85, 105)
        };
        _cancelBtn.Click += (_, _) => Close();

        var btnFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false
        };
        btnFlow.Controls.Add(_cancelBtn);
        btnFlow.Controls.Add(_saveBtn);
        footerPanel.Controls.Add(btnFlow);

        Controls.Add(bodyPanel);
        Controls.Add(footerPanel);
        Controls.Add(headerPanel);

        AcceptButton = _saveBtn;
        CancelButton = _cancelBtn;

        // 应用圆角与边框绘制
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

    private async Task OnTestConnectionAsync()
    {
        var url = _urlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url) || (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            _statusLabel.ForeColor = Color.FromArgb(220, 38, 38);
            _statusLabel.Text = "❌ 请输入有效的 HTTP/HTTPS 地址 (如 http://192.168.0.88:82)";
            return;
        }

        _testBtn.Enabled = false;
        _statusLabel.ForeColor = Color.FromArgb(79, 70, 229);
        _statusLabel.Text = "⏳ 正在尝试连接服务器，请稍候...";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var testUrl = $"{url.TrimEnd('/')}/BaseInfo/Report/GetReportByJson";
            var resp = await client.GetAsync(testUrl);

            _statusLabel.ForeColor = Color.FromArgb(22, 101, 52);
            _statusLabel.Text = $"✅ 连接成功！服务器响应正常 (HTTP {(int)resp.StatusCode})";
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.FromArgb(220, 38, 38);
            _statusLabel.Text = $"❌ 无法连接服务器: {ex.Message}";
        }
        finally
        {
            _testBtn.Enabled = true;
        }
    }

    private void OnSave()
    {
        var rawUrl = _urlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(rawUrl) || (!rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("请输入以 http:// 或 https:// 开头的有效服务器地址！", "输入错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var normalizedUrl = rawUrl.TrimEnd('/');
            // 更新内存中单例选项
            _options.Value.ServerUrl = normalizedUrl;

            // 持久化到配置文件
            SaveToConfigFile(normalizedUrl);

            MessageBox.Show(
                $"服务器地址已更新为：\r\n{normalizedUrl}\r\n\r\n已成功保存到配置文件。配置修改后将在下次重连或重启 Agent 时生效。",
                "保存成功",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void SaveToConfigFile(string newServerUrl)
    {
        var candidateIniPaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "config.ini"),
            Path.Combine(AppContext.BaseDirectory, "..", "config.ini"),
            Path.Combine(AppContext.BaseDirectory, "agent.ini"),
            Path.Combine(AppContext.BaseDirectory, "..", "agent.ini"),
            Path.Combine(Environment.CurrentDirectory, "config.ini"),
            Path.Combine(Environment.CurrentDirectory, "agent.ini")
        };

        var targetPath = candidateIniPaths.FirstOrDefault(File.Exists)
                         ?? Path.Combine(AppContext.BaseDirectory, "agent.ini");

        var lines = File.Exists(targetPath)
            ? File.ReadAllLines(targetPath).ToList()
            : new List<string>();

        var found = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("ServerUrl=", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"ServerUrl={newServerUrl}";
                found = true;
                break;
            }
        }

        if (!found)
        {
            lines.Add($"ServerUrl={newServerUrl}");
        }

        File.WriteAllLines(targetPath, lines, System.Text.Encoding.UTF8);
    }
}
