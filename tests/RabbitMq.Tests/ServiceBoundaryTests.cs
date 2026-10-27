using Microsoft.AspNetCore.Mvc;
using Tourbooking.Distributed;

namespace RabbitMq.Tests;

public class ServiceBoundaryTests
{
    [Theory]
    [InlineData("Home", "Payment", "payment")]
    [InlineData("Home", "ConfirmPayment", "payment")]
    [InlineData("Account", "Reviews", "review")]
    [InlineData("Account", "VoteReview", "review")]
    [InlineData("Home", "Contact", "support")]
    [InlineData("Home", "ContactDetails", "support")]
    [InlineData("Home", "PostReply", "support")]
    [InlineData("Admin", "Contacts", "support")]
    [InlineData("Admin", "ReplyContact", "support")]
    [InlineData("Admin", "Dashboard", "reporting")]
    [InlineData("Admin", "ExportUsers", "reporting")]
    [InlineData("Admin", "ExportToursExcel", "reporting")]
    [InlineData("Admin", "ExportBookingsExcel", "reporting")]
    [InlineData("Home", "Booking", "booking")]
    [InlineData("Admin", "UpdateBookingStatus", "booking")]
    [InlineData("Account", "Login", "identity")]
    [InlineData("Tours", "Index", "catalog")]
    public void ActionsBelongToTheirBusinessService(string controller, string action, string expected)
        => Assert.Equal(expected, ServiceBoundary.Owner(controller, action));

    [Fact]
    public void GatewayOverridesReferToExistingMvcActionsAndKnownBackends()
    {
        foreach (var (route, service) in ServiceBoundary.ActionOwners)
        {
            var parts = route.Split('/');
            Assert.Contains(service, ServiceBoundary.Services);
            Assert.Equal(2, parts.Length);
            Assert.Equal(service, ServiceBoundary.Owner(parts[0], parts[1]));
        }
        Assert.Equal(8, ServiceBoundary.Services.Distinct().Count());
    }

    [Fact]
    public void LogicalServicesMapToFivePhysicalBackends()
    {
        Assert.Equal(5, ServiceBoundary.Backends.Distinct().Count());
        Assert.Equal("booking", ServiceBoundary.BackendFor("payment"));
        Assert.Equal("booking", ServiceBoundary.BackendFor("reporting"));
        Assert.Equal("customer", ServiceBoundary.BackendFor("review"));
        Assert.Equal("customer", ServiceBoundary.BackendFor("support"));
        Assert.Equal(new[] { "booking", "payment", "reporting" }, ServiceBoundary.Owners("booking"));
        Assert.Equal(new[] { "review", "support" }, ServiceBoundary.Owners("customer"));
        Assert.True(ServiceData.Needs("booking", "catalog"));
        Assert.True(ServiceData.Needs("customer", "booking"));
    }
}
