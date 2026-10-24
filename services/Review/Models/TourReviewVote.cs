using System.ComponentModel.DataAnnotations;

namespace Tourbooking.Models;

public class TourReviewVote
{
    [Key]
    public int VoteId { get; set; }

    [Required]
    public int ReviewId { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    // 1 = upvote, -1 = downvote
    public int Value { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public TourReview? Review { get; set; }
    public ApplicationUser? User { get; set; }
}
