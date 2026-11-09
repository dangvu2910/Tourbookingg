using System.Net;
using System.Text.Json;

namespace RabbitMq.Tests;

public sealed class HybridFactAttribute : FactAttribute
{
    public HybridFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TEST_CHAT_MONGO") == null)
            Skip = "Requires the hybrid stack test profile.";
    }
}

public class VersionedApiIntegrationTests
{
    [HybridFact]
    public async Task KongExposesVersionedContractsAndBlocksInternalHeadersAndRoutes()
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new Uri(Environment.GetEnvironmentVariable("TOURBOOKING_URL")!) };
        client.DefaultRequestHeaders.Add("X-Service-Key", "external-spoof");
        var correlation = Guid.NewGuid().ToString("N");
        client.DefaultRequestHeaders.Add("X-Correlation-ID", correlation);
        using var response = await client.GetAsync("/api/v1/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(correlation, body.RootElement.GetProperty("meta").GetProperty("correlation_id").GetString());
        Assert.True(body.RootElement.GetProperty("data")[0].TryGetProperty("tour_id", out _));
        Assert.True(body.RootElement.GetProperty("meta").TryGetProperty("pagination", out _));
        foreach (var path in new[] { "/internal/data", "/internal/v1/data", "/service-info" })
        {
            using var denied = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
        foreach (var path in new[] { "/api/v1/auth/me", "/api/v1/bookings", "/api/v1/chat", "/api/v1/notifications" })
        {
            using var denied = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            using var error = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
            Assert.False(error.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("UNAUTHENTICATED", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }
}
