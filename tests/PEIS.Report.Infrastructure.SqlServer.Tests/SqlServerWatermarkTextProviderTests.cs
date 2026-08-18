using Microsoft.Extensions.Options;
using PEIS.Report.Infrastructure.SqlServer;
using Xunit;

namespace PEIS.Report.Infrastructure.SqlServer.Tests;

public sealed class SqlServerWatermarkTextProviderTests
{
    [Fact]
    public async Task Missing_connections_returns_null_without_attempting_a_database_write()
    {
        using var provider = new SqlServerWatermarkTextProvider(
            Options.Create(new WatermarkDatabaseOptions()),
            Options.Create(new ReportDatabaseOptions()));

        var first = await provider.GetWatermarkTextAsync(CancellationToken.None);
        var second = await provider.GetWatermarkTextAsync(CancellationToken.None);

        Assert.Null(first);
        Assert.Null(second);
    }
}
