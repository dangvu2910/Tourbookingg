using System.ComponentModel.DataAnnotations;

namespace Tourbooking.Models;

public class Booking
{
    [Timestamp]
    public byte[] Version { get; set; } = [];
    public Guid? RequestId { get; set; }
    [Key]
    public int BookingId { get; set; }

    [Required]
    public int TourId { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

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
    public DateTime TravelDate { get; set; }

    [Range(1, 20)]
    public int GuestCount { get; set; }

    [Range(0, double.MaxValue)]
    public decimal TotalPrice { get; set; }

    [StringLength(30)]
    public string Status { get; set; } = "Pending";

    public DateTime? CancelledAt { get; set; }

    [StringLength(500)]
    public string? CancelReason { get; set; }

    public DateTime? OriginalTravelDate { get; set; }

    public DateTime? RescheduledAt { get; set; }

    [StringLength(500)]
    public string? RescheduleNote { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Tour? Tour { get; set; }
    public ApplicationUser? User { get; set; }
}
