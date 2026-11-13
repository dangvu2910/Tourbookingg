using System.ComponentModel.DataAnnotations;

namespace Tourbooking.Models;

public class TourReview
{
    [Key]
    public int ReviewId { get; set; }

    [Required]
    public int BookingId { get; set; }

    [Required]
    public int TourId { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Range(1, 5)]
    public int Rating { get; set; }

    [Required]
    [StringLength(120)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [StringLength(1000)]
    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Booking? Booking { get; set; }
    public Tour? Tour { get; set; }
    public ApplicationUser? User { get; set; }
    public List<TourReviewVote> Votes { get; set; } = new();
}