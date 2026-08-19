using PEIS.PrintAgent.Services;
using Xunit;

namespace PEIS.PrintAgent.Tests;

public sealed class AgentIdentityStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "peis-print-agent-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Blank_configuration_creates_and_reuses_a_persisted_guid()
    {
        var firstStore = new AgentIdentityStore(_root);
        var first = firstStore.GetOrCreate(null);
        var second = new AgentIdentityStore(_root).GetOrCreate(string.Empty);

        Assert.True(Guid.TryParse(first, out _));
        Assert.Equal(first, second);
    }

    [Fact]
    public void Explicit_migration_identifier_is_returned_without_overwriting_installation_identity()
    {
        var store = new AgentIdentityStore(_root);
        var persisted = store.GetOrCreate(null);

        var overrideId = store.GetOrCreate("legacy-agent-001");
        var afterOverride = new AgentIdentityStore(_root).GetOrCreate(null);

        Assert.Equal("legacy-agent-001", overrideId);
        Assert.Equal(persisted, afterOverride);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
