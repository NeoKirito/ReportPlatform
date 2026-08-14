namespace PEIS.Report.Engine;

/// <summary>
/// Declares the stable export profile names used by PEIS requests. FastReport-specific JPEG, font, and streaming
/// settings will be mapped inside the licensed adapter once its API and production fixtures are available.
/// </summary>
public sealed record PdfExportProfile(
    string Name,
    int JpegQuality,
    bool EmbedFonts,
    bool IntendedForPrint,
    bool IsLabel)
{
    public static readonly PdfExportProfile Legacy = new("legacy", 90, true, false, false);
    public static readonly PdfExportProfile Screen = new("screen", 78, false, false, false);
    public static readonly PdfExportProfile PrintA4 = new("print-a4", 92, true, true, false);
    public static readonly PdfExportProfile Label = new("label", 95, true, true, true);
    public static readonly PdfExportProfile Archive = new("archive", 88, true, false, false);

    public static PdfExportProfile Resolve(string? profile) => Normalize(profile) switch
    {
        "legacy" => Legacy,
        "screen" => Screen,
        "print-a4" => PrintA4,
        "label" => Label,
        "archive" => Archive,
        _ => Screen
    };

    public static string Normalize(string? profile)
    {
        var normalized = string.IsNullOrWhiteSpace(profile) ? "screen" : profile.Trim().ToLowerInvariant();
        return normalized is "legacy" or "screen" or "print-a4" or "label" or "archive" ? normalized : "screen";
    }

    public static string FileName(string? requested, string reportId)
    {
        var candidate = string.IsNullOrWhiteSpace(requested) ? $"{reportId}.pdf" : requested;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            candidate = candidate.Replace(invalid, '_');
        return candidate.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? candidate : $"{candidate}.pdf";
    }
}
