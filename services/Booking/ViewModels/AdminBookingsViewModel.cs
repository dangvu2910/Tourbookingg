namespace Tourbooking.ViewModels;

public class AdminBookingsViewModel
{
    public int TotalBookings { get; set; }
    public string Revenue { get; set; } = string.Empty;
    public int ActiveTours { get; set; }
    public string AverageGroupSize { get; set; } = string.Empty;
    public List<AdminBookingRow> RecentBookings { get; set; } = new();
    public List<string> Statuses { get; set; } = new();
    public string? SelectedStatus { get; set; }
    public int CurrentPage { get; set; }
    public int TotalPages { get; set; }
    public int PageSize { get; set; }
}

public record AdminBookingRow(
    int BookingId,
    string CustomerInitials,
    string CustomerName,
    string CustomerEmail,
    string TourName,
    string Date,
    int Quantity,
    string TotalPrice,
    string Status);
