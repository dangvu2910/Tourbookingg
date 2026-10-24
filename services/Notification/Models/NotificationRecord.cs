namespace Tourbooking.Notifications;

public sealed class NotificationRecord
{
    public Guid EventId { get; set; }
    public string Type { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string? UserId { get; set; }
    public string? Email { get; set; }
    public string Message { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

