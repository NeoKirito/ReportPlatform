using System.Security.Cryptography;
using System.Text;

namespace PEIS.Report.Api.Printing;

/// <summary>
/// Security settings for desktop agent registration. An empty token keeps pilot deployments compatible;
/// production must provision a non-empty value outside source control.
/// </summary>
public sealed class PrintAgentSecurityOptions
{
    public string? RegistrationToken { get; set; }

    /// <summary>Development must explicitly opt in before blank registration tokens are accepted.</summary>
    public bool AllowInsecureDevelopment { get; set; }

    public bool IsRegistrationAuthorized(string? suppliedToken, bool isProduction = false)
    {
        if (string.IsNullOrWhiteSpace(RegistrationToken))
            return !isProduction && AllowInsecureDevelopment;
        if (string.IsNullOrWhiteSpace(suppliedToken))
            return false;

        var expected = Encoding.UTF8.GetBytes(RegistrationToken);
        var supplied = Encoding.UTF8.GetBytes(suppliedToken);
        return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}
