namespace Tourbooking.Distributed;

public sealed record IntegrationEvent(Guid EventId, string Type, string Source, string ResourceId,
    string? UserId, string? Email, string Status, DateTime OccurredAt, string CorrelationId);

