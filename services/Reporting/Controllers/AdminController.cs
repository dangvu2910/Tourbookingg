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

    public async Task<IActionResult> Dashboard()
    {
        var tours = await _context.Tours.AsNoTracking().ToListAsync();

        var model = new AdminDashboardViewModel
        {
            TotalTours = tours.Count,
            TotalBookings = await _context.Bookings.CountAsync(),
            TotalUsers = await _userManager.Users.CountAsync(),
            TopTours = tours
                .OrderByDescending(t => t.Price)
                .Take(3)
                .Select(t => new AdminTourCard
                {
                    TourId = t.TourId,
                    Name = t.Name,
                    ImageUrl = NormalizeImageUrl(t.ImageUrl) ?? string.Empty,
                    Location = t.Location,
                    Metric = t.Price.ToString("C0", VnCulture)
                })
                .ToList(),
            RecentActivities = (await _context.Bookings.AsNoTracking().OrderByDescending(b => b.CreatedAt).Take(5)
                .Select(b => new { b.BookingId, b.FullName, b.Status }).ToListAsync())
                .Select(b => new AdminActivity($"Đặt tour #{b.BookingId}", $"{b.FullName} — {b.Status}")).ToList()
        };

        return View(model);
    }


    [HttpGet]
    public async Task<IActionResult> ExportToursExcel(string? region)
    {
        var toursQuery = _context.Tours.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(region))
        {
            var keywords = GetRegionKeywords(new[] { region });
            if (keywords.Count > 0)
            {
                toursQuery = toursQuery.Where(t => keywords.Any(k => t.Location.Contains(k)));
            }
        }

        var tours = await toursQuery.OrderBy(t => t.TourId).ToListAsync();

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Tours");
        sheet.Cell(1, 1).Value = "TourId";
        sheet.Cell(1, 2).Value = "Ten tour";
        sheet.Cell(1, 3).Value = "Dia diem";
        sheet.Cell(1, 4).Value = "Gia";
        sheet.Cell(1, 5).Value = "Trang thai";
        sheet.Cell(1, 6).Value = "Anh";

        var row = 2;
        foreach (var tour in tours)
        {
            var status = "Da dang";
            sheet.Cell(row, 1).Value = tour.TourId;
            sheet.Cell(row, 2).Value = tour.Name;
            sheet.Cell(row, 3).Value = tour.Location;
            sheet.Cell(row, 4).Value = tour.Price;
            sheet.Cell(row, 5).Value = status;
            sheet.Cell(row, 6).Value = tour.ImageUrl ?? string.Empty;
            row++;
        }

        sheet.Columns().AdjustToContents();

        await using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var fileName = $"tours-report-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx";
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }


    [HttpGet]
    public async Task<IActionResult> ExportBookingsExcel()
    {
        var bookings = await _context.Bookings
            .AsNoTracking()
            .Include(b => b.Tour)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Bookings");
        sheet.Cell(1, 1).Value = "BookingId";
        sheet.Cell(1, 2).Value = "Khach hang";
        sheet.Cell(1, 3).Value = "Email";
        sheet.Cell(1, 4).Value = "Ten tour";
        sheet.Cell(1, 5).Value = "Ngay di";
        sheet.Cell(1, 6).Value = "So luong";
        sheet.Cell(1, 7).Value = "Tong gia";
        sheet.Cell(1, 8).Value = "Trang thai";

        var row = 2;
        foreach (var booking in bookings)
        {
            sheet.Cell(row, 1).Value = booking.BookingId;
            sheet.Cell(row, 2).Value = booking.FullName;
            sheet.Cell(row, 3).Value = booking.Email;
            sheet.Cell(row, 4).Value = booking.Tour?.Name ?? "-";
            sheet.Cell(row, 5).Value = booking.TravelDate.ToString("dd/MM/yyyy");
            sheet.Cell(row, 6).Value = booking.GuestCount;
            sheet.Cell(row, 7).Value = booking.TotalPrice;
            sheet.Cell(row, 8).Value = booking.Status;
            row++;
        }

        sheet.Columns().AdjustToContents();

        await using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var fileName = $"bookings-report-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx";
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }


    [HttpGet]
    public async Task<IActionResult> ExportUsers()
    {
        var users = await _userManager.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync();

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Users");
        sheet.Cell(1, 1).Value = "Ho va ten";
        sheet.Cell(1, 2).Value = "Email";
        sheet.Cell(1, 3).Value = "Vai tro";
        sheet.Cell(1, 4).Value = "Ngay gia nhap";
        sheet.Cell(1, 5).Value = "Trang thai";
        sheet.Cell(1, 6).Value = "So dien thoai";
        sheet.Cell(1, 7).Value = "Dia chi";

        var row = 2;
        foreach (var user in users)
        {
            var roles = await _userManager.GetRolesAsync(user);
            var role = roles.FirstOrDefault() ?? "User";
            var roleLabel = role == "Admin" ? "Quan tri" : "Nguoi dung";
            var status = user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTimeOffset.UtcNow
                ? "Tam dung"
                : "Dang hoat dong";
            var joinedDate = user.CreatedAt == default
                ? string.Empty
                : user.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy");
            var displayName = string.IsNullOrWhiteSpace(user.UserName) ? user.Email ?? "" : user.UserName;

            sheet.Cell(row, 1).Value = displayName;
            sheet.Cell(row, 2).Value = user.Email ?? string.Empty;
            sheet.Cell(row, 3).Value = roleLabel;
            sheet.Cell(row, 4).Value = joinedDate;
            sheet.Cell(row, 5).Value = status;
            sheet.Cell(row, 6).Value = user.PhoneNumber ?? string.Empty;
            sheet.Cell(row, 7).Value = user.Address ?? string.Empty;
            row++;
        }

        sheet.Columns().AdjustToContents();

        await using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var fileName = $"users-report-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx";
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }


}
