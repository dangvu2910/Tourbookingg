using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using Tourbooking.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Tourbooking.ViewModels;
using Tourbooking.Contracts;

namespace Tourbooking.Controllers;

public class HomeController : Controller
{

        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IConfiguration _configuration;
        private readonly ISeatReservationClient _reservations;

        public HomeController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IConfiguration configuration, ISeatReservationClient reservations)
        {
            _context = context;
            _userManager = userManager;
            _configuration = configuration;
            _reservations = reservations;
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

    
        public async Task<IActionResult> Booking(int? tourId)
        {
            if (!tourId.HasValue)
            {
                TempData["BookingMessage"] = "Vui lòng chọn tour trước khi đặt chỗ.";
                return RedirectToAction("Index", "Tours");
            }

            var tour = await _context.Tours.AsNoTracking().FirstOrDefaultAsync(t => t.TourId == tourId.Value);
            if (tour == null)
            {
                TempData["BookingMessage"] = "Tour không tồn tại hoặc đã bị gỡ.";
                return RedirectToAction("Index", "Tours");
            }

            var user = await _userManager.GetUserAsync(User);
            var model = new BookingCreateViewModel
            {
                TourId = tour.TourId,
                TourName = tour.Name,
                TourLocation = tour.Location,
                TourImageUrl = NormalizeImageUrl(tour.ImageUrl),
                TourPrice = tour.Price,
                FullName = user?.FullName ?? string.Empty,
                Email = user?.Email ?? string.Empty,
                PhoneNumber = user?.PhoneNumber ?? string.Empty,
                GuestCount = 2,
                PaymentMethod = "Card"
            };

            return View(model);
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Booking(BookingCreateViewModel model)
        {
            var tour = await _context.Tours.AsNoTracking().FirstOrDefaultAsync(t => t.TourId == model.TourId);
            if (tour == null)
            {
                TempData["BookingMessage"] = "Tour không tồn tại hoặc đã bị gỡ.";
                return RedirectToAction("Index", "Tours");
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return RedirectToAction("Login", "Account");
            }


            if (model.RequestId == Guid.Empty) ModelState.AddModelError(nameof(model.RequestId), "Mã yêu cầu không hợp lệ.");
            if (model.TravelDate?.Date < DateTime.Today.AddDays(1)) ModelState.AddModelError(nameof(model.TravelDate), "Ngày khởi hành phải từ ngày mai.");
            var existing = await _context.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.RequestId == model.RequestId);
            if (existing != null)
            {
                if (!MatchesRequest(existing, model, userId)) return Conflict();
                return RedirectToAction("Payment", new { bookingId = existing.BookingId });
            }

            if (!ModelState.IsValid)
            {
                model.TourName = tour.Name;
                model.TourLocation = tour.Location;
                model.TourImageUrl = NormalizeImageUrl(tour.ImageUrl);
                model.TourPrice = tour.Price;
                return View(model);
            }

            const decimal serviceFee = 45000m;
            const decimal localTax = 120000m;
            ReservedSeats? reserved = null;
            if (_configuration.GetValue("Reservations:Enabled", false))
            {
                try { reserved = await _reservations.ReserveAsync(new(model.RequestId, tour.TourId, userId, model.TravelDate!.Value, model.GuestCount), HttpContext.RequestAborted); }
                catch (ReservationException ex) { return Conflict(ex.Message); }
                catch (HttpRequestException) { return StatusCode(503, "Catalog tạm thời không thể giữ chỗ. Vui lòng thử lại với cùng yêu cầu."); }
                catch (TaskCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested) { return StatusCode(504); }
            }
            var total = ((reserved?.UnitPrice ?? tour.Price) * model.GuestCount) + serviceFee + localTax;
            var paymentStatus = "PendingConfirmation";

            var booking = new Booking
            {
                RequestId = model.RequestId,
                SeatReservationId = reserved?.ReservationId,
                SeatReservationState = reserved?.Status,
                TourId = tour.TourId,
                UserId = userId,
                TourNameSnapshot = reserved?.TourName ?? tour.Name,
                TourLocationSnapshot = reserved?.TourLocation ?? tour.Location,
                DepartureTimeSnapshot = reserved?.DepartureTime ?? tour.DepartureTime,
                ReturnTimeSnapshot = reserved?.ReturnTime ?? tour.ReturnTime,
                FullName = model.FullName.Trim(),
                Email = model.Email.Trim(),
                PhoneNumber = model.PhoneNumber.Trim(),
                TravelDate = model.TravelDate ?? DateTime.Today,
                GuestCount = model.GuestCount,
                TotalPrice = total,
                Status = paymentStatus
            };

            await using var transaction = await _context.Database.BeginTransactionAsync();
            _context.Bookings.Add(booking);
            try
            {
                await _context.SaveChangesAsync();
                _context.Payments.Add(new Payment
                {
                    BookingId = booking.BookingId,
                    UserId = userId,
                    Amount = total,
                    Method = "BankTransfer",
                    Status = "PendingConfirmation",
                    BankName = _configuration["BankTransfer:BankCode"],
                    BankAccountNumber = _configuration["BankTransfer:AccountNumber"],
                    BankAccountName = _configuration["BankTransfer:AccountName"],
                    BankReference = $"VOY-{booking.BookingId}"
                });
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 }
                or Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
            {
                await transaction.RollbackAsync();
                _context.ChangeTracker.Clear();
                var duplicate = await _context.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.RequestId == model.RequestId && b.UserId == userId);
                if (duplicate == null) throw;
                if (!MatchesRequest(duplicate, model, userId)) return Conflict();
                return RedirectToAction("Payment", new { bookingId = duplicate.BookingId });
            }

            TempData["BookingSuccess"] = "Đặt tour thành công. Vui lòng thanh toán để hoàn tất.";
            return RedirectToAction("Payment", new { bookingId = booking.BookingId });
        }

        private static bool MatchesRequest(Booking booking, BookingCreateViewModel model, string userId) =>
            booking.UserId == userId && booking.TourId == model.TourId
            && booking.GuestCount == model.GuestCount && booking.TravelDate.Date == model.TravelDate?.Date
            && booking.FullName == model.FullName?.Trim() && booking.Email == model.Email?.Trim()
            && booking.PhoneNumber == model.PhoneNumber?.Trim();
}
