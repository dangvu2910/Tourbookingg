using System.ComponentModel.DataAnnotations;

namespace Tourbooking.Models;

public class ContactInquiry
{
    [Key]
    public int ContactInquiryId { get; set; }

    [Required]
    [StringLength(200)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [StringLength(30)]
    public string? PhoneNumber { get; set; }

    [Required]
    [StringLength(120)]
    public string Subject { get; set; } = string.Empty;

    [Required]
    [StringLength(2000)]
    public string Message { get; set; } = string.Empty;

    [StringLength(450)]
    public string? UserId { get; set; }

    [StringLength(30)]
    public string Status { get; set; } = "Pending";

    [StringLength(2000)]
    public string? AdminReply { get; set; }

    public DateTime? RepliedAt { get; set; }

    [StringLength(450)]
    public string? RepliedByAdminId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ApplicationUser? User { get; set; }
}