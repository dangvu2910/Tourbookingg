using Microsoft.AspNetCore.Mvc;
using Tourbooking.Distributed;

namespace Tourbooking.Tests;

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
    [InlineData("Admin", "ExportUsers", "identity")]
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
        Assert.Equal("catalog", ServiceBoundary.BackendFor("reporting"));
        Assert.Equal("identity", ServiceBoundary.BackendFor("review"));
        Assert.Equal("customer", ServiceBoundary.BackendFor("support"));
        Assert.Equal(new[] { "booking", "payment" }, ServiceBoundary.Owners("booking"));
        Assert.Equal(new[] { "support" }, ServiceBoundary.Owners("customer"));
        Assert.Equal(new[] { "identity", "review" }, ServiceBoundary.Owners("identity"));
        Assert.Equal(new[] { "catalog", "reporting" }, ServiceBoundary.Owners("catalog"));
        Assert.True(ServiceData.Needs("booking", "catalog"));
        // Moved modules keep reading the projections they need from their new backend.
        Assert.True(ServiceData.Needs("identity", "booking"));
        Assert.True(ServiceData.Needs("catalog", "payment"));
    }
}
