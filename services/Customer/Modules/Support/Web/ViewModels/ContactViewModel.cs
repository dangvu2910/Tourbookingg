using System.ComponentModel.DataAnnotations;

namespace Tourbooking.ViewModels;

public class ContactViewModel
{
    [Required]
    [Display(Name = "Họ tên")]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [Display(Name = "Email")]
    public string Email { get; set; } = string.Empty;

    [Display(Name = "Số điện thoại")]
    public string? PhoneNumber { get; set; }

    [Required]
    [StringLength(120, ErrorMessage = "Tiêu đề quá dài")]
    [Display(Name = "Tiêu đề")]
    public string Subject { get; set; } = string.Empty;

    [Required]
    [StringLength(1000, ErrorMessage = "Nội dung quá dài")]
    [Display(Name = "Nội dung")]
    public string Message { get; set; } = string.Empty;
}
