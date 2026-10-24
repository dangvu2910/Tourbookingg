namespace Tourbooking.ViewModels;

public class PaymentPageViewModel
{
    public int BookingId { get; set; }
    public string TourName { get; set; } = string.Empty;
    public string TourLocation { get; set; } = string.Empty;
    public DateTime TravelDate { get; set; }
    public int GuestCount { get; set; }
    public decimal Amount { get; set; }

    public string PaymentMethod { get; set; } = string.Empty;

    public string BankCode { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string AddInfo { get; set; } = string.Empty;

    public string QrImageUrl { get; set; } = string.Empty;
}
