using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Web.WebView2.WinForms;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PEIS.PrintAgent.Previewing;

/// <summary>
/// Opens each PDF on its own STA UI thread so the existing Generic Host and print queues remain unchanged.
/// Forces the window to the very top layer of the desktop (SW_RESTORE + HWND_TOPMOST + SetForegroundWindow)
/// so operators immediately see the preview and print options.
/// </summary>
public sealed class WebView2PdfPreviewer(
    IOptions<AgentOptions> options,
    ILogger<WebView2PdfPreviewer> logger) : IPdfPreviewer
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int SW_RESTORE = 9;

    public static void ForceForegroundWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        try
        {
            ShowWindow(hWnd, SW_RESTORE);
            ShowWindow(hWnd, 5); // SW_SHOW
            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

            var foregroundWnd = GetForegroundWindow();
            var foregroundThreadId = GetWindowThreadProcessId(foregroundWnd, IntPtr.Zero);
            var currentThreadId = GetCurrentThreadId();

            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                AttachThreadInput(currentThreadId, foregroundThreadId, true);
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
            else
            {
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }

            keybd_event(0, 0, 0, 0);
            SetForegroundWindow(hWnd);
            BringWindowToTop(hWnd);
            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SwitchToThisWindow(hWnd, true);
        }
        catch
        {
            // Best effort window activation
        }
    }

    public Task OpenAsync(PdfPreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!options.Value.Preview.Enabled)
            throw new InvalidOperationException("Desktop PDF preview is disabled by Agent:Preview:Enabled.");
        if (!File.Exists(request.PdfPath))
            throw new FileNotFoundException("The downloaded PDF no longer exists.", request.PdfPath);

        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = CreateForm(request, opened);
                Application.Run(form);
            }
            catch (Exception ex)
            {
                opened.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = $"PEIS PDF Preview {Path.GetFileName(request.PdfPath)}"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        cancellationToken.Register(() => opened.TrySetCanceled(cancellationToken));
        return opened.Task;
    }

    private Form CreateForm(PdfPreviewRequest request, TaskCompletionSource opened)
    {
        var preview = options.Value.Preview;
        var form = new Form
        {
            Text = string.IsNullOrWhiteSpace(request.Title) ? "PEIS 报告预览与打印" : request.Title,
            Width = Math.Clamp(preview.WindowWidth, 700, 2400),
            Height = Math.Clamp(preview.WindowHeight, 500, 1600),
            StartPosition = FormStartPosition.CenterScreen,
            WindowState = FormWindowState.Normal,
            MinimumSize = new Size(700, 500),
            TopMost = true,
            ShowInTaskbar = true
        };

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8, 6, 8, 4),
            WrapContents = false
        };

        var status = new Label
        {
            AutoSize = true,
            Text = "正在加载 PDF…",
            Padding = new Padding(8, 8, 8, 0)
        };
        toolbar.Controls.Add(status);

        if (request.PrintAsync is not null)
        {
            var printerLabel = new Label
            {
                Text = "目标打印机:",
                AutoSize = true,
                Padding = new Padding(6, 8, 2, 0)
            };
            toolbar.Controls.Add(printerLabel);

            var printer = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 320
            };
            foreach (var name in request.PrinterNames ?? []) printer.Items.Add(name);
            if (printer.Items.Count > 0)
            {
                var preferred = Enumerable.Range(0, printer.Items.Count)
                    .FirstOrDefault(index => string.Equals(
                        printer.Items[index]?.ToString(),
                        request.SelectedPrinterName,
                        StringComparison.OrdinalIgnoreCase));
                printer.SelectedIndex = preferred;
            }
            toolbar.Controls.Add(printer);

            var print = new Button { Text = "立即打印", AutoSize = true, Font = new Font(Control.DefaultFont, FontStyle.Bold) };
            print.Enabled = printer.Items.Count > 0;
            print.Click += async (_, _) =>
            {
                if (printer.SelectedItem is not string selectedPrinter || string.IsNullOrWhiteSpace(selectedPrinter))
                {
                    MessageBox.Show(form, "请选择打印机。", "打印机", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                print.Enabled = false;
                printer.Enabled = false;
                status.Text = "正在提交打印…";
                try
                {
                    await request.PrintAsync(selectedPrinter);
                    status.Text = $"已提交到本机打印队列：{selectedPrinter}";
                }
                catch (Exception ex)
                {
                    status.Text = "打印失败";
                    logger.LogError(ex, "Preview confirmation print failed for {PdfPath}", request.PdfPath);
                    MessageBox.Show(form, ex.Message, "打印失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    print.Enabled = true;
                    printer.Enabled = true;
                }
            };
            toolbar.Controls.Add(print);
        }

        var topMostCheck = new CheckBox
        {
            Text = "保持置顶",
            Checked = true,
            AutoSize = true,
            Padding = new Padding(12, 6, 8, 0)
        };
        topMostCheck.CheckedChanged += (_, _) =>
        {
            form.TopMost = topMostCheck.Checked;
            if (topMostCheck.Checked)
            {
                ForceForegroundWindow(form.Handle);
            }
        };
        toolbar.Controls.Add(topMostCheck);

        var webView = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(toolbar);
        form.Controls.Add(webView);
        toolbar.BringToFront();

        form.Shown += async (_, _) =>
        {
            try
            {
                form.WindowState = FormWindowState.Normal;
                form.BringToFront();
                form.Activate();
                ForceForegroundWindow(form.Handle);

                _ = webView.Handle;
                if (!webView.IsHandleCreated)
                {
                    webView.CreateControl();
                }

                try
                {
                    var userDataFolder = Path.Combine(Path.GetTempPath(), "PEIS.PrintAgent", "WebView2Data", Environment.ProcessId.ToString());
                    Directory.CreateDirectory(userDataFolder);
                    var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
                    if (form.IsDisposed || !form.IsHandleCreated || webView.IsDisposed || !webView.IsHandleCreated) return;
                    await webView.EnsureCoreWebView2Async(env);
                    if (form.IsDisposed || webView.IsDisposed) return;
                    webView.Source = new Uri(Path.GetFullPath(request.PdfPath));
                    status.Text = Path.GetFileName(request.PdfPath);
                    ForceForegroundWindow(form.Handle);
                    opened.TrySetResult();
                }
                catch (Exception webViewEx)
                {
                    logger.LogWarning(webViewEx, "Embedded WebView2 initialization failed for {PdfPath}; falling back to system default viewer.", request.PdfPath);
                    try
                    {
                        status.Text = $"已调起系统预览：{Path.GetFileName(request.PdfPath)}";
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = Path.GetFullPath(request.PdfPath),
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Normal
                        });
                        ForceForegroundWindow(form.Handle);
                        opened.TrySetResult();
                    }
                    catch (Exception fallbackEx)
                    {
                        status.Text = "PDF 预览组件启动失败";
                        logger.LogError(fallbackEx, "Fallback system viewer also failed for {PdfPath}", request.PdfPath);
                        opened.TrySetException(new InvalidOperationException(
                            "无法启动 PDF 预览，请检查系统 PDF 关联程序或 Edge 运行库。", webViewEx));
                    }
                }
            }
            catch (Exception ex)
            {
                status.Text = "PDF 预览窗口异常";
                logger.LogError(ex, "Form_Shown uncaught error for {PdfPath}", request.PdfPath);
                opened.TrySetException(ex);
            }
        };

        form.FormClosed += (_, _) => webView.Dispose();
        return form;
    }
}
