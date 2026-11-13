namespace Tourbooking.ViewModels;

public sealed class BookingTicketViewModel
{
    public int BookingId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public string CustomerPhone { get; init; } = string.Empty;
    public string TourName { get; init; } = string.Empty;
    public string TourLocation { get; init; } = string.Empty;
    public DateTime TravelDate { get; init; }
    public TimeOnly DepartureTime { get; init; }
    public TimeOnly ReturnTime { get; init; }
    public int GuestCount { get; init; }
}
