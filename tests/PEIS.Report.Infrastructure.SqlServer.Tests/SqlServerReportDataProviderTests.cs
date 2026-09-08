using System.Data;
using PEIS.Report.Infrastructure.SqlServer;
using Xunit;

namespace PEIS.Report.Infrastructure.SqlServer.Tests;

public sealed class SqlServerReportDataProviderTests
{
    [Theory]
    [InlineData(new[] { "xm", "zjhm", "zjhm" }, new[] { "xm", "zjhm", "zjhm1" })]
    [InlineData(new[] { "zjhm", "zjhm", "zjhm1", "zjhm" }, new[] { "zjhm", "zjhm2", "zjhm1", "zjhm3" })]
    [InlineData(new[] { "zjhm", "ZJHM", "ZJHM1" }, new[] { "zjhm", "ZJHM2", "ZJHM1" })]
    [InlineData(new[] { "", "Column1", "", "xm" }, new[] { "Column2", "Column1", "Column3", "xm" })]
    [InlineData(new[] { "xm", "zjhm", "编号" }, new[] { "xm", "zjhm", "编号" })]
    public void Result_columns_preserve_explicit_aliases_and_all_ordinal_values(string[] source, string[] expected)
    {
        var names = SqlServerReportDataProvider.GetUniqueColumnNames(source);
        Assert.Equal(expected, names);

        var table = new DataTable();
        foreach (var name in names) table.Columns.Add(name, typeof(int));
        var values = Enumerable.Range(0, names.Length).Cast<object>().ToArray();
        table.Rows.Add(values);
        for (var ordinal = 0; ordinal < names.Length; ordinal++)
            Assert.Equal(ordinal, table.Rows[0][expected[ordinal]]);
    }
}
