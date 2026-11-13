using Microsoft.EntityFrameworkCore;
using Tourbooking.Contracts;

namespace Tourbooking.Catalog.Reservations;

public sealed class SqlSeatReservationRepository(ApplicationDbContext db, TimeProvider clock) : ISeatReservationRepository
{
    private async Task LockAsync(string resource, CancellationToken ct)
    {
        if (db.Database.IsSqlServer())
            await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=5000; IF @result<0 THROW 51000,'Reservation lock unavailable',1;", ct);
    }
    private static string DepartureKey(int tourId, DateTime date) => $"tour-seats:{tourId}:{date:yyyyMMdd}";
    private static ReservedSeats Snapshot(SeatReservation row) => new(row.Id, row.TourId, row.UserId, row.TravelDate,
        row.GuestCount, row.Status, row.ExpiresAt, row.UnitPrice, row.TourName, row.TourLocation, row.DepartureTime, row.ReturnTime);

    public async Task<ReservedSeats> ReserveAsync(ReserveSeats request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync("reservation:" + request.RequestId, ct);
        await LockAsync(DepartureKey(request.TourId, request.TravelDate), ct);
        var existing = await db.SeatReservations.FindAsync([request.RequestId], ct);
        if (existing != null)
        {
            if (existing.TourId != request.TourId || existing.UserId != request.UserId || existing.TravelDate != request.TravelDate || existing.GuestCount != request.GuestCount)
                throw new ReservationException("IDEMPOTENCY_CONFLICT", "Mã giữ chỗ đã dùng cho yêu cầu khác.");
            if (existing.Status == "Released" || existing.Status == "Expired" || existing.Status == "Held" && existing.ExpiresAt <= clock.GetUtcNow().UtcDateTime)
                throw new ReservationException("RESERVATION_EXPIRED", "Giữ chỗ đã hết hạn; vui lòng tạo yêu cầu mới.");
            return Snapshot(existing);
        }
        var tour = await db.Tours.AsNoTracking().SingleOrDefaultAsync(t => t.TourId == request.TourId, ct)
            ?? throw new ReservationException("RESOURCE_NOT_FOUND", "Tour không tồn tại.");
        var now = clock.GetUtcNow().UtcDateTime;
        var occupied = await db.SeatReservations.Where(r => r.TourId == request.TourId && r.TravelDate == request.TravelDate &&
            (r.Status == "Confirmed" || r.Status == "Held" && r.ExpiresAt > now)).SumAsync(r => (int?)r.GuestCount, ct) ?? 0;
        if (occupied + request.GuestCount > SeatReservationService.Capacity)
            throw new ReservationException("INSUFFICIENT_CAPACITY", "Tour không còn đủ chỗ trong ngày đã chọn.");
        var row = new SeatReservation
        {
            Id = request.RequestId, TourId = request.TourId, UserId = request.UserId, TravelDate = request.TravelDate,
            GuestCount = request.GuestCount, ExpiresAt = now.AddMinutes(15), UnitPrice = tour.Price,
            TourName = tour.Name, TourLocation = tour.Location, DepartureTime = tour.DepartureTime, ReturnTime = tour.ReturnTime
        };
        db.SeatReservations.Add(row);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Snapshot(row);
    }
    public Task<ReservedSeats> ConfirmAsync(Guid id, CancellationToken ct) => TransitionAsync(id, false, ct);
    public Task<ReservedSeats> ReleaseAsync(Guid id, CancellationToken ct) => TransitionAsync(id, true, ct);
    private async Task<ReservedSeats> TransitionAsync(Guid id, bool release, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync("reservation:" + id, ct);
        var row = await db.SeatReservations.FindAsync([id], ct)
            ?? throw new ReservationException("RESOURCE_NOT_FOUND", "Không tìm thấy giữ chỗ.");
        await LockAsync(DepartureKey(row.TourId, row.TravelDate), ct);
        if (release) row.Status = "Released";
        else if (row.Status != "Confirmed")
        {
            if (row.Status != "Held" || row.ExpiresAt <= clock.GetUtcNow().UtcDateTime)
                throw new ReservationException("RESERVATION_EXPIRED", "Giữ chỗ đã hết hạn.");
            row.Status = "Confirmed";
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Snapshot(row);
    }
}
