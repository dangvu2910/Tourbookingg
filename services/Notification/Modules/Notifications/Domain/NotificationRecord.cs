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
    // Kept for the admin feed; records stored before these fields existed have them null.
    public string? Status { get; set; }
    public decimal? TotalPrice { get; set; }
}

/// <summary>When an administrator last opened the notification bell.</summary>
public sealed class AdminNotificationSeen
{
    public string AdminId { get; set; } = "";
    public DateTime SeenAt { get; set; }
}
