using System;
using System.Globalization;
using System.IO;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tourbooking.Models;
using Tourbooking.Distributed;
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

    public async Task<IActionResult> Bookings(string? status, int page = 1)
    {
        const int pageSize = 10;
        var bookingsQuery = _context.Bookings
            .AsNoTracking()
            .Include(b => b.Tour)
            .OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.BookingId)
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

        var bookingIds = bookings.Select(b => b.BookingId).ToArray();
        var paymentRows = await _context.Payments
            .AsNoTracking()
            .Where(payment => bookingIds.Contains(payment.BookingId))
            .OrderByDescending(payment => payment.CreatedAt)
            .Select(payment => new { payment.BookingId, payment.Status, payment.TransactionCode })
            .ToListAsync();
        var latestPayments = new Dictionary<int, (string Status, string? TransactionCode)>();
        foreach (var payment in paymentRows)
            latestPayments.TryAdd(payment.BookingId, (payment.Status, payment.TransactionCode));

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
                b.Status,
                latestPayments.GetValueOrDefault(b.BookingId).Status,
                latestPayments.GetValueOrDefault(b.BookingId).TransactionCode,
                Convert.ToBase64String(b.Version))).ToList()
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateBookingStatus(int id, string status, string? filterStatus, int page = 1, string? bookingVersion = null)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            TempData["BookingAdminError"] = "Trạng thái vé không hợp lệ.";
            return RedirectToBookings(filterStatus, page);
        }

        var booking = await _context.Bookings.FirstOrDefaultAsync(b => b.BookingId == id);
        if (booking == null)
        {
            TempData["BookingAdminError"] = $"Không tìm thấy vé #{id}.";
            return RedirectToBookings(filterStatus, page);
        }

        BookingVersionGuard.RequireCurrent(_context, booking, bookingVersion);

        var normalized = status.Trim();
        if (!normalized.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            TempData["BookingAdminError"] = "Chỉ có thể hủy vé tại đây. Hãy xác nhận thanh toán bằng nút riêng sau khi kiểm tra đã nhận tiền.";
            return RedirectToBookings(filterStatus, page);
        }

        if (string.Equals(booking.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            TempData["BookingAdminError"] = $"Vé #{id} đã được hủy trước đó.";
            return RedirectToBookings(filterStatus, page);
        }

        booking.Status = "Cancelled";
        booking.CancelledAt ??= DateTime.UtcNow;
        var payment = await _context.Payments
            .Where(p => p.BookingId == id)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync();
        if (payment != null)
            payment.Status = payment.Status == "Paid" ? "RefundPending" : "Cancelled";

        await _context.SaveChangesAsync();
        TempData["BookingAdminMessage"] = $"Đã hủy vé #{id}.";
        return RedirectToBookings(filterStatus, page);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmPaymentReceived(int id, string? filterStatus, int page = 1, string? bookingVersion = null)
    {
        var booking = await _context.Bookings.FirstOrDefaultAsync(b => b.BookingId == id);
        if (booking == null)
        {
            TempData["BookingAdminError"] = $"Không tìm thấy vé #{id}.";
            return RedirectToBookings(filterStatus, page);
        }

        BookingVersionGuard.RequireCurrent(_context, booking, bookingVersion);

        if (string.Equals(booking.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            TempData["BookingAdminError"] = $"Vé #{id} đã hủy, không thể xác nhận thanh toán.";
            return RedirectToBookings(filterStatus, page);
        }

        var payment = await _context.Payments
            .Where(p => p.BookingId == id)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync();

        if (payment == null)
        {
            TempData["BookingAdminError"] = $"Không tìm thấy giao dịch thanh toán cho vé #{id}.";
            return RedirectToBookings(filterStatus, page);
        }

        if (payment.Status is "Cancelled" or "RefundPending")
        {
            TempData["BookingAdminError"] = $"Giao dịch của vé #{id} đã bị hủy hoặc đang hoàn tiền.";
            return RedirectToBookings(filterStatus, page);
        }

        if (payment.Status is not ("Processing" or "Paid"))
        {
            TempData["BookingAdminError"] = $"Khách chưa báo đã chuyển khoản cho vé #{id}.";
            return RedirectToBookings(filterStatus, page);
        }

        payment.Status = "Paid";
        payment.PaidAt ??= DateTime.UtcNow;
        booking.Status = "Confirmed";
        await _context.SaveChangesAsync();

        TempData["BookingAdminMessage"] = $"Đã xác nhận nhận tiền và cấp vé #{id}.";
        return RedirectToBookings(filterStatus, page);
    }

    private IActionResult RedirectToBookings(string? status, int page) =>
        RedirectToAction(nameof(Bookings), new { status, page = Math.Max(page, 1) });
}
