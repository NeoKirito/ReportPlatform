using System.Drawing;
using System.Windows.Forms;
using PEIS.Report.Contracts;

namespace PEIS.PrintAgent.Setup;

public sealed class SetupForm : Form
{
    private readonly AgentInstallerConfig _config;
    private readonly ProgressBar _progressBar;
    private readonly Label _statusLabel;
    private readonly Label _serverLabel;
    private readonly Button _actionButton;
    private readonly System.Windows.Forms.Timer _autoCloseTimer;
    private bool _hasStarted;

    public SetupForm(AgentInstallerConfig config)
    {
        _config = config;

        Text = "PEIS 医疗体检打印助手 - 一键安装";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 240);
        BackColor = Color.FromArgb(248, 249, 250);
        TopMost = true;

        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "app.ico");
            if (File.Exists(iconPath))
            {
                Icon = new Icon(iconPath);
            }
        }
        catch { }

        // 顶部横幅容器
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 70,
            BackColor = Color.White
        };
        headerPanel.Paint += (s, e) =>
        {
            using var pen = new Pen(Color.FromArgb(222, 226, 230));
            e.Graphics.DrawLine(pen, 0, headerPanel.Height - 1, headerPanel.Width, headerPanel.Height - 1);
        };

        var titleLabel = new Label
        {
            Text = "PEIS 医疗体检打印助手",
            Font = new Font("Microsoft YaHei", 13, FontStyle.Bold),
            ForeColor = Color.FromArgb(33, 37, 41),
            Location = new Point(20, 14),
            AutoSize = true
        };

        _serverLabel = new Label
        {
            Text = $"服务器连接: {_config.ServerUrl}",
            Font = new Font("Microsoft YaHei", 9, FontStyle.Regular),
            ForeColor = Color.FromArgb(108, 117, 125),
            Location = new Point(22, 42),
            AutoSize = true
        };

        headerPanel.Controls.Add(titleLabel);
        headerPanel.Controls.Add(_serverLabel);

        // 主体区域
        _statusLabel = new Label
        {
            Text = "准备安装...",
            Font = new Font("Microsoft YaHei", 9.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(73, 80, 87),
            Location = new Point(20, 95),
            Size = new Size(480, 24)
        };

        _progressBar = new ProgressBar
        {
            Location = new Point(20, 125),
            Size = new Size(480, 22),
            Minimum = 0,
            Maximum = 100,
            Value = 5,
            Style = ProgressBarStyle.Continuous
        };

        _actionButton = new Button
        {
            Text = "取消",
            Font = new Font("Microsoft YaHei", 9, FontStyle.Regular),
            Location = new Point(410, 175),
            Size = new Size(90, 32),
            Enabled = false,
            FlatStyle = FlatStyle.System
        };
        _actionButton.Click += (s, e) => Application.Exit();

        Controls.Add(headerPanel);
        Controls.Add(_statusLabel);
        Controls.Add(_progressBar);
        Controls.Add(_actionButton);

        _autoCloseTimer = new System.Windows.Forms.Timer { Interval = 1600 };
        _autoCloseTimer.Tick += (s, e) =>
        {
            _autoCloseTimer.Stop();
            Application.Exit();
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_hasStarted) return;
        _hasStarted = true;

        Task.Run(() =>
        {
            try
            {
                AgentInstallerService.ExecuteInstall(_config, progress: (pct, msg) =>
                {
                    if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke(() =>
                        {
                            _progressBar.Value = Math.Clamp(pct, 0, 100);
                            _statusLabel.Text = msg;
                        });
                    }
                });

                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(() =>
                    {
                        _progressBar.Value = 100;
                        _statusLabel.Text = "✅ 安装成功！打印助手已启动并在托盘运行。";
                        _statusLabel.ForeColor = Color.FromArgb(25, 135, 84);
                        _actionButton.Text = "完成";
                        _actionButton.Enabled = true;
                        _autoCloseTimer.Start();
                    });
                }
            }
            catch (Exception ex)
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(() =>
                    {
                        _statusLabel.Text = $"❌ 安装失败: {ex.Message}";
                        _statusLabel.ForeColor = Color.FromArgb(220, 53, 69);
                        _actionButton.Text = "关闭";
                        _actionButton.Enabled = true;
                    });
                }
            }
        });
    }
}
