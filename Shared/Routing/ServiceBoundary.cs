namespace Tourbooking.Distributed;

// Public URL ownership is shared by the gateway and cross-service link generation.
public static class ServiceBoundary
{
    public static readonly string[] Services = ["identity", "catalog", "booking", "notification", "payment", "review", "support", "reporting"];
    public static readonly string[] Backends = ["identity", "catalog", "booking", "customer", "notification"];

    public static string[] Owners(string backend) => backend switch
    {
        "booking" => ["booking", "payment", "reporting"],
        "customer" => ["review", "support"],
        "identity" or "catalog" or "notification" => [backend],
        _ => []
    };

    public static string BackendFor(string service) => service switch
    {
        "payment" or "reporting" => "booking",
        "review" or "support" => "customer",
        _ => service
    };

    // These owners select actions from the logical service assemblies loaded by each backend.
    public static readonly IReadOnlyDictionary<string, string> ActionOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Account/Bookings"] = "booking", ["Account/Ticket"] = "booking",
        ["Account/CancelBooking"] = "booking", ["Account/RescheduleBooking"] = "booking",
        ["Account/Reviews"] = "review", ["Account/VoteReview"] = "review",
        ["Home/Booking"] = "booking", ["Home/Payment"] = "payment", ["Home/ConfirmPayment"] = "payment",
        ["Home/Contact"] = "support", ["Home/ContactDetails"] = "support", ["Home/PostReply"] = "support", ["Home/ContactMessages"] = "support",
        ["Admin/Users"] = "identity", ["Admin/EditUser"] = "identity", ["Admin/DeleteUser"] = "identity", ["Admin/CreateUser"] = "identity",
        ["Admin/Tours"] = "catalog", ["Admin/Contacts"] = "support", ["Admin/ReplyContact"] = "support",
        ["Admin/Dashboard"] = "reporting", ["Admin/ExportUsers"] = "reporting",
        ["Admin/ExportToursExcel"] = "reporting", ["Admin/ExportBookingsExcel"] = "reporting"
    };

    public static string Owner(string controller, string action) => ActionOwners.TryGetValue($"{controller}/{action}", out var owner) ? owner : controller.ToLowerInvariant() switch
    {
        "tours" => "catalog",
        "catalogapi" => "catalog",
        "bookingapi" or "notifications" => "booking",
        "identityapi" => "identity",
        "account" => "identity",
        "admin" => "booking",
        _ => "catalog"
    };
}
