using System;
using System.Globalization;
using System.IO;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tourbooking.Models;
using Tourbooking.ViewModels;

namespace Tourbooking.Controllers;

[Authorize(Roles = "Admin")]
public class AdminController : Controller
{

    private static readonly CultureInfo VnCulture = CultureInfo.GetCultureInfo("vi-VN");
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public AdminController(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    private static List<string> GetRegionKeywords(IEnumerable<string> regions)
    {
        var regionKeywords = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Miền Bắc"] = new[]
            {
                "Hà Nội", "Ha Noi", "Hải Phòng", "Hai Phong", "Quảng Ninh", "Quang Ninh",
                "Lào Cai", "Lao Cai", "Sa Pa", "Sapa", "Hà Giang", "Ha Giang",
                "Sơn La", "Son La", "Mộc Châu", "Moc Chau", "Ninh Bình", "Ninh Binh"
            },
            ["Miền Trung"] = new[]
            {
                "Đà Nẵng", "Da Nang", "Quảng Nam", "Quang Nam", "Hội An", "Hoi An",
                "Huế", "Hue", "Thừa Thiên", "Bình Định", "Binh Dinh", "Quy Nhơn", "Quy Nhon",
                "Phú Yên", "Phu Yen", "Khánh Hòa", "Khanh Hoa", "Nha Trang",
                "Đắk Lắk", "Dak Lak", "Lâm Đồng", "Lam Dong", "Đà Lạt", "Da Lat",
                "Quảng Ngãi", "Quang Ngai"
            },
            ["Miền Nam"] = new[]
            {
                "TP. Hồ Chí Minh", "TP Ho Chi Minh", "Hồ Chí Minh", "Ho Chi Minh", "Sài Gòn", "Sai Gon",
                "Vũng Tàu", "Vung Tau", "Đồng Nai", "Dong Nai", "Cần Thơ", "Can Tho",
                "Phú Quốc", "Phu Quoc", "Bình Dương", "Binh Duong", "Tây Ninh", "Tay Ninh",
                "Long An", "Bến Tre", "Ben Tre", "Tiền Giang", "Tien Giang", "Kiên Giang", "Kien Giang"
            }
        };

        var keywords = new List<string>();
        foreach (var region in regions)
        {
            if (string.IsNullOrWhiteSpace(region))
            {
                continue;
            }

            if (regionKeywords.TryGetValue(region, out var regionItems))
            {
                keywords.AddRange(regionItems);
            }
            else
            {
                keywords.Add(region);
            }
        }

        return keywords.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? NormalizeImageUrl(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        if (imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || imageUrl.StartsWith("~", StringComparison.OrdinalIgnoreCase))
        {
            return imageUrl;
        }

        if (imageUrl.StartsWith("wwwroot/", StringComparison.OrdinalIgnoreCase))
        {
            return "/" + imageUrl.Substring("wwwroot/".Length).TrimStart('/');
        }

        if (imageUrl.StartsWith("/", StringComparison.OrdinalIgnoreCase))
        {
            return imageUrl.Replace("\\", "/");
        }

        if (imageUrl.StartsWith("images/", StringComparison.OrdinalIgnoreCase))
        {
            return "/" + imageUrl.TrimStart('/');
        }

        return "/images/" + imageUrl.TrimStart('/');
    }

    public async Task<IActionResult> Tours(string? region, int page = 1)
    {
        const int pageSize = 10;
        var toursQuery = _context.Tours.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(region))
        {
            var keywords = GetRegionKeywords(new[] { region });
            if (keywords.Count > 0)
            {
                toursQuery = toursQuery.Where(t => keywords.Any(k => t.Location.Contains(k)));
            }
        }

        var totalTours = await toursQuery.CountAsync();
        var relatedBookings = _context.Bookings.Where(b => toursQuery.Select(t => t.TourId).Contains(b.TourId));
        var bookingCount = await relatedBookings.CountAsync();
        var confirmedCount = await relatedBookings.CountAsync(b => b.Status == "Confirmed");
        var totalPages = (int)Math.Ceiling(totalTours / (double)pageSize);

        if (totalPages == 0)
        {
            totalPages = 1;
        }

        if (page < 1)
        {
            page = 1;
        }

        if (page > totalPages)
        {
            page = totalPages;
        }

        var tours = await toursQuery
            .OrderBy(t => t.TourId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        var destinations = tours
            .Select(t => t.Location)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var model = new AdminToursViewModel
        {
            TotalTours = totalTours,
            Destinations = destinations,
            AverageBookingRate = bookingCount == 0 ? 0 : (int)Math.Round(confirmedCount * 100.0 / bookingCount),
            Regions = new List<string> { "Miền Bắc", "Miền Trung", "Miền Nam" },
            SelectedRegion = region,
            CurrentPage = page,
            TotalPages = totalPages,
            PageSize = pageSize,
            Tours = tours.Select(t => new AdminTourRow
            {
                TourId = t.TourId,
                Name = t.Name,
                Location = t.Location,
                ImageUrl = NormalizeImageUrl(t.ImageUrl) ?? string.Empty,
                Price = t.Price.ToString("C0", VnCulture),
                DepartureTime = t.DepartureTime,
                ReturnTime = t.ReturnTime,
                Status = "Published"
            }).ToList()
        };

        return View(model);
    }

}
