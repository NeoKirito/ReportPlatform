using PEIS.Report.Api.Printing;
using Xunit;

namespace PEIS.Report.Api.Tests;

public sealed class PrintAgentSecurityOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_server_token_keeps_pilot_registration_compatible(string? token)
    {
        var options = new PrintAgentSecurityOptions { RegistrationToken = token };

        Assert.True(options.IsRegistrationAuthorized(null));
        Assert.True(options.IsRegistrationAuthorized("any-value"));
    }

    [Fact]
    public void Configured_server_token_requires_exact_agent_token()
    {
        var options = new PrintAgentSecurityOptions { RegistrationToken = "agent-registration-secret" };

        Assert.False(options.IsRegistrationAuthorized(null));
        Assert.False(options.IsRegistrationAuthorized("wrong-secret"));
        Assert.True(options.IsRegistrationAuthorized("agent-registration-secret"));
    }
}
