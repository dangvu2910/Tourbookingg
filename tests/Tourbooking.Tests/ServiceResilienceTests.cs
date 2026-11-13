using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;
using Tourbooking.Distributed;

namespace Tourbooking.Tests;

public class ServiceResilienceTests
{
    private sealed class FakeService(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(Interlocked.Increment(ref Calls)));
    }

    private static (HttpClient Client, FakeService Service) Create(Func<int, HttpResponseMessage> respond)
    {
        var service = new FakeService(respond);
        var services = new ServiceCollection().AddLogging();
        services.AddServiceResilience();
        services.AddHttpClient("test").ConfigurePrimaryHttpMessageHandler(() => service);
        var client = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient("test");
        return (client, service);
    }

    [Fact]
    public async Task ReadIsRetriedAfterTransientFailure()
    {
        var (client, service) = Create(call => new(call < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));

        using var response = await client.GetAsync("http://retry-read.test/internal/data");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, service.Calls);
    }

    [Fact]
    public async Task CommandIsNeverResentByTheClient()
    {
        var (client, service) = Create(_ => new(HttpStatusCode.ServiceUnavailable));

        using var response = await client.PostAsync("http://no-retry-post.test/internal/v1/reservations", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task OpenCircuitFailsFastWithoutCallingTheService()
    {
        var (client, service) = Create(_ => new(HttpStatusCode.InternalServerError));
        const string url = "http://breaker.test/internal/data";

        // Two reads with retries = 6 failed attempts, above the 4-call minimum: the circuit opens.
        for (var i = 0; i < 2; i++)
        {
            try { (await client.GetAsync(url)).Dispose(); }
            catch (HttpRequestException) { }
        }
        var callsWhenOpened = service.Calls;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));

        Assert.IsType<BrokenCircuitException>(ex.InnerException);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal(callsWhenOpened, service.Calls);
    }

    [Fact]
    public async Task EachTargetServiceHasItsOwnCircuit()
    {
        var (client, service) = Create(_ => new(HttpStatusCode.InternalServerError));
        for (var i = 0; i < 2; i++)
        {
            try { (await client.GetAsync("http://broken-identity.test/x")).Dispose(); }
            catch (HttpRequestException) { }
        }
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://broken-identity.test/x"));

        var before = service.Calls;
        using var other = await client.PostAsync("http://healthy-catalog.test/x", new StringContent("{}"));

        Assert.Equal(before + 1, service.Calls);
    }
}
