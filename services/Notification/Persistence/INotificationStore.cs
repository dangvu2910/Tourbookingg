using Tourbooking.Distributed;

namespace Tourbooking.Notifications;

public interface INotificationStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<bool> IsReadyAsync(CancellationToken ct = default);
    Task<bool> StoreAsync(IntegrationEvent evt, CancellationToken ct);
    Task<List<NotificationRecord>> ListAsync(string? userId, bool admin, CancellationToken ct);
}
