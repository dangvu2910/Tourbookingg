using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using Tourbooking.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Tourbooking.ViewModels;

namespace Tourbooking.Controllers;

public class HomeController : Controller
{

        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IConfiguration _configuration;

        public HomeController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IConfiguration configuration)
        {
            _context = context;
            _userManager = userManager;
            _configuration = configuration;
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

            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return RedirectToAction("Login", "Account");
            }

            var paymentMethod = "BankTransfer";

            if (model.RequestId == Guid.Empty) ModelState.AddModelError(nameof(model.RequestId), "Mã yêu cầu không hợp lệ.");
            if (model.TravelDate?.Date < DateTime.Today.AddDays(1)) ModelState.AddModelError(nameof(model.TravelDate), "Ngày khởi hành phải từ ngày mai.");
            var existing = await _context.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.RequestId == model.RequestId);
            if (existing != null)
            {
                if (existing.UserId != user.Id) return Conflict();
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
            var total = (tour.Price * model.GuestCount) + serviceFee + localTax;
            var paymentStatus = "PendingConfirmation";

            var booking = new Booking
            {
                RequestId = model.RequestId,
                TourId = tour.TourId,
                UserId = user.Id,
                FullName = model.FullName.Trim(),
                Email = model.Email.Trim(),
                PhoneNumber = model.PhoneNumber.Trim(),
                TravelDate = model.TravelDate ?? DateTime.Today,
                GuestCount = model.GuestCount,
                TotalPrice = total,
                Status = paymentStatus
            };

            var bankCode = _configuration["BankTransfer:BankCode"];
            var accountNumber = _configuration["BankTransfer:AccountNumber"];
            var accountName = _configuration["BankTransfer:AccountName"];

            var payment = new Payment
            {
                Booking = booking,
                UserId = user.Id,
                Amount = total,
                Method = paymentMethod,
                Status = paymentStatus,
                Provider = null,
                TransactionCode = null,
                BankName = bankCode?.Trim(),
                BankAccountName = accountName?.Trim(),
                BankAccountNumber = accountNumber?.Trim(),
                BankReference = null
            };

            _context.Bookings.Add(booking);
            _context.Payments.Add(payment);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException sql && sql.Number is 2601 or 2627)
            {
                _context.ChangeTracker.Clear();
                var duplicate = await _context.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.RequestId == model.RequestId && b.UserId == user.Id);
                if (duplicate == null) throw;
                return RedirectToAction("Payment", new { bookingId = duplicate.BookingId });
            }

            if (string.IsNullOrWhiteSpace(payment.BankReference))
            {
                payment.BankReference = $"VOY-{booking.BookingId}";
                await _context.SaveChangesAsync();
            }

            TempData["BookingSuccess"] = "Đặt tour thành công. Vui lòng thanh toán để hoàn tất.";
            return RedirectToAction("Payment", new { bookingId = booking.BookingId });
        }

}
