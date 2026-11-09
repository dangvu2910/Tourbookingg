using System.Security.Claims;
using Tourbooking.Distributed;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using Tourbooking.Models;
using Microsoft.EntityFrameworkCore;
using Tourbooking.ViewModels;

namespace Tourbooking.Controllers;

public class HomeController : Controller
{

        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;

        public HomeController(ApplicationDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        [Authorize]
        public async Task<IActionResult> Payment(int bookingId)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return RedirectToAction("Login", "Account");
            }

            var booking = await _context.Bookings
                .AsNoTracking()
                .Include(b => b.Tour)
                .FirstOrDefaultAsync(b => b.BookingId == bookingId && b.UserId == userId);

            if (booking == null)
            {
                TempData["BookingError"] = "Không tìm thấy booking để thanh toán.";
                return RedirectToAction("Bookings", "Account");
            }

            var payment = await _context.Payments
                .AsNoTracking()
                .Where(p => p.BookingId == bookingId)
                .OrderByDescending(p => p.CreatedAt)
                .ThenByDescending(p => p.PaymentId)
                .FirstOrDefaultAsync();

            if (payment == null)
            {
                return View("PaymentPending", bookingId);
            }

            if (!CanAcknowledgeTransfer(booking, payment))
            {
                TempData["BookingError"] = "Booking hoặc thanh toán không còn hiệu lực. Nếu đã chuyển khoản, hãy liên hệ hỗ trợ để đối soát; không chuyển khoản lại.";
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

            var amount = Math.Max(0m, decimal.Round(payment.Amount, 0)).ToString("0", CultureInfo.InvariantCulture);
            var qrUrl = string.Empty;

            if (!string.IsNullOrWhiteSpace(bankCode) && !string.IsNullOrWhiteSpace(accountNumber))
            {
                qrUrl = $"https://api.vietqr.io/image/{Uri.EscapeDataString(bankCode)}-{Uri.EscapeDataString(accountNumber)}-compact2.png?amount={amount}";

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
                TourName = booking.TourNameSnapshot ?? booking.Tour?.Name ?? "Tour",
                TourLocation = booking.TourLocationSnapshot ?? booking.Tour?.Location ?? string.Empty,
                TravelDate = booking.TravelDate,
                GuestCount = booking.GuestCount,
                Amount = payment.Amount,
                BookingStatus = booking.Status,
                PaymentStatus = payment.Status,
                PaymentMethod = payment.Method,
                BankCode = bankCode,
                AccountNumber = accountNumber,
                AccountName = accountName,
                AddInfo = addInfo,
                QrImageUrl = qrUrl
            };

            return View(model);
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmPayment(int bookingId, string? transactionCode, string? bookingVersion = null)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return RedirectToAction("Login", "Account");
            }

            var booking = await _context.Bookings
                .FirstOrDefaultAsync(b => b.BookingId == bookingId && b.UserId == userId);

            if (booking == null)
            {
                return BookingError("Không tìm thấy booking để xác nhận thanh toán.");
            }

            // Reservation reconciliation can change Version while the QR page is open.
            // A transfer acknowledgement must evaluate current state instead of the form version.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var payment = await _context.Payments
                    .Where(p => p.BookingId == bookingId)
                    .OrderByDescending(p => p.CreatedAt)
                    .ThenByDescending(p => p.PaymentId)
                    .FirstOrDefaultAsync();

                if (payment == null)
                {
                    return BookingError("Không tìm thấy thông tin thanh toán. Nếu đã chuyển khoản, hãy liên hệ hỗ trợ; không chuyển khoản lại.");
                }

                var code = string.IsNullOrWhiteSpace(transactionCode) ? null : transactionCode.Trim();
                if (!CanAcknowledgeTransfer(booking, payment))
                {
                    return BookingError("Booking hoặc thanh toán không còn hiệu lực. Nếu đã chuyển khoản, hãy liên hệ hỗ trợ để đối soát; không chuyển khoản lại.");
                }
                if (payment.HasTransferAcknowledgement())
                {
                    TempData["BookingSuccess"] = "Yêu cầu thanh toán đã được ghi nhận. Bạn không cần chuyển khoản lại.";
                    return RedirectToAction("Bookings", "Account");
                }
                if (code?.Length > 80) return BadRequest("Mã giao dịch tối đa 80 ký tự.");
                if (!string.IsNullOrWhiteSpace(code))
                {
                    payment.TransactionCode = code;
                }

                payment.Status = "Processing";
                booking.Status = "Processing";

                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    // SaveChanges is atomic. Reload before checking a concurrent cancellation,
                    // approval or duplicate acknowledgement; this never initiates a bank transfer.
                    await _context.Entry(booking).ReloadAsync();
                    await _context.Entry(payment).ReloadAsync();
                    if (_context.Entry(booking).State == EntityState.Detached) break;
                    continue;
                }

                TempData["BookingSuccess"] = "Đã ghi nhận thông báo chuyển khoản, đang chờ đối soát. Bạn không cần chuyển khoản lại.";
                return RedirectToAction("Bookings", "Account");
            }

            return BookingError("Booking đang cập nhật. Hãy kiểm tra trạng thái hoặc liên hệ hỗ trợ nếu đã chuyển khoản; không chuyển khoản lại.");
        }

        private static bool CanAcknowledgeTransfer(Booking booking, Payment payment) =>
            !string.Equals(booking.Status, "Cancelled", StringComparison.OrdinalIgnoreCase)
            && payment.Status is ("Pending" or "PendingConfirmation" or "Processing" or "Paid")
            && booking.SeatReservationState is not ("Released" or "Expired");

        private IActionResult BookingError(string message)
        {
            TempData["BookingError"] = message;
            return RedirectToAction("Bookings", "Account");
        }
}
