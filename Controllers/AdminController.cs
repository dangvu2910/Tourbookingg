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
                Status = "Published"
            }).ToList()
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

    public async Task<IActionResult> Bookings(string? status, int page = 1)
    {
        const int pageSize = 10;
        var bookingsQuery = _context.Bookings
            .AsNoTracking()
            .Include(b => b.Tour)
            .OrderByDescending(b => b.CreatedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (string.Equals(status, "PendingConfirmation", StringComparison.OrdinalIgnoreCase))
            {
                bookingsQuery = bookingsQuery.Where(b =>
                    b.Status == "PendingConfirmation" || b.Status == "Pending" || b.Status == "Processing");
            }
            else
            {
                bookingsQuery = bookingsQuery.Where(b => b.Status == status);
            }
        }

        var totalBookings = await bookingsQuery.CountAsync();
        var totalPages = (int)Math.Ceiling(totalBookings / (double)pageSize);

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

        var bookings = await bookingsQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var totalRevenue = bookings.Sum(b => b.TotalPrice);
        var averageGroup = totalBookings == 0
            ? 0
            : bookings.Average(b => b.GuestCount);
        var locations = await _context.Tours
            .AsNoTracking()
            .Select(t => t.Location)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToListAsync();

        var provinceCount = locations
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var model = new AdminBookingsViewModel
        {
            TotalBookings = totalBookings,
            Revenue = totalRevenue.ToString("C0", VnCulture),
            ActiveTours = provinceCount,
            AverageGroupSize = totalBookings == 0
                ? "0"
                : averageGroup.ToString("0.0", CultureInfo.InvariantCulture),
            Statuses = new List<string> { "PendingConfirmation", "Confirmed", "Cancelled" },
            SelectedStatus = status,
            CurrentPage = page,
            TotalPages = totalPages,
            PageSize = pageSize,
            RecentBookings = bookings.Select(b => new AdminBookingRow(
                b.BookingId,
                GetInitials(b.FullName),
                b.FullName,
                b.Email,
                b.Tour?.Name ?? "-",
                b.TravelDate.ToString("dd/MM/yyyy"),
                b.GuestCount,
                b.TotalPrice.ToString("C0", VnCulture),
                b.Status)).ToList()
        };

        return View(model);
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

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateBookingStatus(int id, string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return RedirectToAction(nameof(Bookings));
        }

        var booking = await _context.Bookings.FirstOrDefaultAsync(b => b.BookingId == id);
        if (booking == null)
        {
            return RedirectToAction(nameof(Bookings));
        }

        var normalized = status.Trim();
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Confirmed",
            "Cancelled",
            "PendingConfirmation",
            "Pending"
        };

        if (!allowed.Contains(normalized))
        {
            return RedirectToAction(nameof(Bookings));
        }

        if (booking.Status == "Cancelled") return RedirectToAction(nameof(Bookings));
        booking.Status = normalized.Equals("Pending", StringComparison.OrdinalIgnoreCase)
            ? "PendingConfirmation"
            : normalized;

        var payments = await _context.Payments.Where(p => p.BookingId == id).ToListAsync();
        foreach (var payment in payments)
        {
            payment.Status = booking.Status switch { "Confirmed" => "Paid", "Cancelled" => "Cancelled", _ => "PendingConfirmation" };
            payment.PaidAt = booking.Status == "Confirmed" ? DateTime.UtcNow : null;
        }
        if (booking.Status == "Cancelled") booking.CancelledAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Bookings));
    }

    public async Task<IActionResult> Users(int page = 1)
    {
        const int pageSize = 10;
        var usersQuery = _userManager.Users.AsNoTracking().OrderBy(u => u.Email);
        var totalUsers = await usersQuery.CountAsync();
        var totalPages = (int)Math.Ceiling(totalUsers / (double)pageSize);

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

        var users = await usersQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var rows = new List<AdminUserRow>(users.Count);
        foreach (var user in users)
        {
            var roles = await _userManager.GetRolesAsync(user);
            var role = roles.FirstOrDefault() ?? "User";
            var status = user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTimeOffset.UtcNow
                ? "Paused"
                : "Active";
            var displayName = string.IsNullOrWhiteSpace(user.UserName) ? user.Email ?? "" : user.UserName;
            var joinedDate = user.CreatedAt == default
                ? "-"
                : user.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy");
            rows.Add(new AdminUserRow(
                user.Id,
                GetInitials(displayName),
                displayName,
                user.Email ?? string.Empty,
                role,
                joinedDate,
                status));
        }

        var model = new AdminUsersViewModel
        {
            TotalUsers = totalUsers,
            CurrentPage = page,
            TotalPages = totalPages,
            PageSize = pageSize,
            Users = rows
        };

        return View(model);
    }

    public async Task<IActionResult> Contacts(string? status = null)
    {
        var query = _context.ContactInquiries
            .AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(c => c.Status == status);
        }

        var model = new AdminContactsViewModel
        {
            SelectedStatus = status,
            Contacts = await query
                .Select(c => new AdminContactRow
                {
                    ContactInquiryId = c.ContactInquiryId,
                    FullName = c.FullName,
                    Email = c.Email,
                    PhoneNumber = c.PhoneNumber,
                    Subject = c.Subject,
                    Message = c.Message,
                    Status = c.Status,
                    AdminReply = c.AdminReply,
                    CreatedAt = c.CreatedAt,
                    RepliedAt = c.RepliedAt
                })
                .ToListAsync()
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReplyContact(int contactInquiryId, string adminReply)
    {
        if (string.IsNullOrWhiteSpace(adminReply))
        {
            TempData["ContactReplyMessage"] = "Vui lòng nhập nội dung phản hồi.";
            return RedirectToAction(nameof(Contacts));
        }

        var contact = await _context.ContactInquiries.FirstOrDefaultAsync(c => c.ContactInquiryId == contactInquiryId);
        if (contact == null)
        {
            TempData["ContactReplyMessage"] = "Không tìm thấy yêu cầu liên hệ.";
            return RedirectToAction(nameof(Contacts));
        }

        var adminUser = await _userManager.GetUserAsync(User);
        contact.AdminReply = adminReply.Trim();
        contact.RepliedAt = DateTime.UtcNow;
        contact.RepliedByAdminId = adminUser?.Id;
        contact.Status = "Replied";
        contact.UpdatedAt = DateTime.UtcNow;

        var reply = new ContactInquiryReply
        {
            ContactInquiryId = contact.ContactInquiryId,
            Message = adminReply.Trim(),
            UserId = adminUser?.Id,
            IsFromAdmin = true,
        };

        _context.Add(reply);
        await _context.SaveChangesAsync();

        TempData["ContactReplyMessage"] = "Đã gửi phản hồi cho người dùng.";
        return RedirectToAction(nameof(Contacts));
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

    public async Task<IActionResult> EditUser(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return NotFound();
        }

        var user = await _userManager.FindByIdAsync(id);
        if (user == null)
        {
            return NotFound();
        }

        var roles = await _userManager.GetRolesAsync(user);
        var model = new EditUserViewModel
        {
            Id = user.Id,
            Email = user.Email ?? string.Empty,
            FullName = user.FullName,
            PhoneNumber = user.PhoneNumber,
            Address = user.Address,
            Role = roles.FirstOrDefault() ?? "User"
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditUser(EditUserViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await _userManager.FindByIdAsync(model.Id);
        if (user == null)
        {
            return NotFound();
        }

        if (!string.Equals(user.Email, model.Email, StringComparison.OrdinalIgnoreCase))
        {
            var setEmailResult = await _userManager.SetEmailAsync(user, model.Email);
            if (!setEmailResult.Succeeded)
            {
                foreach (var error in setEmailResult.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                return View(model);
            }

            var setUserNameResult = await _userManager.SetUserNameAsync(user, model.Email);
            if (!setUserNameResult.Succeeded)
            {
                foreach (var error in setUserNameResult.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                return View(model);
            }
        }

        user.FullName = model.FullName;
        user.PhoneNumber = model.PhoneNumber;
        user.Address = model.Address;

        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            foreach (var error in updateResult.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return View(model);
        }

        if (!string.IsNullOrWhiteSpace(model.NewPassword))
        {
            var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
            var resetResult = await _userManager.ResetPasswordAsync(user, resetToken, model.NewPassword);
            if (!resetResult.Succeeded)
            {
                foreach (var error in resetResult.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                return View(model);
            }
        }

        var currentRoles = await _userManager.GetRolesAsync(user);
        var targetRole = string.IsNullOrWhiteSpace(model.Role) ? "User" : model.Role;
        if (!currentRoles.Contains(targetRole))
        {
            var removeResult = await _userManager.RemoveFromRolesAsync(user, currentRoles);
            if (!removeResult.Succeeded)
            {
                foreach (var error in removeResult.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                return View(model);
            }

            var addResult = await _userManager.AddToRoleAsync(user, targetRole);
            if (!addResult.Succeeded)
            {
                foreach (var error in addResult.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                return View(model);
            }
        }

        return RedirectToAction(nameof(Users));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteUser(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return NotFound();
        }

        var user = await _userManager.FindByIdAsync(id);
        if (user == null)
        {
            return NotFound();
        }

        await _userManager.DeleteAsync(user);
        return RedirectToAction(nameof(Users));
    }

    public IActionResult CreateUser()
    {
        var model = new CreateUserViewModel
        {
            Role = "User"
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateUser(CreateUserViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = new ApplicationUser
        {
            UserName = model.Email,
            Email = model.Email,
            FullName = model.FullName,
            PhoneNumber = model.PhoneNumber,
            Address = model.Address,
            CreatedAt = DateTime.UtcNow
        };

        var createResult = await _userManager.CreateAsync(user, model.Password);
        if (!createResult.Succeeded)
        {
            foreach (var error in createResult.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return View(model);
        }

        var role = string.IsNullOrWhiteSpace(model.Role) ? "User" : model.Role;
        var roleResult = await _userManager.AddToRoleAsync(user, role);
        if (!roleResult.Succeeded)
        {
            foreach (var error in roleResult.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return View(model);
        }

        return RedirectToAction(nameof(Users));
    }

    private static string GetInitials(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "?";
        }

        var parts = value
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 1)
        {
            return parts[0].Length >= 2
                ? string.Concat(parts[0][0], parts[0][1]).ToUpperInvariant()
                : parts[0][0].ToString().ToUpperInvariant();
        }

        var first = parts[0][0];
        var last = parts[^1][0];
        return string.Concat(first, last).ToUpperInvariant();
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
}
