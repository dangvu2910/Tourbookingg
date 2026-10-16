using System.ComponentModel.DataAnnotations;

namespace Tourbooking.Models;

public class Payment
{
    [Timestamp]
    public byte[] Version { get; set; } = [];
    [Key]
    public int PaymentId { get; set; }

    [Required]
    public int BookingId { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Range(0, double.MaxValue)]
    public decimal Amount { get; set; }

    [Required]
    [StringLength(30)]
    public string Method { get; set; } = "Card";

    [StringLength(30)]
    public string Status { get; set; } = "Pending";

    [StringLength(60)]
    public string? Provider { get; set; }

    [StringLength(80)]
    public string? TransactionCode { get; set; }

    [StringLength(80)]
    public string? BankName { get; set; }

    [StringLength(120)]
    public string? BankAccountName { get; set; }

    [StringLength(40)]
    public string? BankAccountNumber { get; set; }

    [StringLength(120)]
    public string? BankReference { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? PaidAt { get; set; }

    public Booking? Booking { get; set; }
    public ApplicationUser? User { get; set; }
}
