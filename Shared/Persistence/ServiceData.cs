using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Tourbooking.Distributed;

// A shared model is retained for Razor queries. Non-owned tables are read projections only.
public static class ServiceData
{
    public static readonly string[] Services = ["identity", "catalog", "booking", "payment", "review", "support", "reporting"];

    public static string Owner(string table) => table switch
    {
        "Tours" => "catalog",
        "Bookings" => "booking",
        "Payments" => "payment",
        "TourReviews" or "TourReviewVotes" => "review",
        "ContactInquiries" or "ContactInquiryReplies" => "support",
        _ when table.StartsWith("AspNet", StringComparison.Ordinal) => "identity",
        _ => ""
    };

    public static bool IsOwned(string service, IEntityType entity) =>
        service == "standalone" || Owner(entity.GetTableName()!) == service;

    public static bool Needs(string service, string owner) => service != owner && owner switch
    {
        "identity" => true,
        "catalog" => service is "booking" or "payment" or "review" or "reporting",
        "booking" => service is "catalog" or "payment" or "review" or "reporting",
        "payment" => service is "booking" or "reporting",
        "review" => service is "catalog" or "reporting",
        _ => false
    };
}
