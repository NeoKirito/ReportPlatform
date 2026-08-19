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

    public bool IsRegistrationAuthorized(string? suppliedToken)
    {
        if (string.IsNullOrWhiteSpace(RegistrationToken))
            return true;
        if (string.IsNullOrWhiteSpace(suppliedToken))
            return false;

        var expected = Encoding.UTF8.GetBytes(RegistrationToken);
        var supplied = Encoding.UTF8.GetBytes(suppliedToken);
        return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}
