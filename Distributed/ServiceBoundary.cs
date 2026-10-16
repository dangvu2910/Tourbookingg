using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.ActionConstraints;

namespace Tourbooking.Distributed;

// The same route map is used by the gateway and MVC discovery, including POST actions.
public static class ServiceBoundary
{
    public static string Owner(string controller, string action) => controller.ToLowerInvariant() switch
    {
        "tours" => "catalog",
        "catalogapi" => "catalog",
        "bookingapi" or "notifications" => "booking",
        "identityapi" => "identity",
        "account" => action.ToLowerInvariant() switch
        {
            "bookings" or "cancelbooking" or "reschedulebooking" or "reviews" or "votereview" => "booking",
            _ => "identity"
        },
        "home" => action.ToLowerInvariant() switch
        {
            "booking" or "payment" or "confirmpayment" or "contact" or "contactdetails" or "postreply" => "booking",
            _ => "catalog"
        },
        "admin" => action.ToLowerInvariant() switch
        {
            "users" or "exportusers" or "edituser" or "deleteuser" or "createuser" => "identity",
            "tours" or "exporttoursexcel" => "catalog",
            _ => "booking"
        },
        _ => "catalog"
    };
}

public sealed class ServiceBoundaryConvention(string service) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers)
        {
            foreach (var action in controller.Actions)
                if (ServiceBoundary.Owner(controller.ControllerName, action.ActionName) != service)
                    foreach (var selector in action.Selectors)
                        selector.ActionConstraints.Add(new ForeignServiceConstraint());
        }
    }

    // Retain route metadata so Razor can generate links to actions owned by another service.
    private sealed class ForeignServiceConstraint : IActionConstraint
    {
        public int Order => 0;
        public bool Accept(ActionConstraintContext context) => false;
    }
}
