namespace Tourbooking.ViewModels;

public class ContactPageViewModel
{
    public ContactViewModel Form { get; set; } = new();

    public string? LookupEmail { get; set; }

    public List<ContactInquiryRow> Tickets { get; set; } = new();
}

public class ContactInquiryRow
{
    public int ContactInquiryId { get; set; }

    public string FullName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? AdminReply { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? RepliedAt { get; set; }
}