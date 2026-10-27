namespace Tourbooking.Distributed;

public sealed class InboxMessage
{
    public Guid Id { get; set; }
    public DateTime ProcessedAt { get; set; }
}

public sealed record BookingEventData(int BookingId, string UserId, decimal TotalPrice, string Status);
