using PEIS.Report.Contracts;

namespace PEIS.PrintAgent.Services;

/// <summary>Resolves workstation-local printer choices without exposing physical printer names to the browser.</summary>
public sealed class DeliveryPrinterResolver(PrinterSelectionStore selections)
{
    public string? Resolve(
        string? djid,
        string? serverPrinterName,
        IReadOnlyList<PrinterDescriptor> installed,
        string? configuredDefault,
        bool useDefaultWhenMissing)
    {
        var remembered = selections.Get(djid);
        if (IsInstalled(remembered, installed)) return CanonicalName(remembered!, installed);
        if (remembered is not null) selections.Remove(djid);

        if (IsInstalled(serverPrinterName, installed))
        {
            var canonical = CanonicalName(serverPrinterName!, installed);
            selections.Save(djid, canonical);
            return canonical;
        }

        if (!useDefaultWhenMissing) return null;

        var fallback = IsInstalled(configuredDefault, installed)
            ? CanonicalName(configuredDefault!, installed)
            : installed.FirstOrDefault(x => x.IsDefault)?.Name;
        if (string.IsNullOrWhiteSpace(fallback)) return null;
        selections.Save(djid, fallback);
        return fallback;
    }

    public void Remember(string? djid, string printerName, IReadOnlyList<PrinterDescriptor> installed)
    {
        if (!IsInstalled(printerName, installed))
            throw new InvalidOperationException($"Printer '{printerName}' is not installed on this workstation.");
        selections.Save(djid, CanonicalName(printerName, installed));
    }

    public static string? SuggestedDefault(IReadOnlyList<PrinterDescriptor> installed, string? configuredDefault)
    {
        if (IsInstalled(configuredDefault, installed)) return CanonicalName(configuredDefault!, installed);
        return installed.FirstOrDefault(x => x.IsDefault)?.Name ?? installed.FirstOrDefault()?.Name;
    }

    private static bool IsInstalled(string? printerName, IReadOnlyList<PrinterDescriptor> installed)
        => !string.IsNullOrWhiteSpace(printerName) &&
           installed.Any(x => string.Equals(x.Name, printerName.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string CanonicalName(string printerName, IReadOnlyList<PrinterDescriptor> installed)
        => installed.First(x => string.Equals(x.Name, printerName.Trim(), StringComparison.OrdinalIgnoreCase)).Name;
}
