namespace Tourbooking.Notifications;

// Keep persistence attributes and future internal fields out of the public wire contract.
public sealed record NotificationResponse(Guid EventId, string Type, string ResourceId, string? UserId,
    string? Email, string Message, DateTime CreatedAt, string CorrelationId)
{
    public static NotificationResponse From(NotificationRecord record) => new(record.EventId, record.Type,
        record.ResourceId, record.UserId, record.Email, record.Message, record.CreatedAt, record.CorrelationId);
}
