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
/// OpenAsync completes after WebView2 has initialized and accepted the local PDF URI; it does not wait for the user
/// to close the window.
/// </summary>
public sealed class WebView2PdfPreviewer(
    IOptions<AgentOptions> options,
    ILogger<WebView2PdfPreviewer> logger) : IPdfPreviewer
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
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
            Text = string.IsNullOrWhiteSpace(request.Title) ? "PEIS 报告预览" : request.Title,
            Width = Math.Clamp(preview.WindowWidth, 700, 2400),
            Height = Math.Clamp(preview.WindowHeight, 500, 1600),
            StartPosition = FormStartPosition.CenterScreen,
            MinimumSize = new Size(700, 500),
            TopMost = true,
            ShowInTaskbar = true
        };

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8, 6, 8, 4),
            WrapContents = false
        };
        var status = new Label
        {
            AutoSize = true,
            Text = "正在加载 PDF…",
            Padding = new Padding(8, 7, 8, 0)
        };
        toolbar.Controls.Add(status);

        if (request.PrintAsync is not null)
        {
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

            var print = new Button { Text = "打印", AutoSize = true };
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

        var webView = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(webView);
        form.Controls.Add(toolbar);
        form.Shown += async (_, _) =>
        {
            try
            {
                form.Activate();
                form.BringToFront();
                SetForegroundWindow(form.Handle);

                _ = Task.Delay(2000).ContinueWith(_ =>
                {
                    try
                    {
                        if (!form.IsDisposed && form.IsHandleCreated)
                        {
                            form.BeginInvoke(new Action(() => form.TopMost = false));
                        }
                    }
                    catch { }
                });

                if (!webView.IsHandleCreated)
                {
                    webView.CreateControl();
                }

                try
                {
                    var userDataFolder = Path.Combine(Path.GetTempPath(), "PEIS.PrintAgent", "WebView2Data");
                    Directory.CreateDirectory(userDataFolder);
                    var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
                    await webView.EnsureCoreWebView2Async(env);
                    webView.Source = new Uri(Path.GetFullPath(request.PdfPath));
                    status.Text = Path.GetFileName(request.PdfPath);
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
                            UseShellExecute = true
                        });
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
