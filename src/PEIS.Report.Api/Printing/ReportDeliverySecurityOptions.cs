using System.Security.Cryptography;
using System.Text;

namespace PEIS.Report.Api.Printing;

/// <summary>
/// Protects the server-to-server PDF upload endpoint. Blank preserves local pilot compatibility only.
/// </summary>
public sealed class ReportDeliverySecurityOptions
{
    public string? UploadToken { get; set; }
    public int ArtifactLifetimeMinutes { get; set; } = 120;
    public long MaxUploadBytes { get; set; } = 100 * 1024 * 1024;

    public bool IsUploadAuthorized(string? suppliedToken)
    {
        if (string.IsNullOrWhiteSpace(UploadToken)) return true;
        if (string.IsNullOrWhiteSpace(suppliedToken)) return false;
        var expected = Encoding.UTF8.GetBytes(UploadToken);
        var supplied = Encoding.UTF8.GetBytes(suppliedToken);
        return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}
