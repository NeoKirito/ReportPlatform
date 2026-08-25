using PEIS.Report.Api.Printing;
using Xunit;

namespace PEIS.Report.Api.Tests;

public sealed class PrintAgentSecurityOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_server_token_requires_explicit_development_opt_in(string? token)
    {
        var strict = new PrintAgentSecurityOptions { RegistrationToken = token };
        Assert.False(strict.IsRegistrationAuthorized(null));
        Assert.False(strict.IsRegistrationAuthorized("any-value"));

        var development = new PrintAgentSecurityOptions { RegistrationToken = token, AllowInsecureDevelopment = true };
        Assert.True(development.IsRegistrationAuthorized(null));
        Assert.True(development.IsRegistrationAuthorized("any-value"));
        Assert.False(development.IsRegistrationAuthorized("any-value", isProduction: true));
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
