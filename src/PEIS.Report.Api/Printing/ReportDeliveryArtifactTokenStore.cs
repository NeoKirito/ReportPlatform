using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace PEIS.Report.Api.Printing;

public sealed class ReportDeliveryArtifactTokenStore
{
    private readonly ConcurrentDictionary<Guid, TokenEntry> _tokens = new();

    public string Create(Guid artifactId, DateTimeOffset expiresAt)
    {
        Prune();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _tokens[artifactId] = new TokenEntry(token, expiresAt);
        return token;
    }

    public bool Validate(Guid artifactId, string? suppliedToken)
    {
        Prune();
        if (string.IsNullOrWhiteSpace(suppliedToken) || !_tokens.TryGetValue(artifactId, out var entry) ||
            entry.ExpiresAt <= DateTimeOffset.UtcNow) return false;
        var expected = Encoding.ASCII.GetBytes(entry.Token);
        var supplied = Encoding.ASCII.GetBytes(suppliedToken);
        return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _tokens)
        {
            if (item.Value.ExpiresAt <= now) _tokens.TryRemove(item.Key, out _);
        }
    }

    private sealed record TokenEntry(string Token, DateTimeOffset ExpiresAt);
}
