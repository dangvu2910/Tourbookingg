using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Tourbooking.Catalog.Reservations;
using Tourbooking.Distributed;

namespace Tourbooking.Migration;

public static partial class HybridTransfer
{
    public static async Task EnableSeatReservationsAsync(string root, string backup)
    {
        Directory.CreateDirectory(backup);
        await using var connection = new SqlConnection(NativeConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))";
        var path = Path.Combine((string)(await command.ExecuteScalarAsync())!, "TourBookingDB-before-reservations-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bak");
        command.CommandText = "BACKUP DATABASE [TourBookingDB] TO DISK=@path WITH COPY_ONLY,CHECKSUM; RESTORE VERIFYONLY FROM DISK=@path WITH CHECKSUM;";
        command.Parameters.AddWithValue("path", path);
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
        await File.WriteAllTextAsync(Path.Combine(backup, "backup-receipt.json"), JsonSerializer.Serialize(new { path, restoreVerifyOnly = true }, Json));
        foreach (var backend in Databases.Keys)
        {
            await using var db = ExpressContext(backend);
            await DatabaseInitializer.InitializeAsync(db);
        }
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var booking = ExpressContext("booking");
        await using var catalog = ExpressContext("catalog");
        booking.Database.SetDbConnection(connection, contextOwnsConnection: false);
        catalog.Database.SetDbConnection(connection, contextOwnsConnection: false);
        await booking.Database.UseTransactionAsync(transaction);
        await catalog.Database.UseTransactionAsync(transaction);
        var bookings = await booking.Bookings.Where(b => b.Status != "Cancelled").ToListAsync();
        if (bookings.GroupBy(b => new { b.TourId, Date = b.TravelDate.Date }).Any(g => g.Sum(b => b.GuestCount) > 20))
            throw Stop("An existing departure already exceeds 20 guests. No booking was changed; review the legacy departure first.");
        var tours = await catalog.Tours.AsNoTracking().ToDictionaryAsync(t => t.TourId);
        var seeded = 0;
        foreach (var row in bookings.Where(b => b.SeatReservationId == null))
        {
            tours.TryGetValue(row.TourId, out var tour);
            var id = Guid.NewGuid();
            catalog.SeatReservations.Add(new SeatReservation
            {
                Id = id, TourId = row.TourId, UserId = row.UserId, TravelDate = row.TravelDate.Date,
                GuestCount = row.GuestCount, Status = "Confirmed", ExpiresAt = DateTime.UtcNow.AddMinutes(15),
                UnitPrice = tour?.Price ?? 0, TourName = row.TourNameSnapshot ?? tour?.Name ?? "Legacy tour",
                TourLocation = row.TourLocationSnapshot ?? tour?.Location ?? "",
                DepartureTime = row.DepartureTimeSnapshot ?? tour?.DepartureTime ?? new TimeOnly(8, 0),
                ReturnTime = row.ReturnTimeSnapshot ?? tour?.ReturnTime ?? new TimeOnly(18, 0)
            });
            row.SeatReservationId = id;
            row.SeatReservationState = "Confirmed";
            seeded++;
        }
        await catalog.SaveChangesAsync();
        await booking.SaveChangesAsync();
        // Clear fields outside UserSummary from existing replicas; never alter Identity's owner rows.
        command.Transaction = transaction;
        command.Parameters.Clear();
        foreach (var backend in new[] { "booking", "catalog", "customer" })
        {
            command.CommandText = $"UPDATE [{backend}_read].[AspNetUsers] SET PasswordHash=NULL, SecurityStamp=NULL, ConcurrencyStamp=NULL, Address=NULL, LockoutEnd=NULL, EmailConfirmed=0, PhoneNumberConfirmed=0, TwoFactorEnabled=0, LockoutEnabled=0, AccessFailedCount=0";
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        await File.WriteAllTextAsync(Path.Combine(backup, "reservation-enable-report.json"), JsonSerializer.Serialize(new
        { verified = true, capacity = 20, legacyBookingsReserved = seeded, privateReplicaFieldsCleared = true }, Json));
        Console.WriteLine($"Capacity 20 enabled; {seeded} existing bookings reserved; backup verified.");
    }
}
