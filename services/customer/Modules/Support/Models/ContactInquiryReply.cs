using System.ComponentModel.DataAnnotations;

namespace Tourbooking.Models;

public class ContactInquiryReply
{
    [Key]
    public int ContactInquiryReplyId { get; set; }

    public int ContactInquiryId { get; set; }

    [Required]
    [StringLength(2000)]
    public string Message { get; set; } = string.Empty;

    [StringLength(450)]
    public string? UserId { get; set; }

    public bool IsFromAdmin { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ContactInquiry? ContactInquiry { get; set; }

    public ApplicationUser? User { get; set; }
}
