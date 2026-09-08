using PEIS.Report.Api.Printing;
using Xunit;

namespace PEIS.Report.Api.Tests;

public sealed class ReportDeliverySecurityOptionsTests
{
    [Fact]
    public void Blank_token_keeps_existing_local_pilot_deployments_compatible()
        => Assert.True(new ReportDeliverySecurityOptions().IsUploadAuthorized(null));

    [Fact]
    public void Configured_token_rejects_missing_or_wrong_value()
    {
        var options = new ReportDeliverySecurityOptions { UploadToken = "server-secret" };
        Assert.False(options.IsUploadAuthorized(null));
        Assert.False(options.IsUploadAuthorized("wrong"));
        Assert.True(options.IsUploadAuthorized("server-secret"));
    }
}
