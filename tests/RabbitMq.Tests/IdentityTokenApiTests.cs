using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tourbooking.Controllers;
using Tourbooking.Distributed;
using Tourbooking.Models;

namespace RabbitMq.Tests;

public class IdentityTokenApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PasswordLoginAndLogoutUseIdentityAndRotateSecurityStamp(bool wrongPassword)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["Service:Name"] = "identity", ["Jwt:SigningKey"] = new string('t', 64) }).Build();
        var collection = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config)
            .AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connection));
        collection.AddIdentity<ApplicationUser,IdentityRole>().AddEntityFrameworkStores<ApplicationDbContext>().AddDefaultTokenProviders();
        using var provider = collection.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = "login@example.test", Email = "login@example.test" };
        Assert.True((await users.CreateAsync(user, "Test!Password123")).Succeeded);
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
        var controller = new IdentityApiController(users, scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>(), config)
        { ControllerContext = new ControllerContext { HttpContext = http } };
        var result = await controller.Login(new IdentityApiController.LoginRequest
        { Email = user.Email, Password = wrongPassword ? "Wrong!Password123" : "Test!Password123" });
        if (wrongPassword)
        {
            Assert.IsType<UnauthorizedResult>(result);
            Assert.Equal(1, await users.GetAccessFailedCountAsync(user));
            return;
        }
        var response = Assert.IsType<OkObjectResult>(result);
        var token = JsonSerializer.SerializeToElement(response.Value).GetProperty("access_token").GetString()!;
        var principal = new JwtSecurityTokenHandler().ValidateToken(token, ApiBearerAuthentication.Validation(config), out _);
        Assert.Equal(user.Id, principal.FindFirstValue(ClaimTypes.NameIdentifier));
        var oldStamp = user.SecurityStamp;
        http.User = principal;
        http.Request.Headers.Authorization = "Bearer " + token;
        Assert.IsType<NoContentResult>(await controller.LogoutToken());
        Assert.NotEqual(oldStamp, user.SecurityStamp);
        Assert.NotEqual(principal.FindFirstValue(ApiBearerAuthentication.StampClaim), ApiBearerAuthentication.HashStamp(user.SecurityStamp));
    }
}
