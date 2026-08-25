using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace PEIS.Report.Api.Printing;

public sealed class InternalApiAuthorization(
    IHostEnvironment environment,
    IOptions<InternalApiSecurityOptions> options)
{
    public bool IsAuthorized(HttpContext context)
    {
        var configured = options.Value.AccessToken;
        if (string.IsNullOrWhiteSpace(configured))
            return !environment.IsProduction() && options.Value.AllowInsecureDevelopment;

        var supplied = context.Request.Headers[InternalApiSecurityOptions.HeaderName].ToString();
        var expectedBytes = Encoding.UTF8.GetBytes(configured);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}

/// <summary>
/// Generates download URLs that are bound to the registered AgentId and expire quickly. The agent does not hold the
/// signing key; possession of an artifact GUID alone is insufficient to retrieve a PDF.
/// </summary>
public sealed class ArtifactDownloadAuthorizer(
    IHostEnvironment environment,
    IOptions<ArtifactAccessOptions> options)
{
    public string CreateDownloadPath(Guid artifactId, string agentId)
    {
        var key = RequireKey();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, options.Value.DownloadLifetimeMinutes)).ToUnixTimeSeconds();
        var signature = Sign(key, artifactId, agentId, expiresAt);
        return $"/api/print/artifacts/{artifactId:N}?agentId={Uri.EscapeDataString(agentId)}&expires={expiresAt}&signature={Uri.EscapeDataString(signature)}";
    }

    public bool IsAuthorized(Guid artifactId, string? agentId, long expiresAt, string? signature)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(signature) || expiresAt < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return false;

        var key = options.Value.SigningKey;
        if (string.IsNullOrWhiteSpace(key))
            return !environment.IsProduction() && options.Value.AllowInsecureDevelopment;

        var expected = Sign(key, artifactId, agentId, expiresAt);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(signature);
        return expectedBytes.Length == suppliedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    public bool IsProductionReady => !environment.IsProduction() || !string.IsNullOrWhiteSpace(options.Value.SigningKey);

    private string RequireKey()
    {
        var key = options.Value.SigningKey;
        if (!string.IsNullOrWhiteSpace(key)) return key;
        if (!environment.IsProduction() && options.Value.AllowInsecureDevelopment) return "development-insecure-artifact-key";
        throw new InvalidOperationException("ArtifactAccess:SigningKey must be configured for secure print artifact delivery.");
    }

    private static string Sign(string key, Guid artifactId, string agentId, long expiresAt)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var payload = Encoding.UTF8.GetBytes($"{artifactId:N}\n{agentId}\n{expiresAt}");
        return Convert.ToBase64String(hmac.ComputeHash(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
