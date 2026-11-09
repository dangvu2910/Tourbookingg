using Microsoft.EntityFrameworkCore;
using Tourbooking.Contracts;
using Tourbooking.Models;

namespace Tourbooking.Distributed;

// Durable saga state survives restarts; HTTP calls never run inside a local SQL transaction.
public sealed class BookingSeatReconciler(IServiceScopeFactory scopes, ILogger<BookingSeatReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await ReconcileOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Reservation reconciliation unavailable: {ErrorType}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
        }
    }

    public async Task ReconcileOnceAsync(CancellationToken ct)
    {
        using var queryScope = scopes.CreateScope();
        var queryDb = queryScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var bookingIds = await queryDb.Bookings.AsNoTracking().Where(b => b.SeatReservationToRelease != null || b.SeatReservationId != null &&
            (b.SeatReservationState == "Held" || b.Status == "Cancelled" && b.SeatReservationState != "Released"))
            .OrderBy(b => b.CreatedAt).ThenBy(b => b.BookingId)
            .Select(b => b.BookingId).Take(20).ToListAsync(ct);
        foreach (var bookingId in bookingIds)
        {
            // Isolate tracked state and failures so one unavailable reservation cannot block the batch.
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var client = scope.ServiceProvider.GetRequiredService<ISeatReservationClient>();
            var booking = await db.Bookings.SingleOrDefaultAsync(b => b.BookingId == bookingId, ct);
            if (booking == null) continue;
            try
            {
                if (booking.Status == "Cancelled")
                {
                    if (booking.SeatReservationId is { } current) await ReleaseIfPresentAsync(client, current, ct);
                    if (booking.SeatReservationToRelease is { } previous) await ReleaseIfPresentAsync(client, previous, ct);
                    booking.SeatReservationState = "Released";
                    booking.SeatReservationToRelease = null;
                    booking.SeatPreviousTravelDate = null;
                }
                else
                {
                    if (booking.SeatReservationId is { } id && booking.SeatReservationState == "Held")
                    {
                        try { booking.SeatReservationState = (await client.ConfirmAsync(id, ct)).Status; }
                        catch (ReservationException ex) when (ex.Code is "RESERVATION_EXPIRED" or "RESOURCE_NOT_FOUND")
                        {
                            await CompensateAsync(db, booking, ct);
                        }
                    }
                    // Keep the previous reservation until the replacement is confirmed.
                    if (booking.SeatReservationState == "Confirmed" && booking.SeatReservationToRelease is { } previous)
                    {
                        await ReleaseIfPresentAsync(client, previous, ct);
                        booking.SeatReservationToRelease = null;
                        booking.SeatPreviousTravelDate = null;
                        await RestorePaymentStatusAsync(db, booking, ct);
                    }
                }
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                ex is (HttpRequestException or TaskCanceledException or DbUpdateConcurrencyException or ReservationException))
            {
                logger.LogWarning("Reservation reconciliation will retry for booking {BookingId}: {ErrorType}", booking.BookingId, ex.GetType().Name);
            }
        }
    }

    private static async Task CompensateAsync(ApplicationDbContext db, Booking booking, CancellationToken ct)
    {
        var payments = await db.Payments.Where(p => p.BookingId == booking.BookingId).ToListAsync(ct);
        if (booking.SeatReservationToRelease is { } previous && booking.SeatPreviousTravelDate is { } previousDate)
        {
            booking.SeatReservationId = previous;
            booking.SeatReservationToRelease = null;
            booking.TravelDate = previousDate;
            booking.SeatPreviousTravelDate = null;
            booking.SeatReservationState = "Confirmed";
            RestorePaymentStatus(booking, payments);
            booking.RescheduleNote = "Giữ chỗ cho lịch mới hết hạn; đã giữ nguyên lịch cũ.";
            return;
        }
        booking.Status = "Cancelled";
        booking.CancelledAt = DateTime.UtcNow;
        booking.CancelReason = "Giữ chỗ hết hạn trước khi xác nhận.";
        booking.SeatReservationState = "Released";
        foreach (var payment in payments) payment.Cancel();
    }

    private static async Task RestorePaymentStatusAsync(ApplicationDbContext db, Booking booking, CancellationToken ct)
    {
        var payments = await db.Payments.Where(p => p.BookingId == booking.BookingId).ToListAsync(ct);
        RestorePaymentStatus(booking, payments);
    }

    private static void RestorePaymentStatus(Booking booking, List<Payment> payments)
    {
        var latest = payments.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.PaymentId).FirstOrDefault();
        booking.Status = latest?.Status switch
        {
            "Paid" => "Confirmed",
            "Processing" => "Processing",
            _ => "PendingConfirmation"
        };
    }

    private static async Task ReleaseIfPresentAsync(ISeatReservationClient client, Guid id, CancellationToken ct)
    {
        try { await client.ReleaseAsync(id, ct); }
        catch (ReservationException ex) when (ex.Code == "RESOURCE_NOT_FOUND") { }
    }
}
