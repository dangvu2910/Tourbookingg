using Microsoft.EntityFrameworkCore;
using Tourbooking.Distributed;

namespace Tourbooking.Notifications;

public sealed class NotificationDb(DbContextOptions<NotificationDb> options) : DbContext(options)
{
    public DbSet<NotificationRecord> Notifications => Set<NotificationRecord>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<NotificationRecord>().HasKey(n => n.EventId);
        builder.Entity<NotificationRecord>().Property(n => n.UserId).HasMaxLength(450);
        builder.Entity<NotificationRecord>().HasIndex(n => new { n.UserId, n.CreatedAt });
    }
    public async Task<bool> StoreAsync(IntegrationEvent evt, CancellationToken ct)
    {
        if (await Notifications.AnyAsync(n => n.EventId == evt.EventId, ct)) return false;
        Notifications.Add(new NotificationRecord
        {
            EventId = evt.EventId, Type = evt.Type, ResourceId = evt.ResourceId,
            UserId = evt.UserId, Email = evt.Email, CorrelationId = evt.CorrelationId,
            Message = $"{evt.Type}: #{evt.ResourceId} — {evt.Status}", CreatedAt = evt.OccurredAt
        });
        try { await SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        {
            // Another consumer committed the same event. The unique key is the final deduplication guard.
            ChangeTracker.Clear();
            return false;
        }
    }
}

