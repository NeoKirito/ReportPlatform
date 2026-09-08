using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.Xml.Linq;
using PEIS.Report.Engine;

namespace PEIS.Report.FastReport.OpenSource;

/// <summary>
/// Resolves HTTP pictures once before FastReport's synchronous Prepare. Original
/// SQL columns and database templates remain untouched; pictures bind to additional
/// request-local columns containing downloaded bytes instead of retrying each URL.
/// </summary>
internal static class ReportImagePreparation
{
    internal sealed record Result(string Template, ImageResolveBatch Batch);
    private sealed record Binding(XElement Source, DataTable Table, DataColumn Column, XElement[] Pictures);

    private static readonly byte[] _unavailableImage = CreateUnavailableImageCore();

    public static async Task<Result> PrepareAsync(
        string template,
        IReadOnlyDictionary<string, DataTable> tables,
        IImageResolver resolver,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(template) || !template.Contains("<PictureObject", StringComparison.Ordinal))
            return new Result(template, new ImageResolveBatch(new Dictionary<string, ResolvedImage>(), 0, 0, 0, 0));

        var hasImageLocation = template.Contains("ImageLocation=\"http", StringComparison.OrdinalIgnoreCase) ||
                               template.Contains("ImageLocation='http", StringComparison.OrdinalIgnoreCase);
        var hasDataColumn = template.Contains("DataColumn=", StringComparison.OrdinalIgnoreCase);

        if (!hasImageLocation && (!hasDataColumn || !HasAnyHttpUrlsInTables(tables)))
            return new Result(template, new ImageResolveBatch(new Dictionary<string, ResolvedImage>(), 0, 0, 0, 0));

        var document = XDocument.Parse(template.TrimStart('\uFEFF', '\u0000', '\u200B'), LoadOptions.PreserveWhitespace);
        var pictures = document.Descendants("PictureObject").ToArray();
        var bindings = new List<Binding>();
        var urls = new HashSet<Uri>();
        foreach (var group in pictures.Where(p => string.IsNullOrEmpty((string?)p.Attribute("ImageSourceExpression")))
                     .GroupBy(p => (string?)p.Attribute("DataColumn")))
        {
            var path = group.Key?.Split('.', 2);
            if (path is not { Length: 2 }) continue;
            var source = document.Descendants("TableDataSource").FirstOrDefault(s =>
                string.Equals((string?)s.Attribute("Name"), path[0], StringComparison.OrdinalIgnoreCase)
                || string.Equals((string?)s.Attribute("Alias"), path[0], StringComparison.OrdinalIgnoreCase));
            if (source is null) continue;
            var tableName = (string?)source.Attribute("ReferenceName") ?? (string?)source.Attribute("Name") ?? path[0];
            if (!tables.TryGetValue(tableName, out var table) || !table.Columns.Contains(path[1])) continue;
            var column = table.Columns[path[1]]!;
            var columnUrls = table.Rows.Cast<DataRow>().Select(r => HttpUri(r[column])).OfType<Uri>().ToArray();
            if (columnUrls.Length == 0) continue;
            urls.UnionWith(columnUrls);
            bindings.Add(new Binding(source, table, column, group.ToArray()));
        }

        var locations = pictures.Where(p => string.IsNullOrEmpty((string?)p.Attribute("DataColumn"))
            && string.IsNullOrEmpty((string?)p.Attribute("ImageSourceExpression")))
            .Select(p => (Picture: p, Uri: HttpUri((string?)p.Attribute("ImageLocation"))))
            .Where(p => p.Uri is not null).ToArray();
        urls.UnionWith(locations.Select(p => p.Uri!));
        if (urls.Count == 0)
            return new Result(template, new ImageResolveBatch(new Dictionary<string, ResolvedImage>(), 0, 0, 0, 0));

        var batch = await resolver.ResolveAsync(urls, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] Resolve(Uri uri) => batch.Images.TryGetValue(uri.AbsoluteUri, out var image)
            ? image.Bytes
            : _unavailableImage;

        foreach (var binding in bindings)
        {
            var index = binding.Table.Columns.Count;
            string name;
            do { name = $"__peis_image_{index++}"; } while (binding.Table.Columns.Contains(name));
            var resolved = binding.Table.Columns.Add(name, typeof(object));
            foreach (DataRow row in binding.Table.Rows)
                row[resolved] = HttpUri(row[binding.Column]) is { } uri ? Resolve(uri) : row[binding.Column];
            binding.Source.Add(new XElement("Column", new XAttribute("Name", name), new XAttribute("DataType", "System.Object")));
            foreach (var picture in binding.Pictures)
            {
                var sourceName = ((string)picture.Attribute("DataColumn")!).Split('.', 2)[0];
                picture.SetAttributeValue("DataColumn", $"{sourceName}.{name}");
                picture.Attribute("ImageLocation")?.Remove();
            }
        }
        foreach (var location in locations)
        {
            location.Picture.Attribute("ImageLocation")?.Remove();
            location.Picture.SetAttributeValue("Image", Convert.ToBase64String(Resolve(location.Uri!)));
        }
        // LoadFromString distinguishes XML from Base64 by the XML declaration.
        return new Result("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + document.ToString(SaveOptions.DisableFormatting), batch);
    }

    private static Uri? HttpUri(object? value) => value is string text
        && Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri : null;

    private static bool HasAnyHttpUrlsInTables(IReadOnlyDictionary<string, DataTable> tables)
    {
        foreach (var table in tables.Values)
        {
            foreach (DataColumn col in table.Columns)
            {
                if (col.DataType != typeof(string)) continue;
                foreach (DataRow row in table.Rows)
                {
                    if (row[col] is string text && (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }
        }
        return false;
    }

    private static byte[] CreateUnavailableImageCore()
    {
        using var bitmap = new Bitmap(440, 100);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.WhiteSmoke);
        using var border = new Pen(Color.Firebrick, 2);
        graphics.DrawRectangle(border, 1, 1, 437, 97);
        using var font = new Font("Arial", 17, FontStyle.Bold);
        using var brush = new SolidBrush(Color.Firebrick);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString("IMAGE UNAVAILABLE", font, brush, new RectangleF(4, 4, 432, 92), format);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
