namespace Tourbooking.Catalog.Reservations;

public sealed class SeatReservation
{
    public Guid Id { get; set; }
    public int TourId { get; set; }
    public string UserId { get; set; } = "";
    public DateTime TravelDate { get; set; }
    public int GuestCount { get; set; }
    public string Status { get; set; } = "Held";
    public DateTime ExpiresAt { get; set; }
    public decimal UnitPrice { get; set; }
    public string TourName { get; set; } = "";
    public string TourLocation { get; set; } = "";
    public TimeOnly DepartureTime { get; set; }
    public TimeOnly ReturnTime { get; set; }
}
