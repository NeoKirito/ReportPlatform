using PEIS.Report.Api.Printing;
using Xunit;

namespace PEIS.Report.Api.Tests;

public sealed class ReportDeliveryArtifactTokenStoreTests
{
    [Fact]
    public void Token_is_scoped_to_artifact_and_rejects_missing_or_modified_values()
    {
        var store = new ReportDeliveryArtifactTokenStore();
        var artifactId = Guid.NewGuid();
        var token = store.Create(artifactId, DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.True(store.Validate(artifactId, token));
        Assert.False(store.Validate(Guid.NewGuid(), token));
        Assert.False(store.Validate(artifactId, null));
        Assert.False(store.Validate(artifactId, token + "x"));
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        var store = new ReportDeliveryArtifactTokenStore();
        var artifactId = Guid.NewGuid();
        var token = store.Create(artifactId, DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.False(store.Validate(artifactId, token));
    }
}
