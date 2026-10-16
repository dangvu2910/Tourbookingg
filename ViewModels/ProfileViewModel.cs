using System.ComponentModel.DataAnnotations;

namespace Tourbooking.ViewModels;

public class ProfileViewModel
{
    [Display(Name = "Họ và tên")]
    public string? FullName { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập email.")]
    [EmailAddress(ErrorMessage = "Email không hợp lệ.")]
    [Display(Name = "Email liên hệ")]
    public string Email { get; set; } = string.Empty;

    [RegularExpression("^\\d{10}$", ErrorMessage = "Số điện thoại phải đủ 10 số.")]
    [Display(Name = "Số điện thoại")]
    public string? PhoneNumber { get; set; }

    [Display(Name = "Địa chỉ")]
    public string? Address { get; set; }
}
