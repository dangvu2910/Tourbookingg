using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Tourbooking.Distributed;
using Tourbooking.Models;

namespace Tourbooking.Tests;

public class ReviewFixTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("/Account/Login")]
    [InlineData("/api/v1/auth/login")]
    [InlineData("/api/identity/login")]
    public async Task LoginLimiterRejectsBurstButSeparatesCallersAndAllowsReads(string path)
    {
        using var limiter = RequestRateLimits.Create(loginLimit: 2);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        using var first = await limiter.AcquireAsync(context);
        using var second = await limiter.AcquireAsync(context);
        context.Request.Path = path == "/api/v1/auth/login" ? "/api/identity/login" : "/api/v1/auth/login";
        using var rejected = await limiter.AcquireAsync(context);
        Assert.True(first.IsAcquired && second.IsAcquired);
        Assert.False(rejected.IsAcquired);
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.2");
        using var differentCaller = await limiter.AcquireAsync(context);
        Assert.True(differentCaller.IsAcquired);
        context.Request.Method = "GET";
        using var read = await limiter.AcquireAsync(context);
        Assert.True(read.IsAcquired);
    }

    [Fact]
    public async Task RefreshCoalescesRequestsAndSkipsUnchangedSnapshots()
    {
        var clock = new Clock();
        var coordinator = new ProjectionRefreshCoordinator(clock);
        var fetched = 0;
        var written = 0;
        var rows = new List<TableSnapshot> { new("Tours", [new() { ["TourId"] = JsonSerializer.SerializeToElement(1) }]) };
        Task<List<TableSnapshot>> Fetch(CancellationToken _) { fetched++; return Task.FromResult(rows); }
        Task Apply(List<TableSnapshot> _, CancellationToken ct) { written++; return Task.CompletedTask; }
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => coordinator.RefreshAsync("catalog", Fetch, Apply, default)));
        Assert.Equal(1, fetched);
        Assert.Equal(1, written);
        clock.Now = clock.Now.AddSeconds(11);
        await coordinator.RefreshAsync("catalog", Fetch, Apply, default);
        Assert.Equal(2, fetched);
        Assert.Equal(1, written);
        rows[0].Rows[0]["TourId"] = JsonSerializer.SerializeToElement(2);
        clock.Now = clock.Now.AddSeconds(11);
        await coordinator.RefreshAsync("catalog", Fetch, Apply, default);
        Assert.Equal(2, written);
    }

    [Fact]
    public async Task UserProjectionOnlyCarriesSummaryAndClearsOlderPrivateFields()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var identity = new ApplicationDbContext(options);
        await identity.Database.EnsureCreatedAsync();
        identity.Users.Add(new ApplicationUser { Id = "user", UserName = "name", Email = "u@example.test", FullName = "Test",
            Address = "private-address", PasswordHash = "private-hash", SecurityStamp = "private-stamp", LockoutEnabled = true });
        await identity.SaveChangesAsync();
        var tables = await ProjectionStore.ExportAsync(identity, "identity");
        var row = Assert.Single(Assert.Single(tables).Rows);
        Assert.False(row.ContainsKey("Address"));
        Assert.False(row.ContainsKey("PasswordHash"));
        Assert.False(row.ContainsKey("SecurityStamp"));
        // An older producer cannot reintroduce private data through the write adapter.
        row["Address"] = JsonSerializer.SerializeToElement("old-producer-address");
        await using var consumer = new ApplicationDbContext(options, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string,string?> { ["Service:Name"] = "booking" }).Build());
        await ProjectionStore.ReplaceAsync(consumer, tables);
        var saved = await consumer.Users.AsNoTracking().SingleAsync();
        Assert.Equal("Test", saved.FullName);
        Assert.Null(saved.Address);
        Assert.Null(saved.PasswordHash);
        Assert.False(saved.LockoutEnabled);
    }
}
