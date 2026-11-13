using System.ComponentModel.DataAnnotations;

namespace Tourbooking.ViewModels;

public class BookingCreateViewModel
{
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public int TourId { get; set; }
    public string TourName { get; set; } = string.Empty;
    public string TourLocation { get; set; } = string.Empty;
    public string? TourImageUrl { get; set; }
    public decimal TourPrice { get; set; }

    [Required]
    [StringLength(200)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [Phone]
    public string PhoneNumber { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Date)]
    public DateTime? TravelDate { get; set; }

    [Range(1, 20)]
    public int GuestCount { get; set; } = 2;

    public decimal ServiceFee { get; set; } = 45000m;
    public decimal LocalTax { get; set; } = 120000m;

    [StringLength(30)]
    public string PaymentMethod { get; set; } = "Card";

    [StringLength(80)]
    public string? BankName { get; set; }

    [StringLength(120)]
    public string? BankAccountName { get; set; }

    [StringLength(40)]
    public string? BankAccountNumber { get; set; }

    [StringLength(120)]
    public string? BankReference { get; set; }

    [StringLength(80)]
    public string? TransactionCode { get; set; }
}
