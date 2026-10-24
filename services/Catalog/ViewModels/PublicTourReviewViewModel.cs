namespace Tourbooking.ViewModels;

public record PublicTourReviewRow(
    int ReviewId,
    string ReviewerName,
    int Rating,
    string Title,
    string Content,
    DateTime CreatedAt,
    int Upvotes,
    int Downvotes,
    int? UserVote);