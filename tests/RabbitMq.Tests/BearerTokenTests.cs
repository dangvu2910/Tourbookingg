using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Tourbooking.Distributed;

namespace RabbitMq.Tests;

public class BearerTokenTests
{
    private static IConfiguration Config(string issuer = "identity-test", string audience = "api-test", char key = 'a') =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["Jwt:Issuer"] = issuer, ["Jwt:Audience"] = audience, ["Jwt:SigningKey"] = new string(key, 64) }).Build();

    [Fact]
    public void SignedTokenRetainsCallerAndRole()
    {
        var config = Config();
        var token = ApiBearerAuthentication.Issue(config,
            [new Claim(ClaimTypes.NameIdentifier, "user"), new Claim(ClaimTypes.Role, "Admin")], DateTime.UtcNow);
        var principal = new JwtSecurityTokenHandler().ValidateToken(token, ApiBearerAuthentication.Validation(config), out _);
        Assert.Equal("user", principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.True(principal.IsInRole("Admin"));
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("signature")]
    [InlineData("expired")]
    public void WrongIssuerAudienceSignatureAndExpiredTokenAreRejected(string failure)
    {
        var token = ApiBearerAuthentication.Issue(Config(), [new Claim(ClaimTypes.NameIdentifier, "user")],
            failure == "expired" ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow);
        var expected = failure switch
        {
            "issuer" => Config(issuer: "other"), "audience" => Config(audience: "other"),
            "signature" => Config(key: 'b'), _ => Config()
        };
        Assert.ThrowsAny<SecurityTokenException>(() => new JwtSecurityTokenHandler()
            .ValidateToken(token, ApiBearerAuthentication.Validation(expected), out _));
    }
}
