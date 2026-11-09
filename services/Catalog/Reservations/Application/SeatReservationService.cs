using Tourbooking.Contracts;

namespace Tourbooking.Catalog.Reservations;

public interface ISeatReservationRepository : ISeatReservationClient { }

public sealed class SeatReservationService(ISeatReservationRepository repository, TimeProvider clock) : ISeatReservationClient
{
    public const int Capacity = 20;
    public Task<ReservedSeats> ReserveAsync(ReserveSeats request, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty || request.TourId <= 0 || string.IsNullOrWhiteSpace(request.UserId) ||
            request.GuestCount is < 1 or > Capacity || request.TravelDate.Date <= clock.GetUtcNow().UtcDateTime.Date)
            throw new ReservationException("INVALID_REQUEST", "Thông tin giữ chỗ không hợp lệ; tối đa 20 khách cho mỗi tour/ngày.");
        return repository.ReserveAsync(request with { TravelDate = request.TravelDate.Date }, ct);
    }
    public Task<ReservedSeats> ConfirmAsync(Guid id, CancellationToken ct) => repository.ConfirmAsync(id, ct);
    public Task<ReservedSeats> ReleaseAsync(Guid id, CancellationToken ct) => repository.ReleaseAsync(id, ct);
}
