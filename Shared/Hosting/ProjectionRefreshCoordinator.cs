using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tourbooking.Distributed;

// One bounded set of owner entries per backend; coalesce concurrent refreshes and avoid unchanged DB rewrites.
public sealed class ProjectionRefreshCoordinator(TimeProvider clock)
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public DateTimeOffset NextRefresh;
        public string? Hash;
    }
    private readonly ConcurrentDictionary<string, Entry> entries = new();
    public async Task RefreshAsync(string owner, Func<CancellationToken, Task<List<TableSnapshot>>> fetch,
        Func<List<TableSnapshot>, CancellationToken, Task> apply, CancellationToken ct)
    {
        var entry = entries.GetOrAdd(owner, _ => new());
        await entry.Gate.WaitAsync(ct);
        try
        {
            if (clock.GetUtcNow() < entry.NextRefresh) return;
            var tables = await fetch(ct);
            var normalized = tables.OrderBy(t => t.Table).Select(t => new
            {
                t.Table, Rows = t.Rows.Select(row => JsonSerializer.Serialize(row.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value)))
                    .OrderBy(row => row).ToArray()
            });
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized))));
            if (hash != entry.Hash)
            {
                await apply(tables, ct);
                entry.Hash = hash;
            }
            entry.NextRefresh = clock.GetUtcNow().AddSeconds(10);
        }
        finally { entry.Gate.Release(); }
    }
}
