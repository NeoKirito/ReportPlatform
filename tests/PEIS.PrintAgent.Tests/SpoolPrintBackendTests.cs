using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PEIS.PrintAgent.Printing;
using Xunit;

namespace PEIS.PrintAgent.Tests;

public sealed class SpoolPrintBackendTests
{
    [Fact]
    public void ResolvePrintExecutable_Finds_Bundled_SumatraPDF()
    {
        var options = new AgentOptions();
        var exePath = SpoolPrintBackend.ResolvePrintExecutable(options);

        Assert.NotNull(exePath);
        Assert.True(File.Exists(exePath));
        Assert.Contains("SumatraPDF", exePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrintAsync_Throws_On_Missing_File()
    {
        var options = Options.Create(new AgentOptions());
        var backend = new SpoolPrintBackend(options, NullLogger<SpoolPrintBackend>.Instance);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            backend.PrintAsync("non_existent_file.pdf", "TestPrinter", 1, false, CancellationToken.None));
    }

    [Fact]
    public async Task PrintAsync_Throws_On_Empty_Printer()
    {
        var options = Options.Create(new AgentOptions());
        var backend = new SpoolPrintBackend(options, NullLogger<SpoolPrintBackend>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            backend.PrintAsync("test.pdf", "", 1, false, CancellationToken.None));
    }
}
