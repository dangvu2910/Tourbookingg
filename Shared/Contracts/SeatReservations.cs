namespace Tourbooking.Contracts;

public sealed record ReserveSeats(Guid RequestId, int TourId, string UserId, DateTime TravelDate, int GuestCount);
public sealed record ReservedSeats(Guid ReservationId, int TourId, string UserId, DateTime TravelDate, int GuestCount,
    string Status, DateTime ExpiresAt, decimal UnitPrice, string TourName, string TourLocation,
    TimeOnly DepartureTime, TimeOnly ReturnTime);
public interface ISeatReservationClient
{
    Task<ReservedSeats> ReserveAsync(ReserveSeats request, CancellationToken ct);
    Task<ReservedSeats> ConfirmAsync(Guid id, CancellationToken ct);
    Task<ReservedSeats> ReleaseAsync(Guid id, CancellationToken ct);
}
public sealed class ReservationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
