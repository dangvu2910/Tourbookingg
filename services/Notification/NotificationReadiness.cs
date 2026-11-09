using System.Data.Common;
using MongoDB.Driver;

namespace Tourbooking.Notifications;

public static class NotificationReadiness
{
    public static async Task<bool> IsReadyAsync(INotificationStore store, CancellationToken ct)
    {
        try { return await store.IsReadyAsync(ct); }
        catch (Exception ex) when (ex is MongoException or DbException or TimeoutException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
