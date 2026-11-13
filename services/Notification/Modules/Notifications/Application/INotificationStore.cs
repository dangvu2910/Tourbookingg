using Tourbooking.Distributed;

namespace Tourbooking.Notifications;

public interface INotificationStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<bool> IsReadyAsync(CancellationToken ct = default);
    Task<bool> StoreAsync(IntegrationEvent evt, CancellationToken ct);
    Task<List<NotificationRecord>> ListAsync(string? userId, bool admin, CancellationToken ct);

    // Admin bell (see AdminFeed).
    Task<List<NotificationRecord>> ListAdminFeedAsync(int limit, CancellationToken ct);
    Task<long> CountAdminFeedSinceAsync(DateTime since, CancellationToken ct);
    /// <summary>Returns when the admin last opened the bell; first call starts from <paramref name="now"/>.</summary>
    Task<DateTime> GetOrStartAdminSeenAsync(string adminId, DateTime now, CancellationToken ct);
    Task MarkAdminSeenAsync(string adminId, DateTime seenAt, CancellationToken ct);
}
