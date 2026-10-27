namespace Tourbooking.ViewModels;

public class AccountReviewsViewModel
{
    public List<AccountReviewRow> Reviews { get; set; } = new();
}

public record AccountReviewRow(
    int BookingId,
    int TourId,
    string TourName,
    string TourLocation,
    string? ImageUrl,
    DateTime TravelDate,
    string Status,
    bool CanReview,
    bool HasReview,
    int? Rating,
    string? Title,
    string? Content,
    DateTime? ReviewedAt);