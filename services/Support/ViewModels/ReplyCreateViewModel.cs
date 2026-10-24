using System.ComponentModel.DataAnnotations;

namespace Tourbooking.ViewModels;

public class ReplyCreateViewModel
{
    public int ContactInquiryId { get; set; }

    [Required]
    [StringLength(2000)]
    public string Message { get; set; } = string.Empty;
}
