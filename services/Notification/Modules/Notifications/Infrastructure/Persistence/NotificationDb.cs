using Microsoft.EntityFrameworkCore;
using Tourbooking.Distributed;

namespace Tourbooking.Notifications;

public sealed class NotificationDb(DbContextOptions<NotificationDb> options) : DbContext(options), INotificationStore
{
    public async Task InitializeAsync(CancellationToken ct = default) => await Database.EnsureCreatedAsync(ct);
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Database.CanConnectAsync(ct);
    public Task<List<NotificationRecord>> ListAsync(string? userId, bool admin, CancellationToken ct) =>
        Notifications.AsNoTracking().Where(n => admin || n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.EventId).Take(admin ? 200 : 100).ToListAsync(ct);
    public DbSet<NotificationRecord> Notifications => Set<NotificationRecord>();
    public DbSet<AdminNotificationSeen> AdminSeen => Set<AdminNotificationSeen>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<NotificationRecord>().HasKey(n => n.EventId);
        builder.Entity<NotificationRecord>().Property(n => n.UserId).HasMaxLength(450);
        builder.Entity<NotificationRecord>().Property(n => n.TotalPrice).HasConversion<double?>();
        builder.Entity<NotificationRecord>().HasIndex(n => new { n.UserId, n.CreatedAt });
        builder.Entity<NotificationRecord>().HasIndex(n => new { n.Type, n.CreatedAt });
        builder.Entity<AdminNotificationSeen>().HasKey(s => s.AdminId);
    }

    private IQueryable<NotificationRecord> AdminFeedQuery => Notifications.AsNoTracking().Where(n =>
        n.Type == AdminFeed.BookingCreated || n.Type == AdminFeed.ContactCreated
        || (n.Type == AdminFeed.BookingUpdated && n.Status == AdminFeed.PaymentSubmittedStatus));

    public Task<List<NotificationRecord>> ListAdminFeedAsync(int limit, CancellationToken ct) =>
        AdminFeedQuery.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.EventId)
            .Take(Math.Clamp(limit, 1, 50)).ToListAsync(ct);

    public Task<long> CountAdminFeedSinceAsync(DateTime since, CancellationToken ct) =>
        AdminFeedQuery.Where(n => n.CreatedAt > since).LongCountAsync(ct);

    public async Task<DateTime> GetOrStartAdminSeenAsync(string adminId, DateTime now, CancellationToken ct)
    {
        var seen = await AdminSeen.AsNoTracking().FirstOrDefaultAsync(s => s.AdminId == adminId, ct);
        if (seen != null) return seen.SeenAt;
        await MarkAdminSeenAsync(adminId, now, ct);
        return now;
    }

    public async Task MarkAdminSeenAsync(string adminId, DateTime seenAt, CancellationToken ct)
    {
        var seen = await AdminSeen.FirstOrDefaultAsync(s => s.AdminId == adminId, ct);
        if (seen == null) AdminSeen.Add(new AdminNotificationSeen { AdminId = adminId, SeenAt = seenAt });
        else if (seenAt > seen.SeenAt) seen.SeenAt = seenAt;
        await SaveChangesAsync(ct);
        ChangeTracker.Clear();
    }
    public async Task<bool> StoreAsync(IntegrationEvent evt, CancellationToken ct)
    {
        if (await Notifications.AnyAsync(n => n.EventId == evt.EventId, ct)) return false;
        Notifications.Add(new NotificationRecord
        {
            EventId = evt.EventId, Type = evt.Type, ResourceId = evt.ResourceId,
            UserId = evt.UserId, Email = evt.Email, CorrelationId = evt.CorrelationId,
            Message = FormatMessage(evt), CreatedAt = evt.OccurredAt,
            Status = evt.Status, TotalPrice = AdminFeed.ReadTotalPrice(evt)
        });
        try { await SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        {
            // Another consumer committed the same event. The unique key is the final deduplication guard.
            ChangeTracker.Clear();
            return false;
        }
    }

    public static string FormatMessage(IntegrationEvent evt) => (evt.Type, evt.Status) switch
    {
        ("booking.created", _) => $"Đã nhận đặt tour #{evt.ResourceId}. Đơn đang chờ admin xác nhận.",
        ("booking.updated", "Confirmed") => $"Đặt tour #{evt.ResourceId} đã được xác nhận.",
        ("booking.updated", "Cancelled") => $"Đặt tour #{evt.ResourceId} đã bị hủy.",
        ("booking.updated", "Processing") => $"Đã ghi nhận thông tin thanh toán cho đặt tour #{evt.ResourceId}; đang chờ xác nhận.",
        ("contact.created", _) => $"Đã nhận yêu cầu hỗ trợ #{evt.ResourceId}.",
        ("contact.reply.created", _) => $"Admin đã phản hồi yêu cầu hỗ trợ #{evt.ResourceId}.",
        _ => $"{evt.Type}: #{evt.ResourceId} — {evt.Status}"
    };
}
