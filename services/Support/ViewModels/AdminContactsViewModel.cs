namespace Tourbooking.ViewModels;

public class AdminContactsViewModel
{
    public string? SelectedStatus { get; set; }

    public List<AdminContactRow> Contacts { get; set; } = new();
}

public class AdminContactRow
{
    public int ContactInquiryId { get; set; }

    public string FullName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string? PhoneNumber { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? AdminReply { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? RepliedAt { get; set; }
}