using Tourbooking.Distributed;
using Tourbooking.Notifications;

namespace Tourbooking.Tests;

public class NotificationReadinessTests
{
    private sealed class Store(Exception? error = null) : INotificationStore
    {
        public Task<bool> IsReadyAsync(CancellationToken ct = default) =>
            error == null ? Task.FromResult(true) : Task.FromException<bool>(error);
        public Task InitializeAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> StoreAsync(IntegrationEvent evt, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<NotificationRecord>> ListAsync(string? userId, bool admin, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<NotificationRecord>> ListAdminFeedAsync(int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> CountAdminFeedSinceAsync(DateTime since, CancellationToken ct) => throw new NotSupportedException();
        public Task<DateTime> GetOrStartAdminSeenAsync(string adminId, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task MarkAdminSeenAsync(string adminId, DateTime seenAt, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ReadingRemainsReadyWithoutConsumerConnection() =>
        Assert.True(await NotificationReadiness.IsReadyAsync(new Store(), default));

    [Fact]
    public async Task DatabaseTimeoutIsUnavailableInsteadOfInternalError() =>
        Assert.False(await NotificationReadiness.IsReadyAsync(new Store(new TimeoutException()), default));

    [Fact]
    public async Task RequestCancellationIsNotReportedAsDatabaseFailure()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            NotificationReadiness.IsReadyAsync(new Store(new OperationCanceledException()), cancelled.Token));
    }
}
