using System.Net;
using PEIS.Report.Engine;
using Xunit;

namespace PEIS.Report.Engine.Tests;

public sealed class ImageResolverTests
{
    [Fact]
    public async Task Failed_image_is_negative_cached_for_followup_reports()
    {
        var handler = new CountingFailureHandler();
        using var http = new HttpClient(handler);
        var resolver = new ImageResolver(http, new ImageResolutionOptions
        {
            TimeoutSeconds = 1,
            FailureCacheSeconds = 300,
            MaxConcurrentFetches = 1,
            MaxCachedItems = 8
        });
        var source = new Uri("http://unavailable.example/photo.jpg");

        var first = await resolver.ResolveAsync([source], CancellationToken.None);
        var second = await resolver.ResolveAsync([source], CancellationToken.None);

        Assert.Equal(1, first.FailureCount);
        Assert.Equal(1, second.FailureCount);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class CountingFailureHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
