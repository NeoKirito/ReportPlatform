using System.Collections.Concurrent;
using System.Threading.Channels;
using PEIS.Report.Contracts;

namespace PEIS.PrintAgent.Printing;

/// <summary>
/// Same physical printer = sequential queue. Different printers = independent queues and therefore parallel printing.
/// This is exactly what the registration scenario needs: A4 guide and barcode can start together without
/// allowing two jobs to fight over the same printer driver/spooler.
/// </summary>
public sealed class PrinterQueueManager(IPrintBackend backend, ILogger<PrinterQueueManager> logger)
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
            try
            {
                await item.Status(PrintTargetStatus.Printing, null);
                await backend.PrintAsync(item.PdfPath, printerName, item.Document.Copies, item.Document.Duplex, CancellationToken.None);
                await item.Status(PrintTargetStatus.Completed, null);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Print failed on {Printer}", printerName);
                await item.Status(PrintTargetStatus.Failed, ex.Message);
            }
        }
    }
}

public sealed record PrintWorkItem(
    Guid JobId,
    PrintDocumentDispatch Document,
    string PdfPath,
    Func<PrintTargetStatus, string?, Task> Status);
