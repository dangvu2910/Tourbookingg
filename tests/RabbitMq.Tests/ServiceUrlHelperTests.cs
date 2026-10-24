using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tourbooking.Distributed;

namespace RabbitMq.Tests;

public class ServiceUrlHelperTests
{
    [Theory]
    [InlineData("Booking", "Home", "Payment", "/Home/Payment?bookingId=42")]
    [InlineData("Payment", "Home", "Index", "/Home/Index?bookingId=42")]
    [InlineData("Review", "Account", "Login", "/Account/Login?bookingId=42")]
    [InlineData("Identity", "Admin", "Dashboard", "/Admin/Dashboard?bookingId=42")]
    public void ForeignActionLinksDoNotRequireForeignControllerAssemblies(string service, string controller, string action, string expected)
    {
        using var provider = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Service:Name"] = service.ToLowerInvariant() }).Build())
            .BuildServiceProvider();
        var context = new ActionContext(new DefaultHttpContext { RequestServices = provider }, new RouteData(), new ActionDescriptor());
        var helper = new ServiceUrlHelperFactory().GetUrlHelper(context);
        Assert.Equal(expected, helper.Action(new UrlActionContext { Controller = controller, Action = action, Values = new { bookingId = 42 } }));
    }

    [Fact]
    public void ForeignLinksEscapeRouteAndQueryValuesAndKeepPathBase()
    {
        using var provider = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Service:Name"] = "booking" }).Build()).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = provider };
        http.Request.PathBase = "/tours";
        var helper = new ServiceUrlHelperFactory().GetUrlHelper(new ActionContext(http, new RouteData(), new ActionDescriptor()));
        Assert.Equal("/tours/Account/Login?returnUrl=%2FHome%2FPayment%3FbookingId%3D42", helper.Action(new UrlActionContext
        {
            Controller = "Account", Action = "Login", Values = new { returnUrl = "/Home/Payment?bookingId=42" }
        }));
    }
}
