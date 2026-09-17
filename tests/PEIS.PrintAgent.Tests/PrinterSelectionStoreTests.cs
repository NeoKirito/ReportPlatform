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

    [Fact]
    public void DjidPrintPreference_stores_behavior_duplex_orientation_and_copies()
    {
        var store = new PrinterSelectionStore(_root);
        var pref = new DjidPrintPreference
        {
            Djid = "tjdjd",
            Description = "体检导检单",
            PrinterName = "HP LaserJet",
            PrintBehavior = "Silent",
            Duplex = "DuplexLong",
            Orientation = "Landscape",
            Copies = 2
        };
        store.Save(pref);

        var loaded = new PrinterSelectionStore(_root).GetPreference("tjdjd");
        Assert.NotNull(loaded);
        Assert.Equal("tjdjd", loaded.Djid);
        Assert.Equal("体检导检单", loaded.Description);
        Assert.Equal("HP LaserJet", loaded.PrinterName);
        Assert.Equal("Silent", loaded.PrintBehavior);
        Assert.Equal("DuplexLong", loaded.Duplex);
        Assert.Equal("Landscape", loaded.Orientation);
        Assert.Equal(2, loaded.Copies);
        Assert.Equal("HP LaserJet", new PrinterSelectionStore(_root).Get("tjdjd"));
    }

    [Fact]
    public void Backwards_compatible_with_old_flat_string_json()
    {
        Directory.CreateDirectory(_root);
        var jsonPath = Path.Combine(_root, PrinterSelectionStore.FileName);
        File.WriteAllText(jsonPath, "{\"jktjbbd\": \"Canon iR-ADV\", \"xmtm\": \"TSC TE244\"}");

        var store = new PrinterSelectionStore(_root);
        var jktj = store.GetPreference("jktjbbd");
        Assert.NotNull(jktj);
        Assert.Equal("Canon iR-ADV", jktj.PrinterName);
        Assert.Equal("Canon iR-ADV", store.Get("jktjbbd"));

        var xmtm = store.GetPreference("xmtm");
        Assert.NotNull(xmtm);
        Assert.Equal("TSC TE244", xmtm.PrinterName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
