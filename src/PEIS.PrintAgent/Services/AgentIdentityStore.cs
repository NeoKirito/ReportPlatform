namespace PEIS.PrintAgent.Services;

/// <summary>
/// Supplies one stable identifier per PrintAgent installation. The identifier is deliberately independent
/// from the Windows machine name so a rename, cloned hostname correction, or network change cannot silently
/// change the server-side print target identity.
/// </summary>
public sealed class AgentIdentityStore
{
    private const string IdentityFileName = "agent-id.txt";
    private readonly string _identityDirectory;

    public AgentIdentityStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PEIS",
            "PrintAgent"))
    {
    }

    public AgentIdentityStore(string identityDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityDirectory);
        _identityDirectory = identityDirectory;
    }

    /// <summary>
    /// Uses an explicitly provisioned identifier when supplied; otherwise returns the installation GUID.
    /// Explicit identifiers exist only for a controlled migration from earlier deployments.
    /// </summary>
    public string GetOrCreate(string? configuredAgentId)
    {
        if (!string.IsNullOrWhiteSpace(configuredAgentId))
            return configuredAgentId.Trim();

        Directory.CreateDirectory(_identityDirectory);
        var path = Path.Combine(_identityDirectory, IdentityFileName);
        if (File.Exists(path))
        {
            var saved = File.ReadAllText(path).Trim();
            if (Guid.TryParse(saved, out var existing))
                return existing.ToString("N");
        }

        var created = Guid.NewGuid().ToString("N");
        var temporaryPath = Path.Combine(_identityDirectory, $"{IdentityFileName}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporaryPath, created + Environment.NewLine);
        File.Move(temporaryPath, path, overwrite: true);
        return created;
    }
}
