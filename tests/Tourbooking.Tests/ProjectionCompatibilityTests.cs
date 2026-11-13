using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Tourbooking.Distributed;
using Tourbooking.Models;

namespace Tourbooking.Tests;

public class ProjectionCompatibilityTests
{
    [Fact]
    public async Task OldSnapshotsDefaultNewNullableFieldsButMissingRequiredFieldsRollback()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tourbooking-projections", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        ApplicationDbContext Context(string owner) => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={Path.Combine(folder, owner + ".db")};Foreign Keys=False").Options,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Service:Name"] = owner }).Build());
        try
        {
            await using var source = Context("booking");
            await source.Database.EnsureCreatedAsync();
            source.Bookings.Add(new Booking
            {
                TourId = 1, UserId = "user", FullName = "Test", Email = "test@example.test",
                PhoneNumber = "0901234567", GuestCount = 2, TravelDate = DateTime.UtcNow.AddDays(30), TotalPrice = 100000
            });
            await source.SaveChangesAsync();
            var snapshots = await ProjectionStore.ExportAsync(source, "booking");
            var row = Assert.Single(Assert.Single(snapshots).Rows);
            foreach (var field in new[] { "TourNameSnapshot", "TourLocationSnapshot", "DepartureTimeSnapshot", "ReturnTimeSnapshot" })
                row.Remove(field);
            await using var target = Context("catalog");
            await target.Database.EnsureCreatedAsync();
            await ProjectionStore.ReplaceAsync(target, snapshots);
            Assert.Null((await target.Bookings.AsNoTracking().SingleAsync()).TourNameSnapshot);
            row.Remove("GuestCount");
            await Assert.ThrowsAsync<InvalidOperationException>(() => ProjectionStore.ReplaceAsync(target, snapshots));
            Assert.Equal(2, (await target.Bookings.AsNoTracking().SingleAsync()).GuestCount);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }
}
