namespace Tourbooking.Distributed;

public sealed record IntegrationEvent(Guid EventId, string Type, string Source, string ResourceId,
    string? UserId, string? Email, string Status, DateTime OccurredAt, string CorrelationId);

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string Source { get; set; } = "";
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime? LockedUntil { get; set; }
    public Guid? LockId { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
