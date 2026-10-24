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

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateBookingStatus(int id, string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return RedirectToAction("Bookings");
        }

        var booking = await _context.Bookings.FirstOrDefaultAsync(b => b.BookingId == id);
        if (booking == null)
        {
            return RedirectToAction("Bookings");
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
            return RedirectToAction("Bookings");
        }

        if (booking.Status == "Cancelled") return RedirectToAction("Bookings");
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
        return RedirectToAction("Bookings");
    }

}
