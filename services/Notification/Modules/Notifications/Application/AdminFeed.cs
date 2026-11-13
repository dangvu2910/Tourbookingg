using System.Globalization;
using System.Text.Json;
using Tourbooking.Distributed;

namespace Tourbooking.Notifications;

public sealed record AdminNotificationItem(Guid EventId, string Type, string ResourceId, string Message,
    string Link, DateTime CreatedAt, bool Unread);

public sealed record AdminNotificationSummary(long Unread, IReadOnlyList<AdminNotificationItem> Items);

/// <summary>
/// The admin bell: events an administrator must act on (new booking, payment submitted, new support request).
/// Built from the notifications the consumer already stores; no extra queue or service call.
/// </summary>
public static class AdminFeed
{
    public const string BookingCreated = "booking.created";
    public const string BookingUpdated = "booking.updated";
    public const string ContactCreated = "contact.created";
    public const string PaymentSubmittedStatus = "Processing";
    public const int DefaultLimit = 10;
    private static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi-VN");

    public static bool IsRelevant(NotificationRecord n) =>
        n.Type is BookingCreated or ContactCreated || (n.Type == BookingUpdated && n.Status == PaymentSubmittedStatus);

    public static decimal? ReadTotalPrice(IntegrationEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.Data) || !evt.Type.StartsWith("booking.", StringComparison.Ordinal)) return null;
        try
        {
            using var json = JsonDocument.Parse(evt.Data);
            return json.RootElement.TryGetProperty("TotalPrice", out var price) && price.TryGetDecimal(out var value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    public static string Message(NotificationRecord n)
    {
        var from = string.IsNullOrWhiteSpace(n.Email) ? "" : $" từ {n.Email}";
        return n.Type switch
        {
            BookingCreated => $"Đơn đặt tour mới #{n.ResourceId}{from}" +
                (n.TotalPrice is { } price ? $" · {price.ToString("N0", Vietnamese)} ₫" : "") + ". Cần xác nhận.",
            BookingUpdated => $"Khách đã gửi thanh toán cho đơn #{n.ResourceId}{from}. Cần kiểm tra và xác nhận.",
            ContactCreated => $"Yêu cầu hỗ trợ mới #{n.ResourceId}{from}.",
            _ => n.Message
        };
    }

    public static string Link(NotificationRecord n) => n.Type switch
    {
        BookingCreated => "/Admin/Bookings?status=Pending",
        BookingUpdated => "/Admin/Bookings?status=Processing",
        ContactCreated => "/Admin/Contacts?id=" + Uri.EscapeDataString(n.ResourceId),
        _ => "/Admin/Dashboard"
    };

    public static AdminNotificationItem ToItem(NotificationRecord n, DateTime seenAt) =>
        new(n.EventId, n.Type, n.ResourceId, Message(n), Link(n), n.CreatedAt, n.CreatedAt > seenAt);
}
