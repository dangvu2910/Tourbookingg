using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Tourbooking.Models;
using Tourbooking.Distributed;
using System.Diagnostics;
using System.Text.Json;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public string EventSource { get; set; } = "standalone";
    public bool EventsEnabled { get; set; }
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<Tourbooking.Catalog.Reservations.SeatReservation> SeatReservations => Set<Tourbooking.Catalog.Reservations.SeatReservation>();
    public bool ApplyingProjection { get; set; }
    public string? ServiceSchema { get; }
    public bool UsesMongoChat { get; }
    public bool RequiresSqlTable(string table) => ServiceData.RequiresTable(EventSource, table) &&
        !(UsesMongoChat && ServiceData.Owner(table) == "support");
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, IConfiguration? configuration = null)
        : base(options)
    {
        EventSource = configuration?["Service:Name"] ?? "standalone";
        EventsEnabled = configuration?.GetValue("RabbitMQ:Enabled", false) ?? false;
        ServiceSchema = configuration?["Database:Schema"];
        UsesMongoChat = configuration?["Chat:Provider"] == "MongoDB";
        if (!string.IsNullOrEmpty(ServiceSchema) && !System.Text.RegularExpressions.Regex.IsMatch(ServiceSchema, "^[a-z][a-z0-9_]{0,62}$"))
            throw new InvalidOperationException("Invalid service database schema.");
    }

    public DbSet<Tour> Tours { get; set; }
    public DbSet<Booking> Bookings { get; set; }
    public DbSet<Payment> Payments { get; set; }
    public DbSet<TourReview> TourReviews { get; set; }
    public DbSet<TourReviewVote> TourReviewVotes { get; set; }
    public DbSet<ContactInquiry> ContactInquiries { get; set; }
    public DbSet<ContactInquiryReply> ContactInquiryReplies { get; set; }
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, ServiceModelCacheKeyFactory>();
    

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<Tourbooking.Catalog.Reservations.SeatReservation>().HasKey(r => r.Id);
        builder.Entity<Tourbooking.Catalog.Reservations.SeatReservation>().Property(r => r.UserId).HasMaxLength(450);
        builder.Entity<Tourbooking.Catalog.Reservations.SeatReservation>().Property(r => r.Status).HasMaxLength(16);
        builder.Entity<Tourbooking.Catalog.Reservations.SeatReservation>().Property(r => r.UnitPrice).HasPrecision(18, 2);
        builder.Entity<Tourbooking.Catalog.Reservations.SeatReservation>().HasIndex(r => new { r.TourId, r.TravelDate, r.Status });
        builder.Entity<InboxMessage>().HasKey(x => x.Id);
        builder.Entity<OutboxMessage>().HasKey(m => m.Id);
        builder.Entity<OutboxMessage>().Property(m => m.Source).HasMaxLength(40);
        builder.Entity<OutboxMessage>().Property(m => m.Type).HasMaxLength(100);
        builder.Entity<OutboxMessage>().Property(m => m.LastError).HasMaxLength(1000);
        builder.Entity<OutboxMessage>().HasIndex(m => new { m.Source, m.PublishedAt, m.LockedUntil });
        builder.Entity<Booking>().Property(b => b.Version).IsRowVersion();
        builder.Entity<Booking>().HasIndex(b => b.RequestId).IsUnique().HasFilter("[RequestId] IS NOT NULL");
        builder.Entity<Payment>().Property(p => p.Version).IsRowVersion();

        builder.Entity<Tour>()
            .Property(t => t.Price)
            .HasPrecision(18, 2);
        builder.Entity<Booking>()
            .Property(b => b.TotalPrice)
            .HasPrecision(18, 2);
        builder.Entity<Payment>()
            .Property(p => p.Amount)
            .HasPrecision(18, 2);
        builder.Entity<Payment>()
            .HasOne(p => p.Booking)
            .WithMany()
            .HasForeignKey(p => p.BookingId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.Entity<Payment>()
            .HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.Entity<TourReview>()
            .HasIndex(r => r.BookingId)
            .IsUnique();

        builder.Entity<TourReview>()
            .HasOne(r => r.Booking)
            .WithOne()
            .HasForeignKey<TourReview>(r => r.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<TourReview>()
            .HasOne(r => r.Tour)
            .WithMany()
            .HasForeignKey(r => r.TourId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.Entity<TourReview>()
            .HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.Entity<TourReviewVote>()
            .HasIndex(v => new { v.ReviewId, v.UserId })
            .IsUnique();

        builder.Entity<TourReviewVote>()
            .HasOne(v => v.Review)
            .WithMany(r => r.Votes)
            .HasForeignKey(v => v.ReviewId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<TourReviewVote>()
            .HasOne(v => v.User)
            .WithMany()
            .HasForeignKey(v => v.UserId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.Entity<ContactInquiry>()
            .HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.Entity<ContactInquiryReply>()
            .HasOne(r => r.ContactInquiry)
            .WithMany()
            .HasForeignKey(r => r.ContactInquiryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ContactInquiryReply>()
            .HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.NoAction);

        if (Database.IsSqlServer())
        {
            foreach (var entity in builder.Model.GetEntityTypes().ToArray())
                if (!RequiresSqlTable(entity.GetTableName()!))
                    builder.Entity(entity.ClrType).ToTable(entity.GetTableName()!, table => table.ExcludeFromMigrations());
            if (!string.IsNullOrEmpty(ServiceSchema))
                foreach (var entity in builder.Model.GetEntityTypes())
                    entity.SetSchema(ServiceData.Owner(entity.GetTableName()!) == "" || ServiceData.IsOwned(EventSource, entity)
                        ? ServiceSchema : ServiceSchema + "_read");
            var utc = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
                value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
            foreach (var property in builder.Model.GetEntityTypes().SelectMany(e => e.GetProperties())
                .Where(p => (p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?))
                    && p.Name is not ("TravelDate" or "OriginalTravelDate")))
                property.SetValueConverter(utc);
            foreach (var entity in builder.Model.GetEntityTypes().Where(e => !ServiceData.IsOwned(EventSource, e)))
            {
                foreach (var property in entity.GetProperties())
                {
                    if (property.IsPrimaryKey()) property.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                    if (property.Name == "Version")
                    {
                        property.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                        property.SetColumnType("varbinary(8)");
                    }
                }
            }
        }
        if (Database.IsSqlite())
        {
            builder.Entity<Booking>().Property(b => b.Version).IsConcurrencyToken().ValueGeneratedNever();
            builder.Entity<Payment>().Property(p => p.Version).IsConcurrencyToken().ValueGeneratedNever();
            // Keep exact monetary values and make ordering/comparisons translatable on SQLite.
            builder.Entity<Tour>().Property(t => t.Price).HasConversion<long>(v => (long)(v * 100m), v => v / 100m);
            builder.Entity<Booking>().Property(b => b.TotalPrice).HasConversion<long>(v => (long)(v * 100m), v => v / 100m);
            builder.Entity<Payment>().Property(p => p.Amount).HasConversion<long>(v => (long)(v * 100m), v => v / 100m);
            foreach (var fk in builder.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()).ToList())
                if (ServiceData.Owner(fk.DeclaringEntityType.GetTableName()!) != ServiceData.Owner(fk.PrincipalEntityType.GetTableName()!))
                    fk.DeleteBehavior = DeleteBehavior.ClientNoAction;
        }
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ChangeTracker.DetectChanges();
        if (!ApplyingProjection)
        {
            foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                var table = entry.Metadata.GetTableName()!;
                if (ServiceData.Owner(table) != "" && !ServiceData.IsOwned(EventSource, entry.Metadata))
                    throw new InvalidOperationException($"{EventSource} cannot write {table}; owner is {ServiceData.Owner(table)}.");
                if (Database.IsSqlite() && entry.State != EntityState.Deleted && entry.Entity is Booking or Payment)
                    entry.Property("Version").CurrentValue = Guid.NewGuid().ToByteArray();
            }
        }
        if (ApplyingProjection) return await base.SaveChangesAsync(cancellationToken);
        var changed = ChangeTracker.Entries()
            .Where(e => EventsEnabled && (e.State == EntityState.Added || e.State == EntityState.Modified)
                && (e.Entity is Booking || e.Entity is ContactInquiry || e.Entity is ContactInquiryReply))
            .Select(e => (e.Entity, Added: e.State == EntityState.Added,
                StatusChanged: e.State == EntityState.Added || e.Entity is ContactInquiryReply || e.Property("Status").IsModified))
            .Where(e => e.StatusChanged || e.Entity is Booking).ToList();
        if (changed.Count == 0) return await base.SaveChangesAsync(cancellationToken);

        var ownsTransaction = Database.CurrentTransaction == null;
        await using var transaction = ownsTransaction ? await Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var count = await base.SaveChangesAsync(cancellationToken);
            foreach (var (entity, added, _) in changed)
            {
                var (kind, id, userId, email, status) = entity switch
                {
                    Booking b => ("booking", b.BookingId.ToString(), b.UserId, b.Email, b.Status),
                    ContactInquiry c => ("contact", c.ContactInquiryId.ToString(), c.UserId, c.Email, c.Status),
                    ContactInquiryReply r => ("contact.reply", r.ContactInquiryId.ToString(),
                        ContactInquiries.Find(r.ContactInquiryId)?.UserId, (string?)null, r.IsFromAdmin ? "AdminReplied" : "CustomerReplied"),
                    _ => throw new InvalidOperationException()
                };
                var evt = new IntegrationEvent(Guid.NewGuid(), $"{kind}.{(added ? "created" : "updated")}",
                    ServiceData.Owner(entity switch
                    {
                        Booking => "Bookings",
                        ContactInquiry => "ContactInquiries",
                        ContactInquiryReply => "ContactInquiryReplies",
                        _ => throw new InvalidOperationException()
                    }), id, userId, email, status, DateTime.UtcNow,
                    Activity.Current?.GetTagItem("correlation.id")?.ToString() ?? Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
                    entity is Booking booking ? JsonSerializer.Serialize(new BookingEventData(booking.BookingId, booking.UserId, booking.TotalPrice, booking.Status))
                    : null);
                OutboxMessages.Add(new OutboxMessage
                {
                    Id = evt.EventId, Source = evt.Source, Type = evt.Type,
                    Payload = JsonSerializer.Serialize(evt), CreatedAt = evt.OccurredAt
                });
            }
            await base.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            return count;
        }
        catch
        {
            if (transaction != null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
