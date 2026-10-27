using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Tourbooking.Models;
using Tourbooking.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace Tourbooking.Controllers;

public class AccountController : Controller
{

        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IConfiguration _configuration;
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _webHostEnvironment;

        public AccountController(
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            IConfiguration configuration,
            ApplicationDbContext context,
            IWebHostEnvironment webHostEnvironment)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _configuration = configuration;
            _context = context;
            _webHostEnvironment = webHostEnvironment;
        }

        private static bool CanCustomerCancel(Booking booking, DateTime today)
        {
            if (string.Equals(booking.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (booking.TravelDate.Date < today)
            {
                return false;
            }

            if (string.Equals(booking.Status, "Confirmed", StringComparison.OrdinalIgnoreCase))
            {
                return booking.TravelDate.Date >= today.AddDays(1);
            }

            return true;
        }

        private static bool CanCustomerReschedule(Booking booking, DateTime today)
        {
            if (string.Equals(booking.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (booking.TravelDate.Date < today)
            {
                return false;
            }

            if (string.Equals(booking.Status, "Confirmed", StringComparison.OrdinalIgnoreCase))
            {
                return booking.TravelDate.Date >= today.AddDays(1);
            }

            return true;
        }

        private string? ResolveTourImageUrl(string? imageUrl, string? location = null, string? name = null)
        {
            if (IsHoiAnTour(location, name))
            {
                return "/images/839e713c-76f9-40d3-9764-758b56220ae0_hoian.webp";
            }

            return NormalizeImageUrl(imageUrl);
        }

        private bool IsHoiAnTour(string? location, string? name)
        {
            return ContainsHoiAn(location) || ContainsHoiAn(name);
        }

        private static bool ContainsHoiAn(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Contains("Hội An", StringComparison.OrdinalIgnoreCase)
                || value.Contains("Hoi An", StringComparison.OrdinalIgnoreCase)
                || value.Contains("HoiAn", StringComparison.OrdinalIgnoreCase);
        }

        private string? NormalizeImageUrl(string? imageUrl)
        {
            const string fallbackImage = "/images/8c18b822-a807-48e3-b471-ef035840c58c_mientay.jpeg";

            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                return fallbackImage;
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
                var normalizedPath = imageUrl.Replace("\\", "/");
                var localPath = normalizedPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var physicalPath = Path.Combine(_webHostEnvironment.WebRootPath, localPath);
                return System.IO.File.Exists(physicalPath) ? normalizedPath : fallbackImage;
            }

            if (imageUrl.StartsWith("images/", StringComparison.OrdinalIgnoreCase))
            {
                var normalizedPath = "/" + imageUrl.TrimStart('/');
                var localPath = normalizedPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var physicalPath = Path.Combine(_webHostEnvironment.WebRootPath, localPath);
                return System.IO.File.Exists(physicalPath) ? normalizedPath : fallbackImage;
            }

            var resolvedPath = "/images/" + imageUrl.TrimStart('/');
            var resolvedLocalPath = resolvedPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var resolvedPhysicalPath = Path.Combine(_webHostEnvironment.WebRootPath, resolvedLocalPath);
            return System.IO.File.Exists(resolvedPhysicalPath) ? resolvedPath : fallbackImage;
        }
    
        [Authorize]
        public async Task<IActionResult> Bookings(string? tab)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return RedirectToAction("Login");
            }

            var normalizedTab = string.IsNullOrWhiteSpace(tab) ? "all" : tab.Trim().ToLowerInvariant();
            if (normalizedTab is not ("all" or "upcoming" or "completed" or "cancelled"))
            {
                normalizedTab = "all";
            }

            var bookings = await _context.Bookings
                .Include(b => b.Tour)
                .Where(b => b.UserId == user.Id)
                .OrderByDescending(b => b.CreatedAt)
                .ToListAsync();

            var today = DateTime.Today;
            var minAllowedDateIso = today.AddDays(1).ToString("yyyy-MM-dd");

            var filteredBookings = normalizedTab switch
            {
                "upcoming" => bookings.Where(b => b.TravelDate.Date >= today
                                                  && !string.Equals(b.Status, "Cancelled", StringComparison.OrdinalIgnoreCase)),
                "completed" => bookings.Where(b => b.TravelDate.Date < today
                                                   && !string.Equals(b.Status, "Cancelled", StringComparison.OrdinalIgnoreCase)),
                "cancelled" => bookings.Where(b => string.Equals(b.Status, "Cancelled", StringComparison.OrdinalIgnoreCase)),
                _ => bookings
            };
            var paymentStatuses = await _context.Payments
                .AsNoTracking()
                .Where(payment => bookings.Select(booking => booking.BookingId).Contains(payment.BookingId))
                .OrderByDescending(payment => payment.CreatedAt)
                .Select(payment => new { payment.BookingId, payment.Status })
                .ToListAsync();
            var latestPaymentStatus = new Dictionary<int, string>();
            foreach (var payment in paymentStatuses)
            {
                latestPaymentStatus.TryAdd(payment.BookingId, payment.Status);
            }

            var model = new AccountBookingsViewModel
            {
                CurrentTab = normalizedTab,
                TotalBookings = bookings.Count,
                UpcomingBookings = bookings.Count(b => b.TravelDate.Date >= DateTime.Today && !string.Equals(b.Status, "Cancelled", StringComparison.OrdinalIgnoreCase)),
                CompletedBookings = bookings.Count(b => b.TravelDate.Date < DateTime.Today && !string.Equals(b.Status, "Cancelled", StringComparison.OrdinalIgnoreCase)),
                CancelledBookings = bookings.Count(b => string.Equals(b.Status, "Cancelled", StringComparison.OrdinalIgnoreCase)),
                Bookings = filteredBookings.Select(b => new AccountBookingRow(
                    b.BookingId,
                    b.Tour?.Name ?? "Tour",
                    b.Tour?.Location ?? string.Empty,
                    ResolveTourImageUrl(b.Tour?.ImageUrl, b.Tour?.Location, b.Tour?.Name),
                    b.TravelDate,
                    b.Tour?.DepartureTime ?? new TimeOnly(8, 0),
                    b.Tour?.ReturnTime ?? new TimeOnly(18, 0),
                    b.OriginalTravelDate,
                    b.RescheduledAt,
                    b.RescheduleNote,
                    b.GuestCount,
                    b.TotalPrice,
                    b.Status,
                    latestPaymentStatus.GetValueOrDefault(b.BookingId),
                    b.CancelledAt,
                    b.CancelReason,
                    CanCustomerReschedule(b, today),
                    CanCustomerCancel(b, today),
                    minAllowedDateIso)).ToList()
            };

            return View(model);
        }

        [Authorize]
        public async Task<IActionResult> Ticket(int bookingId)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return RedirectToAction("Login");

            var booking = await _context.Bookings
                .AsNoTracking()
                .Include(b => b.Tour)
                .FirstOrDefaultAsync(b => b.BookingId == bookingId && b.UserId == user.Id);
            if (booking == null)
                return NotFound();

            var payment = await _context.Payments
                .AsNoTracking()
                .Where(p => p.BookingId == bookingId)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();

            if (!string.Equals(booking.Status, "Confirmed", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(payment?.Status, "Paid", StringComparison.OrdinalIgnoreCase))
            {
                TempData["BookingError"] = "Vé điện tử sẽ hiển thị sau khi thanh toán được admin xác nhận.";
                return RedirectToAction(nameof(Bookings));
            }

            return View(new BookingTicketViewModel
            {
                BookingId = booking.BookingId,
                CustomerName = booking.FullName,
                CustomerPhone = booking.PhoneNumber,
                TourName = booking.Tour?.Name ?? "Tour",
                TourLocation = booking.Tour?.Location ?? string.Empty,
                TravelDate = booking.TravelDate,
                DepartureTime = booking.Tour?.DepartureTime ?? new TimeOnly(8, 0),
                ReturnTime = booking.Tour?.ReturnTime ?? new TimeOnly(18, 0),
                GuestCount = booking.GuestCount
            });
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelBooking(int bookingId, string reason)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return RedirectToAction("Login");
            }

            var booking = await _context.Bookings
                .FirstOrDefaultAsync(b => b.BookingId == bookingId && b.UserId == user.Id);

            if (booking == null)
            {
                TempData["BookingError"] = "Không tìm thấy booking cần hủy.";
                return RedirectToAction("Bookings");
            }

            if (!CanCustomerCancel(booking, DateTime.Today))
            {
                TempData["BookingError"] = "Booking này không thể hủy (đã quá ngày đi hoặc đã bị hủy).";
                return RedirectToAction("Bookings");
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                TempData["BookingError"] = "Vui lòng nhập lý do hủy.";
                return RedirectToAction("Bookings");
            }

            booking.Status = "Cancelled";
            booking.CancelledAt = DateTime.UtcNow;
            booking.CancelReason = reason.Trim();
            var payment = await _context.Payments
                .Where(p => p.BookingId == bookingId)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();
            if (payment != null)
                payment.Status = payment.Status == "Paid" ? "RefundPending" : "Cancelled";

            await _context.SaveChangesAsync();
            TempData["BookingSuccess"] = "Đã hủy booking của bạn.";
            return RedirectToAction("Bookings");
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RescheduleBooking(int bookingId, DateTime newTravelDate, string? note)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return RedirectToAction("Login");
            }

            var booking = await _context.Bookings
                .FirstOrDefaultAsync(b => b.BookingId == bookingId && b.UserId == user.Id);

            if (booking == null)
            {
                TempData["BookingError"] = "Không tìm thấy booking cần đổi lịch.";
                return RedirectToAction("Bookings");
            }

            if (!CanCustomerReschedule(booking, DateTime.Today))
            {
                TempData["BookingError"] = "Booking này không thể đổi lịch (đã quá ngày đi hoặc đã bị hủy).";
                return RedirectToAction("Bookings");
            }

            var normalizedDate = newTravelDate.Date;
            if (normalizedDate < DateTime.Today.AddDays(1))
            {
                TempData["BookingError"] = "Ngày đi mới phải từ ngày mai trở đi.";
                return RedirectToAction("Bookings");
            }

            if (normalizedDate == booking.TravelDate.Date)
            {
                TempData["BookingError"] = "Ngày đi mới phải khác ngày đi hiện tại.";
                return RedirectToAction("Bookings");
            }

            booking.OriginalTravelDate ??= booking.TravelDate;
            booking.TravelDate = normalizedDate;
            booking.RescheduledAt = DateTime.UtcNow;
            booking.RescheduleNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

            if (string.Equals(booking.Status, "Confirmed", StringComparison.OrdinalIgnoreCase))
            {
                booking.Status = "PendingConfirmation";
            }

            await _context.SaveChangesAsync();
            TempData["BookingSuccess"] = "Đã gửi yêu cầu đổi lịch. Vui lòng chờ xác nhận.";
            return RedirectToAction("Bookings");
        }

}
