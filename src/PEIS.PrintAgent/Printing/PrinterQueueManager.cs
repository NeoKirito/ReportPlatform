using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using PEIS.PrintAgent;
using PEIS.Report.Contracts;

namespace PEIS.PrintAgent.Printing;

/// <summary>
/// Same physical printer = sequential queue. Different printers = independent queues and therefore parallel printing.
/// This is exactly what the registration scenario needs: A4 guide and barcode can start together without
/// allowing two jobs to fight over the same printer driver/spooler.
/// </summary>
public sealed class PrinterQueueManager(
    IPrintBackend backend,
    IOptions<AgentOptions> options,
    ILogger<PrinterQueueManager> logger)
{
    private readonly ConcurrentDictionary<string, Channel<PrintWorkItem>> _queues = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask EnqueueAsync(PrintWorkItem item, CancellationToken cancellationToken)
    {
        var queue = _queues.GetOrAdd(item.Document.PrinterName, CreateQueue);
        return queue.Writer.WriteAsync(item, cancellationToken);
    }

    private Channel<PrintWorkItem> CreateQueue(string printerName)
    {
        var channel = Channel.CreateBounded<PrintWorkItem>(new BoundedChannelOptions(100)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _ = Task.Run(() => RunPrinterAsync(printerName, channel.Reader));
        return channel;
    }

    private async Task RunPrinterAsync(string printerName, ChannelReader<PrintWorkItem> reader)
    {
        await foreach (var item in reader.ReadAllAsync())
        {
            var retryCount = Math.Clamp(options.Value.PrintBackend.RetryCount, 0, 5);
            var retryDelay = TimeSpan.FromSeconds(Math.Clamp(options.Value.PrintBackend.RetryDelaySeconds, 0, 60));
            Exception? lastError = null;
            for (var attempt = 0; attempt <= retryCount; attempt++)
            {
                try
                {
                    var message = attempt == 0 ? null : $"retry {attempt} of {retryCount}";
                    await item.Status(PrintTargetStatus.Printing, message);
                    await backend.PrintAsync(item.PdfPath, printerName, item.Document.Copies, item.Document.Duplex, CancellationToken.None);
                    await item.Status(PrintTargetStatus.Completed, attempt == 0 ? null : $"completed after retry {attempt}");
                    lastError = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    logger.LogWarning(ex, "Print attempt {Attempt} of {TotalAttempts} failed on {Printer}", attempt + 1, retryCount + 1, printerName);
                    if (attempt < retryCount && retryDelay > TimeSpan.Zero)
                        await Task.Delay(retryDelay);
                }
            }

            if (lastError is not null)
            {
                logger.LogError(lastError, "Print failed on {Printer} after retries", printerName);
                await item.Status(PrintTargetStatus.Failed, lastError.Message);
            }
        }
    }
}

public sealed record PrintWorkItem(
    Guid JobId,
    PrintDocumentDispatch Document,
    string PdfPath,
    Func<PrintTargetStatus, string?, Task> Status);
