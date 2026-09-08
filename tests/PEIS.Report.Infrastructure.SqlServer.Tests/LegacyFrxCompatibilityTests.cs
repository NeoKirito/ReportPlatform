using System.Data;
using System.Xml.Linq;
using PEIS.Report.FastReport.OpenSource;
using PEIS.Report.Engine;
using PEIS.Report.Contracts;
using Xunit;

namespace PEIS.Report.Infrastructure.SqlServer.Tests;

public sealed class LegacyFrxCompatibilityTests
{
    [Fact]
    public async Task Empty_trailing_pages_are_excluded_before_total_pages_are_calculated()
    {
        const string frx = """
            <?xml version="1.0" encoding="utf-8"?>
            <Report DoublePass="true"><Dictionary>
              <TableDataSource Name="Master" ReferenceName="Master" Enabled="true"><Column Name="Value" DataType="System.String" /></TableDataSource>
              <TableDataSource Name="Master2" ReferenceName="Master2" Enabled="true"><Column Name="Value" DataType="System.String" /></TableDataSource>
            </Dictionary>
            <ReportPage Name="Page1"><DataBand Name="Data1" Width="718" Height="30" DataSource="Master"><TextObject Name="Text1" Width="200" Height="20" Text="[TotalPages]" /></DataBand></ReportPage>
            <ReportPage Name="Page2"><DataBand Name="Data2" Width="718" Height="30" DataSource="Master2" /></ReportPage>
            </Report>
            """;
        var data = new DataTable("Master");
        data.Columns.Add("Value");
        data.Rows.Add("present");
        var empty = data.Clone();
        empty.TableName = "Master2";
        var request = new ReportRenderRequest("synthetic", new Dictionary<string, System.Text.Json.JsonElement>(), "legacy", null, null);
        var definition = new ReportDefinition("synthetic", "1", "test", null, new Dictionary<string, string>(), DateTimeOffset.UtcNow, "test");
        var runtime = new OpenSourceFastReportRuntime();
        var preparation = await runtime.PrepareAsync(new FastReportRenderContext(request, definition,
            new ReportTemplate("test", "1", frx, "test"), new ReportDataSet(new Dictionary<string, DataTable> { ["Master"] = data, ["Master2"] = empty }, 1), PdfExportProfile.Legacy), CancellationToken.None);
        await using var prepared = preparation.Document;
        var report = Assert.IsType<global::FastReport.Report>(prepared.GetType().GetProperty("Report")!.GetValue(prepared));
        Assert.Equal(1, report.PreparedPages.Count);
        using var page = report.PreparedPages.GetPage(0);
        Assert.Contains(page.AllObjects.Cast<global::FastReport.Base>().OfType<global::FastReport.TextObject>(), text => text.Text == "1");
    }

    [Fact]
    public async Task Remote_pictures_use_downloaded_bytes_and_visible_failure_images_without_changing_original_columns()
    {
        const string frx = """
            <Report><Dictionary><TableDataSource Name="Master" ReferenceName="Master" Enabled="true">
              <Column Name="Photo" DataType="System.String" />
            </TableDataSource></Dictionary><ReportPage Name="Page1">
              <DataBand Name="Data1" Width="718.2" Height="100" DataSource="Master">
                <PictureObject Name="Photo1" Width="200" Height="80" DataColumn="Master.Photo" />
                <PictureObject Name="Photo2" Left="210" Width="200" Height="80" DataColumn="Master.Photo" />
              </DataBand>
            </ReportPage></Report>
            """;
        var table = new DataTable("Master");
        table.Columns.Add("Photo");
        table.Rows.Add("https://images.invalid/picture.png");
        table.Rows.Add("https://images.invalid/missing.png");
        var tables = new Dictionary<string, DataTable> { ["Master"] = table };
        var resolver = new ControlledImageResolver();
        var prepared = await ReportImagePreparation.PrepareAsync(frx, tables, resolver, CancellationToken.None);
        var document = XDocument.Parse(prepared.Template);
        var bindings = document.Descendants("PictureObject").Select(p => (string)p.Attribute("DataColumn")!).ToArray();

        Assert.Equal(2, resolver.UniqueSources);
        Assert.Equal(bindings[0], bindings[1]);
        Assert.Equal("https://images.invalid/missing.png", table.Rows[1]["Photo"]);
        Assert.Equal(typeof(string), table.Columns["Photo"]!.DataType);
        var bytesColumn = bindings[0].Split('.', 2)[1];
        Assert.Equal(ControlledImageResolver.Png, Assert.IsType<byte[]>(table.Rows[0][bytesColumn]));
        Assert.True(Assert.IsType<byte[]>(table.Rows[1][bytesColumn]).Length > 100);
        Assert.Equal(1, prepared.Batch.FailureCount);

        // Run the real FastReport binding and exporter: a source that still tries
        // to download images.invalid would make this test fail or block.
        var request = new ReportRenderRequest("synthetic", new Dictionary<string, System.Text.Json.JsonElement>(), "legacy", null, null);
        var definition = new ReportDefinition("synthetic", "1", "test", null, new Dictionary<string, string>(), DateTimeOffset.UtcNow, "test");
        var runtime = new OpenSourceFastReportRuntime();
        var result = await runtime.PrepareAsync(new FastReportRenderContext(request, definition,
            new ReportTemplate("test", "1", prepared.Template, "test"), new ReportDataSet(tables, 2), PdfExportProfile.Legacy), CancellationToken.None);
        await using var report = result.Document;
        var pdf = await runtime.ExportPdfAsync(report, PdfExportProfile.Legacy, CancellationToken.None);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf.Pdf, 0, 5));
        Assert.True(pdf.PageCount > 0);
    }

    [Fact]
    public async Task Static_http_picture_is_embedded_and_cancellation_is_preserved()
    {
        const string frx = "<Report><ReportPage Name=\"Page1\"><PictureObject Name=\"Photo\" ImageLocation=\"https://images.invalid/picture.png\" /></ReportPage></Report>";
        var result = await ReportImagePreparation.PrepareAsync(frx, new Dictionary<string, DataTable>(), new ControlledImageResolver(), CancellationToken.None);
        var picture = Assert.Single(XDocument.Parse(result.Template).Descendants("PictureObject"));
        Assert.Null(picture.Attribute("ImageLocation"));
        Assert.Equal(ControlledImageResolver.Png, Convert.FromBase64String((string)picture.Attribute("Image")!));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReportImagePreparation.PrepareAsync(
            frx, new Dictionary<string, DataTable>(), new ControlledImageResolver(), new CancellationToken(true)));
    }

    private sealed class ControlledImageResolver : IImageResolver
    {
        public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6GQAAAABJRU5ErkJggg==");
        public int UniqueSources { get; private set; }
        public Task<ImageResolveBatch> ResolveAsync(IEnumerable<Uri> sources, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var urls = sources.Select(u => u.AbsoluteUri).Distinct().ToArray();
            UniqueSources = urls.Length;
            var images = urls.Where(u => !u.EndsWith("missing.png", StringComparison.Ordinal))
                .ToDictionary(u => u, u => new ResolvedImage(u, Png, "synthetic", false, 0));
            return Task.FromResult(new ImageResolveBatch(images, 0, urls.Length - images.Count, images.Count * Png.Length, 0));
        }
    }

    [Fact]
    public void Normalize_converts_legacy_rich_objects_without_losing_report_expressions()
    {
        const string frx = """
            <?xml version="1.0" encoding="utf-8"?>
            <Report>
              <ReportPage Name="Page1">
                <DataBand Name="Data1" Width="718.2" Height="20.79">
                  <RichObject Name="Rich3" Left="8.45" Width="699.3" Height="20.79" Border.Lines="Left, Right" CanGrow="true" CanShrink="true" Text="{\rtf1\ansi\f0\fs22 [Master3.tjjy]\par}" />
                </DataBand>
              </ReportPage>
            </Report>
            """;

        var normalized = LegacyFrxCompatibility.Normalize(frx);
        var document = XDocument.Parse(normalized);
        var textObject = Assert.Single(document.Descendants("TextObject"));

        Assert.Empty(document.Descendants("RichObject"));
        Assert.Equal("Rich3", textObject.Attribute("Name")?.Value);
        Assert.Equal("[Master3.tjjy]", textObject.Attribute("Text")?.Value);
        Assert.Equal("Left, Right", textObject.Attribute("Border.Lines")?.Value);
        Assert.Equal("true", textObject.Attribute("CanGrow")?.Value);
        Assert.Equal("KaiTi, 11pt", textObject.Attribute("Font")?.Value);
    }

    [Fact]
    public void Normalize_returns_templates_without_rich_objects_unchanged()
    {
        const string frx = "<Report><ReportPage Name=\"Page1\" /></Report>";

        Assert.Same(frx, LegacyFrxCompatibility.Normalize(frx));
    }

    [Fact]
    public void Normalize_accepts_a_decoded_utf8_bom_before_legacy_rich_objects()
    {
        const string frx = "\uFEFF<Report><ReportPage Name=\"Page1\"><RichObject Name=\"Rich1\" Text=\"{\\rtf1\\ansi [Master.Value]\\par}\" /></ReportPage></Report>";

        var normalized = LegacyFrxCompatibility.Normalize(frx);
        var document = XDocument.Parse(normalized);

        Assert.Equal("[Master.Value]", Assert.Single(document.Descendants("TextObject")).Attribute("Text")?.Value);
    }

    [Fact]
    public void Normalize_disables_double_pass_when_the_template_does_not_use_total_page_values()
    {
        const string frx = "<Report DoublePass=\"true\"><ReportPage Name=\"Page1\"><TextObject Name=\"PageNumber\" Text=\"[Page]\" /></ReportPage></Report>";

        var normalized = LegacyFrxCompatibility.Normalize(frx);

        Assert.Equal("false", XDocument.Parse(normalized).Root?.Attribute("DoublePass")?.Value);
    }

    [Theory]
    [InlineData("[TotalPages]")]
    [InlineData("[PageNofM]")]
    [InlineData("[Engine.TotalPages]")]
    public void Normalize_keeps_double_pass_when_the_template_uses_total_page_values(string expression)
    {
        var frx = $"<Report DoublePass=\"true\"><ReportPage Name=\"Page1\"><TextObject Name=\"Pages\" Text=\"{expression}\" /></ReportPage></Report>";

        Assert.Same(frx, LegacyFrxCompatibility.Normalize(frx));
    }

    [Fact]
    public void ResolveImageDpi_keeps_print_profiles_high_resolution_and_reduces_legacy_raster_work()
    {
        Assert.Equal(200, OpenSourceFastReportRuntime.ResolveImageDpi(PEIS.Report.Engine.PdfExportProfile.Legacy));
        Assert.Equal(300, OpenSourceFastReportRuntime.ResolveImageDpi(PEIS.Report.Engine.PdfExportProfile.PrintA4));
        Assert.Equal(300, OpenSourceFastReportRuntime.ResolveImageDpi(PEIS.Report.Engine.PdfExportProfile.Label));
    }

    [Fact]
    public void FindPagesWhoseDataSourcesAreAllEmpty_returns_only_known_empty_pages()
    {
        const string frx = """
            <Report>
              <ReportPage Name="StaticPage"><TextObject Name="Title" Text="Static" /></ReportPage>
              <ReportPage Name="EmptyPage"><DataBand Name="Data1" DataSource="Master5" /></ReportPage>
              <ReportPage Name="PopulatedPage"><DataBand Name="Data2" DataSource="Master6" /></ReportPage>
            </Report>
            """;
        var empty = new DataTable("Master5");
        var populated = new DataTable("Master6");
        populated.Columns.Add("Value");
        populated.Rows.Add("present");

        var emptyPages = LegacyFrxCompatibility.FindPagesWhoseDataSourcesAreAllEmpty(frx, new Dictionary<string, DataTable>
        {
            ["Master5"] = empty,
            ["Master6"] = populated
        });

        Assert.Equal(new[] { "EmptyPage" }, emptyPages);
    }

    [Fact]
    public void Normalize_keeps_a_page_when_any_referenced_data_source_is_unknown()
    {
        const string frx = "<Report><ReportPage Name=\"Page1\"><DataBand Name=\"Data1\" DataSource=\"Unknown\" /></ReportPage></Report>";

        var emptyPages = LegacyFrxCompatibility.FindPagesWhoseDataSourcesAreAllEmpty(frx, new Dictionary<string, DataTable>
        {
            ["Master"] = new("Master")
        });

        Assert.Empty(emptyPages);
    }
}
