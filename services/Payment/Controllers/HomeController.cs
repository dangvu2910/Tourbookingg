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

        public async Task<IActionResult> Payment(int bookingId)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return RedirectToAction("Login", "Account");
            }

            var booking = await _context.Bookings
                .AsNoTracking()
                .Include(b => b.Tour)
                .FirstOrDefaultAsync(b => b.BookingId == bookingId && b.UserId == user.Id);

            if (booking == null)
            {
                TempData["BookingError"] = "Không tìm thấy booking để thanh toán.";
                return RedirectToAction("Bookings", "Account");
            }

            var payment = await _context.Payments
                .AsNoTracking()
                .Where(p => p.BookingId == bookingId)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();

            if (payment == null)
            {
                TempData["BookingError"] = "Không tìm thấy thông tin thanh toán.";
                return RedirectToAction("Bookings", "Account");
            }

            var bankCode = _configuration["BankTransfer:BankCode"]
                           ?? payment.BankName
                           ?? "BIDV";

            var accountNumber = _configuration["BankTransfer:AccountNumber"]
                                ?? payment.BankAccountNumber
                                ?? "";

            var accountName = _configuration["BankTransfer:AccountName"]
                              ?? payment.BankAccountName
                              ?? "";

            var addInfo = payment.BankReference;
            if (string.IsNullOrWhiteSpace(addInfo))
            {
                addInfo = $"VOY-{booking.BookingId}";
            }

            var amount = (int)Math.Max(0m, decimal.Round(payment.Amount, 0));
            var qrUrl = string.Empty;

            if (!string.IsNullOrWhiteSpace(bankCode) && !string.IsNullOrWhiteSpace(accountNumber))
            {
                qrUrl = $"https://api.vietqr.io/image/{bankCode}-{accountNumber}-compact2.png?amount={amount}";

                if (!string.IsNullOrWhiteSpace(addInfo))
                {
                    qrUrl += "&addInfo=" + Uri.EscapeDataString(addInfo);
                }

                if (!string.IsNullOrWhiteSpace(accountName))
                {
                    qrUrl += "&accountName=" + Uri.EscapeDataString(accountName);
                }
            }

            var model = new PaymentPageViewModel
            {
                BookingId = booking.BookingId,
                TourName = booking.Tour?.Name ?? "Tour",
                TourLocation = booking.Tour?.Location ?? string.Empty,
                TravelDate = booking.TravelDate,
                GuestCount = booking.GuestCount,
                Amount = payment.Amount,
                PaymentMethod = payment.Method,
                BankCode = bankCode,
                AccountNumber = accountNumber,
                AccountName = accountName,
                AddInfo = addInfo,
                QrImageUrl = string.IsNullOrWhiteSpace(qrUrl) ? Url.Content("~/images/qrthanhtoan.jpg") : qrUrl
            };

            return View(model);
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmPayment(int bookingId, string? transactionCode)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return RedirectToAction("Login", "Account");
            }

            var booking = await _context.Bookings
                .FirstOrDefaultAsync(b => b.BookingId == bookingId && b.UserId == user.Id);

            if (booking == null)
            {
                TempData["PaymentError"] = "Không tìm thấy booking để xác nhận thanh toán.";
                return RedirectToAction("Payment", new { bookingId });
            }

            if (string.Equals(booking.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                TempData["PaymentError"] = "Booking đã bị hủy, không thể xác nhận thanh toán.";
                return RedirectToAction("Payment", new { bookingId });
            }

            var payment = await _context.Payments
                .Where(p => p.BookingId == bookingId)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();

            if (payment == null)
            {
                TempData["PaymentError"] = "Không tìm thấy thông tin thanh toán.";
                return RedirectToAction("Payment", new { bookingId });
            }

            var code = string.IsNullOrWhiteSpace(transactionCode) ? null : transactionCode.Trim();
            if (booking.Status == "Confirmed" || payment.Status is "Paid" or "Processing")
                return RedirectToAction("Bookings", "Account");
            if (code?.Length > 80) return BadRequest("Mã giao dịch tối đa 80 ký tự.");
            if (!string.IsNullOrWhiteSpace(code))
            {
                payment.TransactionCode = code;
            }

            payment.Status = "Processing";

            if (!string.Equals(booking.Status, "Confirmed", StringComparison.OrdinalIgnoreCase))
            {
                booking.Status = "Processing";
            }

            await _context.SaveChangesAsync();

            TempData["BookingSuccess"] = "Đặt tour thành công. Đã ghi nhận thanh toán.";
            return RedirectToAction("Index");
        }


    
}
