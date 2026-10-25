using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tourbooking.Distributed;

namespace RabbitMq.Tests;

public class SharedSessionValidationTests
{
    private sealed class Handler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("http://identity/internal/auth/session", request.RequestUri!.ToString());
            Assert.Contains("session-cookie", request.Headers.GetValues("Cookie").Single());
            return Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(new[]
            {
                new SharedSessionValidation.SessionClaim(ClaimTypes.NameIdentifier, "admin-id"),
                new SharedSessionValidation.SessionClaim(ClaimTypes.Role, "Admin")
            }) });
        }
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    [Theory]
    [InlineData(1, HttpStatusCode.OK, false)]
    [InlineData(31, HttpStatusCode.OK, true)]
    [InlineData(31, HttpStatusCode.ServiceUnavailable, false)]
    public async Task RenewalUsesIdentityAndPreservesAdminDuringOutages(int ageMinutes, HttpStatusCode status, bool renewed)
    {
        using var handler = new Handler(status);
        using var client = new HttpClient(handler);
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Services:identity"] = "http://identity", ["Services:InternalKey"] = "test-key" }).Build())
            .AddSingleton<IHttpClientFactory>(new Factory(client)).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Headers.Cookie = "session-cookie";
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "Admin") }, "Identity.Application"));
        var properties = new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow.AddMinutes(-ageMinutes) };
        var scheme = new AuthenticationScheme("Identity.Application", null, typeof(CookieAuthenticationHandler));
        var context = new CookieValidatePrincipalContext(http, scheme, new CookieAuthenticationOptions(),
            new AuthenticationTicket(principal, properties, scheme.Name));
        await SharedSessionValidation.ValidateAsync(context);
        Assert.True(context.Principal!.IsInRole("Admin"));
        Assert.Equal(renewed, context.ShouldRenew);
        Assert.Equal(ageMinutes > 30 ? 1 : 0, handler.Calls);
        if (renewed) Assert.Equal("admin-id", context.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
    }
}
