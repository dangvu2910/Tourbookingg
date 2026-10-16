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
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, IConfiguration? configuration = null)
        : base(options)
    {
        EventSource = configuration?["Service:Name"] ?? "standalone";
        EventsEnabled = configuration?.GetValue("RabbitMQ:Enabled", false) ?? false;
    }

    public DbSet<Tour> Tours { get; set; }
    public DbSet<Booking> Bookings { get; set; }
    public DbSet<Payment> Payments { get; set; }
    public DbSet<TourReview> TourReviews { get; set; }
    public DbSet<TourReviewVote> TourReviewVotes { get; set; }
    public DbSet<ContactInquiry> ContactInquiries { get; set; }
    public DbSet<ContactInquiryReply> ContactInquiryReplies { get; set; }
    

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
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
            .WithMany(b => b.Payments)
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

        
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ChangeTracker.DetectChanges();
        var changed = ChangeTracker.Entries()
            .Where(e => EventsEnabled && (e.State == EntityState.Added || e.State == EntityState.Modified)
                && (e.Entity is Booking || e.Entity is Payment || e.Entity is ContactInquiry))
            .Select(e => (e.Entity, Added: e.State == EntityState.Added,
                StatusChanged: e.State == EntityState.Added || e.Property("Status").IsModified))
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
                    Payment p => ("payment", p.PaymentId.ToString(), p.UserId, (string?)null, p.Status),
                    ContactInquiry c => ("contact", c.ContactInquiryId.ToString(), c.UserId, c.Email, c.Status),
                    _ => throw new InvalidOperationException()
                };
                var evt = new IntegrationEvent(Guid.NewGuid(), $"{kind}.{(added ? "created" : "updated")}",
                    EventSource, id, userId, email, status, DateTime.UtcNow,
                    Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"));
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
