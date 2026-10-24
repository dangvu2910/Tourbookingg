using System.ComponentModel.DataAnnotations;

namespace Tourbooking.Models;

public class Tour
{
    [Key]
    public int TourId { get; set; }

    [Display(Name = "Tên tour")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Địa điểm")]
    public string Location { get; set; } = string.Empty;

    [Display(Name = "Giá")]
    public decimal Price { get; set; }

    [Display(Name = "Mô tả")]
    public string Description { get; set; } = string.Empty;

    [Display(Name = "Hình ảnh")]
    public string? ImageUrl { get; set; }

    [Display(Name = "Danh mục")]
    public int CategoryId { get; set; }
}