using System.Drawing.Printing;
using PEIS.Report.Contracts;

namespace PEIS.PrintAgent.Services;

public sealed class PrinterCatalog
{
    public IReadOnlyList<PrinterDescriptor> GetInstalledPrinters()
    {
        var defaultName = new PrinterSettings().PrinterName;
        return PrinterSettings.InstalledPrinters.Cast<string>()
            .OrderBy(x => x)
            .Select(name => new PrinterDescriptor(name, string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }
}
