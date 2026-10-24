namespace Tourbooking.ViewModels;

public class AccountBookingsViewModel
{
    public string CurrentTab { get; set; } = "all";
    public int TotalBookings { get; set; }
    public int UpcomingBookings { get; set; }
    public int CompletedBookings { get; set; }
    public int CancelledBookings { get; set; }
    public List<AccountBookingRow> Bookings { get; set; } = new();
}

public record AccountBookingRow(
    int BookingId,
    string TourName,
    string TourLocation,
    string? ImageUrl,
    DateTime TravelDate,
    DateTime? OriginalTravelDate,
    DateTime? RescheduledAt,
    string? RescheduleNote,
    int GuestCount,
    decimal TotalPrice,
    string Status,
    DateTime? CancelledAt,
    string? CancelReason,
    bool CanReschedule,
    bool CanCancel,
    string MinAllowedDateIso);
