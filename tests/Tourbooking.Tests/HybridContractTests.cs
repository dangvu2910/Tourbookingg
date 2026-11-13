using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Tourbooking.Distributed;
using Tourbooking.Models;

namespace Tourbooking.Tests;

public class HybridContractTests
{
    [Fact]
    public async Task MalformedBackendJsonReturnsSafeErrorEnvelope()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/catalog";
        context.Response.Body = new MemoryStream();
        var middleware = new ApiContractMiddleware(async http =>
        {
            http.Response.ContentType = "application/json";
            await http.Response.WriteAsync("{private_backend_payload");
        }, NullLogger<ApiContractMiddleware>.Instance);
        await middleware.InvokeAsync(context);
        context.Response.Body.Position=0;
        using var json=await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(500,context.Response.StatusCode);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("INTERNAL_ERROR",json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null,json.RootElement.GetProperty("error").GetProperty("details").ValueKind);
    }

    [Theory]
    [InlineData(405,"METHOD_NOT_ALLOWED")]
    [InlineData(429,"RATE_LIMITED")]
    public async Task ClientStatusDoesNotBecomeInternalError(int status,string code)
    {
        var context=new DefaultHttpContext();
        context.Request.Path="/api/v1/catalog";
        context.Response.Body=new MemoryStream();
        await new ApiContractMiddleware(http => { http.Response.StatusCode=status; return Task.CompletedTask; },
            NullLogger<ApiContractMiddleware>.Instance).InvokeAsync(context);
        context.Response.Body.Position=0;
        using var json=await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(status,context.Response.StatusCode);
        Assert.Equal(code,json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
    [Fact]
    public void MongoChatExcludesSqlChatTablesWithoutChangingSqliteFallbackModel()
    {
        ApplicationDbContext Context(bool mongo) => new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer("Server=localhost;Database=UnusedTestDatabase;Integrated Security=true").Options,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["Service:Name"]="customer", ["Chat:Provider"]=mongo ? "MongoDB" : "SQLite" }).Build());
        using var mongo = Context(true);
        using var sql = Context(false);
        var script = DatabaseInitializer.GenerateSchemaScript(mongo);
        Assert.DoesNotContain("CREATE TABLE [ContactInquiries]", script);
        Assert.DoesNotContain("CREATE TABLE [ContactInquiryReplies]", script);
        // Reviews are owned by the identity backend, not customer.
        Assert.DoesNotContain("CREATE TABLE [TourReviews]", script);
        Assert.Contains("CREATE TABLE [ContactInquiries]", DatabaseInitializer.GenerateSchemaScript(sql));
        Assert.NotSame(mongo.Model, sql.Model);
    }
    [Theory]
    [InlineData("identity")]
    [InlineData("catalog")]
    [InlineData("booking")]
    [InlineData("customer")]
    public void SqlSchemaOnlyCreatesOwnedAndRequiredProjectionTables(string service)
    {
        using var db = SqlContext(service);
        var script = DatabaseInitializer.GenerateSchemaScript(db);
        foreach (var entity in db.Model.GetEntityTypes())
        {
            var create = "CREATE TABLE [" + entity.GetTableName() + "]";
            Assert.Equal(ServiceData.RequiresTable(service, entity.GetTableName()!), script.Contains(create));
        }
        if (service != "identity") Assert.DoesNotContain("CREATE TABLE [AspNetUserTokens]", script);
        if (service == "booking") Assert.DoesNotContain("CREATE TABLE [AspNetUserRoles]", script); // Account export is owned by Identity.
    }
    [Fact]
    public void SharedDatabaseMapsOwnerAndReadModelsToSeparateSchemas()
    {
        ApplicationDbContext Context(string service) => new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer("Server=localhost;Database=TourBookingDB;Integrated Security=true;TrustServerCertificate=true").Options,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Service:Name"] = service, ["Database:Schema"] = service }).Build());
        using var booking = Context("booking");
        using var catalog = Context("catalog");
        Assert.Equal("booking", booking.Model.FindEntityType(typeof(Booking))!.GetSchema());
        Assert.Equal("booking_read", booking.Model.FindEntityType(typeof(Tour))!.GetSchema());
        Assert.Equal("catalog", catalog.Model.FindEntityType(typeof(Tour))!.GetSchema());
        var script = DatabaseInitializer.GenerateSchemaScript(booking);
        Assert.Contains("CREATE TABLE [booking].[Bookings]", script);
        Assert.Contains("CREATE TABLE [booking_read].[Tours]", script);
        Assert.DoesNotContain("CONSTRAINT [FK_Bookings_Tours", script);
    }
    [Fact]
    public void MovedModulesUseTheirNewBackendSchemas()
    {
        ApplicationDbContext Context(string service) => new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer("Server=localhost;Database=TourBookingDB;Integrated Security=true;TrustServerCertificate=true").Options,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Service:Name"] = service, ["Database:Schema"] = service }).Build());
        using var identity = Context("identity");
        using var catalog = Context("catalog");
        using var customer = Context("customer");
        Assert.Equal("identity", identity.Model.FindEntityType(typeof(TourReview))!.GetSchema());
        Assert.Equal("identity", identity.Model.FindEntityType(typeof(TourReviewVote))!.GetSchema());
        Assert.Equal("catalog_read", catalog.Model.FindEntityType(typeof(TourReview))!.GetSchema());
        Assert.Equal("catalog_read", catalog.Model.FindEntityType(typeof(Payment))!.GetSchema());
        Assert.DoesNotContain("CREATE TABLE [customer].[TourReviews]", DatabaseInitializer.GenerateSchemaScript(customer));
    }
    private static ApplicationDbContext SqlContext(string service) => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(
            "Server=localhost;Database=UnusedTestDatabase;Integrated Security=true;TrustServerCertificate=True").Options,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Service:Name"] = service }).Build());

    [Fact]
    public void SqlReadModelsHaveNoIdentityColumnsOrForeignOwnerConstraints()
    {
        using var booking = SqlContext("booking");
        var schema = DatabaseInitializer.GenerateSchemaScript(booking);
        Assert.DoesNotContain("CONSTRAINT [FK_Bookings_Tours", schema);
        Assert.DoesNotContain("CONSTRAINT [FK_Payments_AspNetUsers", schema);
        Assert.DoesNotContain("CONSTRAINT [FK_AspNetUserRoles_AspNetUsers", schema);
        Assert.DoesNotContain("CONSTRAINT [FK_AspNetUserRoles_AspNetRoles", schema);
        Assert.Contains("CONSTRAINT [FK_Payments_Bookings", schema);
        Assert.Equal(ValueGenerated.Never, booking.Model.FindEntityType(typeof(Tour))!.FindProperty(nameof(Tour.TourId))!.ValueGenerated);
        Assert.Equal("rowversion", booking.Model.FindEntityType(typeof(Booking))!.FindProperty(nameof(Booking.Version))!.GetColumnType());
        using var catalog = SqlContext("catalog");
        Assert.Equal(ValueGenerated.OnAdd, catalog.Model.FindEntityType(typeof(Tour))!.FindProperty(nameof(Tour.TourId))!.ValueGenerated);
        Assert.Equal("varbinary(8)", catalog.Model.FindEntityType(typeof(Booking))!.FindProperty(nameof(Booking.Version))!.GetColumnType());
    }

    [Fact]
    public async Task SqlOwnerGuardRejectsWritesBeforeOpeningForeignConnection()
    {
        await using var db = SqlContext("booking");
        db.Tours.Add(new Tour { Name = "Forbidden" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    public async Task VersionedApiUsesEnvelopeSnakeCaseAndCorrelation(int status, bool success)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/catalog";
        var correlation = Guid.NewGuid();
        context.Request.Headers["X-Correlation-ID"] = correlation.ToString();
        context.Response.Body = new MemoryStream();
        var middleware = new ApiContractMiddleware(async http =>
        {
            http.Response.StatusCode = status;
            http.Response.ContentType = "application/json";
            await http.Response.WriteAsync("[{\"tourId\":12,\"createdAt\":\"2026-11-04T00:00:00Z\"}]");
        }, NullLogger<ApiContractMiddleware>.Instance);
        await middleware.InvokeAsync(context);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(success, json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(correlation.ToString("N"), json.RootElement.GetProperty("meta").GetProperty("correlation_id").GetString());
        if (success) Assert.Equal(12, json.RootElement.GetProperty("data")[0].GetProperty("tour_id").GetInt32());
        else Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task LegacyApiKeepsItsResponseShape()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/tours";
        context.Response.Body = new MemoryStream();
        await new ApiContractMiddleware(http => http.Response.WriteAsync("[{\"tourId\":1}]"),
            NullLogger<ApiContractMiddleware>.Instance).InvokeAsync(context);
        context.Response.Body.Position = 0;
        Assert.Equal("[{\"tourId\":1}]", await new StreamReader(context.Response.Body).ReadToEndAsync());
    }
}
