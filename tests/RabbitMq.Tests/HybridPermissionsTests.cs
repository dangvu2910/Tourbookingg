using Microsoft.Data.SqlClient;
using MongoDB.Bson;
using MongoDB.Driver;

namespace RabbitMq.Tests;

public class HybridPermissionsTests
{
    [HybridFact]
    public async Task SqlRuntimeCannotReadForeignDatabaseOrCreateTables()
    {
        foreach (var (variable, foreignTable) in new[]
        {
            ("TEST_SQL_BOOKING", "IdentityDB.dbo.AspNetUsers"),
            ("TEST_SQL_CATALOG", "BookingDB.dbo.Bookings")
        })
        {
            await using var connection = new SqlConnection(Environment.GetEnvironmentVariable(variable)!);
            await connection.OpenAsync();
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT TOP(1) * FROM " + foreignTable;
            var denied = await Assert.ThrowsAsync<SqlException>(() => read.ExecuteScalarAsync());
            Assert.Contains(denied.Number, new[] { 229, 916 });
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            await using var ddl = connection.CreateCommand();
            ddl.Transaction = transaction;
            ddl.CommandText = "CREATE TABLE dbo.__permission_probe (Id int NOT NULL)";
            try
            {
                var noDdl = await Assert.ThrowsAsync<SqlException>(() => ddl.ExecuteNonQueryAsync());
                Assert.Equal(262, noDdl.Number);
            }
            finally { await transaction.RollbackAsync(); }
        }
    }

    [HybridFact]
    public async Task ChatCredentialCannotReadNotificationDatabase()
    {
        var client = new MongoClient(Environment.GetEnvironmentVariable("TEST_CHAT_MONGO")!);
        var denied = await Assert.ThrowsAsync<MongoCommandException>(() => client.GetDatabase("NotificationDB")
            .RunCommandAsync<BsonDocument>(new BsonDocument("listCollections", 1)));
        Assert.Equal(13, denied.Code);
    }
}
