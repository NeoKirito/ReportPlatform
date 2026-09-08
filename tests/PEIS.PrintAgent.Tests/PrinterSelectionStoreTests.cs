using PEIS.PrintAgent.Services;
using PEIS.Report.Contracts;
using Xunit;

namespace PEIS.PrintAgent.Tests;

public sealed class PrinterSelectionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "peis-printer-selection-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Selection_is_persisted_and_reused_by_djid_until_cleared()
    {
        var store = new PrinterSelectionStore(_root);
        store.Save("jktjbbd", "Printer A");

        Assert.Equal("Printer A", new PrinterSelectionStore(_root).Get("JKTJBBD"));

        new PrinterSelectionStore(_root).Clear();
        Assert.Null(new PrinterSelectionStore(_root).Get("jktjbbd"));
    }

    [Fact]
    public void Resolver_reuses_remembered_printer_and_discards_removed_printer()
    {
        var store = new PrinterSelectionStore(_root);
        var resolver = new DeliveryPrinterResolver(store);
        var installed = new[]
        {
            new PrinterDescriptor("Printer A", true),
            new PrinterDescriptor("Printer B", false)
        };
        store.Save("report-1", "Printer B");

        Assert.Equal("Printer B", resolver.Resolve("report-1", null, installed, null, false));

        var afterRemoval = new[] { new PrinterDescriptor("Printer A", true) };
        Assert.Null(resolver.Resolve("report-1", null, afterRemoval, null, false));
        Assert.Null(store.Get("report-1"));
    }

    [Fact]
    public void Silent_first_use_remembers_configured_or_windows_default_printer()
    {
        var store = new PrinterSelectionStore(_root);
        var resolver = new DeliveryPrinterResolver(store);
        var installed = new[]
        {
            new PrinterDescriptor("Windows Default", true),
            new PrinterDescriptor("Configured", false)
        };

        Assert.Equal("Configured", resolver.Resolve("report-a", null, installed, "Configured", true));
        Assert.Equal("Configured", store.Get("report-a"));
        Assert.Equal("Windows Default", resolver.Resolve("report-b", null, installed, "Missing", true));
        Assert.Equal("Windows Default", store.Get("report-b"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
