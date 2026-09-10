using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PEIS.PrintAgent.Services;

/// <summary>
/// Background STA service that hosts the Windows notification area (System Tray) icon.
/// Allows PrintAgent to run without any visible command prompt window, and gives operators
/// an easy right-click menu to view logs, inspect configuration, or exit cleanly.
/// </summary>
public sealed class TrayIconService(
    IOptions<AgentOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<TrayIconService> logger) : IHostedService
{
    private Thread? _uiThread;
    private NotifyIcon? _notifyIcon;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _uiThread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var cfg = options.Value;
                var station = string.IsNullOrWhiteSpace(cfg.StationId) || string.Equals(cfg.StationId, "AUTO", StringComparison.OrdinalIgnoreCase)
                    ? Environment.MachineName
                    : cfg.StationId;

                var contextMenu = new ContextMenuStrip();

                var titleItem = new ToolStripMenuItem("PEIS 打印代理") { Enabled = false, Font = new Font(Control.DefaultFont, FontStyle.Bold) };
                var stationItem = new ToolStripMenuItem($"工作站: {station}") { Enabled = false };
                var serverItem = new ToolStripMenuItem($"服务器: {cfg.ServerUrl}") { Enabled = false };

                var logsItem = new ToolStripMenuItem("查看运行日志", null, (_, _) => OpenLogsFolder());
                var configItem = new ToolStripMenuItem("打开配置文件", null, (_, _) => OpenConfigFile());
                var resetPrinterItem = new ToolStripMenuItem("重置打印机记忆", null, (_, _) => ResetPrinterMemory());
                var exitItem = new ToolStripMenuItem("退出打印代理", null, (_, _) =>
                {
                    logger.LogInformation("Operator requested exit from system tray.");
                    if (_notifyIcon != null) _notifyIcon.Visible = false;
                    lifetime.StopApplication();
                });

                contextMenu.Items.Add(titleItem);
                contextMenu.Items.Add(stationItem);
                contextMenu.Items.Add(serverItem);
                contextMenu.Items.Add(new ToolStripSeparator());
                contextMenu.Items.Add(logsItem);
                contextMenu.Items.Add(configItem);
                contextMenu.Items.Add(resetPrinterItem);
                contextMenu.Items.Add(new ToolStripSeparator());
                contextMenu.Items.Add(exitItem);

                _notifyIcon = new NotifyIcon
                {
                    Icon = LoadAppIcon(),
                    ContextMenuStrip = contextMenu,
                    Text = TruncateText($"PEIS 打印代理 ({station})", 63),
                    Visible = true
                };

                _notifyIcon.DoubleClick += (_, _) => OpenLogsFolder();

                _notifyIcon.ShowBalloonTip(
                    2500,
                    "PEIS 打印代理已就绪",
                    $"工作站 [{station}] 打印与预览服务正在后台运行",
                    ToolTipIcon.Info);

                Application.Run();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to initialize system tray icon.");
            }
        })
        {
            IsBackground = true,
            Name = "PEIS PrintAgent TrayIcon"
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            Application.ExitThread();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error while cleaning up tray icon.");
        }
        return Task.CompletedTask;
    }

    private void OpenLogsFolder()
    {
        try
        {
            var logsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs"));
            if (!Directory.Exists(logsDir))
            {
                logsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "logs"));
            }
            Directory.CreateDirectory(logsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{logsDir}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to open logs folder.");
        }
    }

    private void OpenConfigFile()
    {
        try
        {
            var candidateIniPaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "config.ini"),
                Path.Combine(AppContext.BaseDirectory, "..", "config.ini"),
                Path.Combine(AppContext.BaseDirectory, "agent.ini"),
                Path.Combine(AppContext.BaseDirectory, "..", "agent.ini")
            };
            var configPath = candidateIniPaths.FirstOrDefault(File.Exists);
            if (!string.IsNullOrEmpty(configPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = $"\"{configPath}\"",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to open config file.");
        }
    }

    private void ResetPrinterMemory()
    {
        try
        {
            var memoryFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PEIS.PrintAgent",
                "printer-selections.json");

            if (File.Exists(memoryFile))
            {
                File.Delete(memoryFile);
            }
            MessageBox.Show(
                "已成功重置打印机记忆。下次打印时将重新提示选择打印机。",
                "PEIS 打印代理",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reset printer memory.");
            MessageBox.Show(
                $"重置打印机记忆失败: {ex.Message}",
                "PEIS 打印代理",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
        return text[..maxLength];
    }

    /// <summary>
    /// Load app.ico from Resources directory with fallback to SystemIcons.Application.
    /// Search order: Resources/app.ico → app.ico in app base → SystemIcons.Application
    /// </summary>
    private static Icon LoadAppIcon()
    {
        var candidatePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources", "app.ico"),
            Path.Combine(AppContext.BaseDirectory, "app.ico"),
        };
        foreach (var path in candidatePaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    var icon = new Icon(path);
                    if (icon.Size.Width > 0 && icon.Size.Height > 0)
                        return icon;
                }
            }
            catch
            {
                // Icon file may be corrupted or locked, continue to next candidate
            }
        }
        return SystemIcons.Application;
    }
}
