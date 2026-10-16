using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using Tourbooking.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Tourbooking.ViewModels;

namespace Tourbooking.Controllers
{
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

        public async Task<IActionResult> Index()
        {
            var tours = await _context.Tours.AsNoTracking()
                .ToListAsync();

            var heroTours = tours
                .Where(t => !string.IsNullOrWhiteSpace(t.ImageUrl))
                .Take(3)
                .ToList();

            if (heroTours.Count < 3)
            {
                heroTours = tours.Take(3).ToList();
            }

            var dealsToday = tours
                .OrderBy(t => t.Price)
                .Take(3)
                .ToList();

            var monthlyDeals = tours
                .OrderByDescending(t => t.Price)
                .Take(3)
                .ToList();

            var topChoices = tours
                .OrderByDescending(t => t.TourId)
                .Take(4)
                .ToList();

            var promoCandidates = tours
                .Where(t => !string.IsNullOrWhiteSpace(t.ImageUrl))
                .Take(2)
                .ToList();

            var promoBanners = promoCandidates.Select((tour, index) => new HomePromoBanner
            {
                Title = index == 0 ? "Khuyến mãi hè rực rỡ" : "Gói gia đình tiết kiệm",
                Description = index == 0
                    ? "Giảm đến 30% cho tour biển đảo khi đặt sớm."
                    : "Miễn phí trẻ nhỏ dưới 5 tuổi cho các tour cuối tuần.",
                Cta = index == 0 ? "Đặt ngay" : "Liên hệ tư vấn",
                ImageUrl = NormalizeImageUrl(tour.ImageUrl) ?? string.Empty,
                TourId = tour.TourId
            }).ToList();

            var model = new HomePageViewModel
            {
                HeroTours = heroTours,
                DealsToday = dealsToday,
                MonthlyDeals = monthlyDeals,
                TopChoices = topChoices,
                PromoBanners = promoBanners
            };

            return View(model);
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> Contact()
        {
            var user = await _userManager.GetUserAsync(User);
            var lookupEmail = !string.IsNullOrWhiteSpace(user?.Email) ? user!.Email : null;

            var ticketsQuery = _context.ContactInquiries.AsNoTracking().AsQueryable();
            var ownerId = user?.Id;
            ticketsQuery = ticketsQuery.Where(c => ownerId != null && c.UserId == ownerId);
            if (!string.IsNullOrWhiteSpace(lookupEmail))
            {
                ticketsQuery = ticketsQuery.Where(c => c.Email == lookupEmail);
            }

            var model = new ContactPageViewModel
            {
                LookupEmail = lookupEmail,
                Form = new ContactViewModel
                {
                    FullName = user?.FullName ?? string.Empty,
                    Email = lookupEmail ?? string.Empty
                },
                Tickets = await ticketsQuery
                    .OrderByDescending(c => c.CreatedAt)
                    .Select(c => new ContactInquiryRow
                    {
                        ContactInquiryId = c.ContactInquiryId,
                        FullName = c.FullName,
                        Email = c.Email,
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
        public async Task<IActionResult> Contact(ContactViewModel model)
        {
            if (!ModelState.IsValid)
            {
                var invalidPageModel = await BuildContactPageViewModelAsync(model);
                return View(invalidPageModel);
            }

            var user = await _userManager.GetUserAsync(User);
            var ticket = new ContactInquiry
            {
                FullName = model.FullName.Trim(),
                Email = model.Email.Trim(),
                PhoneNumber = string.IsNullOrWhiteSpace(model.PhoneNumber) ? null : model.PhoneNumber.Trim(),
                Subject = model.Subject.Trim(),
                Message = model.Message.Trim(),
                UserId = user?.Id,
                Status = "Pending"
            };

            _context.ContactInquiries.Add(ticket);
            await _context.SaveChangesAsync();

            TempData["ContactMessage"] = "Yêu cầu của bạn đã được gửi. Khi admin trả lời, trạng thái sẽ hiển thị ngay tại đây.";
            return RedirectToAction("Contact");
        }

        private async Task<ContactPageViewModel> BuildContactPageViewModelAsync(ContactViewModel form)
        {
            var user = await _userManager.GetUserAsync(User);
            var lookupEmail = !string.IsNullOrWhiteSpace(form.Email)
                ? form.Email.Trim()
                : user?.Email;

            var ticketsQuery = _context.ContactInquiries.AsNoTracking().AsQueryable();
            var ownerId = user?.Id;
            ticketsQuery = ticketsQuery.Where(c => ownerId != null && c.UserId == ownerId);
            if (!string.IsNullOrWhiteSpace(lookupEmail))
            {
                ticketsQuery = ticketsQuery.Where(c => c.Email == lookupEmail);
            }

            return new ContactPageViewModel
            {
                LookupEmail = lookupEmail,
                Form = form,
                Tickets = await ticketsQuery
                    .OrderByDescending(c => c.CreatedAt)
                    .Select(c => new ContactInquiryRow
                    {
                        ContactInquiryId = c.ContactInquiryId,
                        FullName = c.FullName,
                        Email = c.Email,
                        Subject = c.Subject,
                        Message = c.Message,
                        Status = c.Status,
                        AdminReply = c.AdminReply,
                        CreatedAt = c.CreatedAt,
                        RepliedAt = c.RepliedAt
                    })
                    .ToListAsync()
            };
        }

        [Authorize]
        [HttpGet]
        public async Task<IActionResult> ContactDetails(int id)
        {
            var ticket = await _context.ContactInquiries
                .AsNoTracking()
                .Include(c => c.User)
                .FirstOrDefaultAsync(c => c.ContactInquiryId == id);

            if (ticket == null)
            {
                return NotFound();
            }

            if (!User.IsInRole("Admin") && ticket.UserId != _userManager.GetUserId(User)) return Forbid();

            var replies = await _context.ContactInquiryReplies
                .AsNoTracking()
                .Where(r => r.ContactInquiryId == id)
                .OrderBy(r => r.CreatedAt)
                .ToListAsync();

            var model = new ContactDetailsViewModel
            {
                Ticket = ticket,
                Replies = replies,
                ReplyForm = new ReplyCreateViewModel { ContactInquiryId = id }
            };

            return View(model);
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PostReply(ReplyCreateViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return RedirectToAction("ContactDetails", new { id = model.ContactInquiryId });
            }

            var ticket = await _context.ContactInquiries.FirstOrDefaultAsync(c => c.ContactInquiryId == model.ContactInquiryId);
            if (ticket == null)
            {
                return NotFound();
            }

            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return Challenge();
            }
            if (!User.IsInRole("Admin") && ticket.UserId != user.Id) return Forbid();

            var reply = new ContactInquiryReply
            {
                ContactInquiryId = model.ContactInquiryId,
                Message = model.Message.Trim(),
                UserId = user.Id,
                IsFromAdmin = false
            };

            _context.Add(reply);
            await _context.SaveChangesAsync();

            return RedirectToAction("ContactDetails", new { id = model.ContactInquiryId });
        }

        [Authorize]
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
                return RedirectToAction(nameof(Payment), new { bookingId = existing.BookingId });
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
                return RedirectToAction(nameof(Payment), new { bookingId = duplicate.BookingId });
            }

            if (string.IsNullOrWhiteSpace(payment.BankReference))
            {
                payment.BankReference = $"VOY-{booking.BookingId}";
                await _context.SaveChangesAsync();
            }

            TempData["BookingSuccess"] = "Đặt tour thành công. Vui lòng thanh toán để hoàn tất.";
            return RedirectToAction(nameof(Payment), new { bookingId = booking.BookingId });
        }

        [Authorize]
        [HttpGet]
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
                return RedirectToAction(nameof(Payment), new { bookingId });
            }

            if (string.Equals(booking.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                TempData["PaymentError"] = "Booking đã bị hủy, không thể xác nhận thanh toán.";
                return RedirectToAction(nameof(Payment), new { bookingId });
            }

            var payment = await _context.Payments
                .Where(p => p.BookingId == bookingId)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();

            if (payment == null)
            {
                TempData["PaymentError"] = "Không tìm thấy thông tin thanh toán.";
                return RedirectToAction(nameof(Payment), new { bookingId });
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
            return RedirectToAction(nameof(Index));
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
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

        private static bool IsDalatTour(string? name, string? location)
        {
            return ContainsDalat(name) || ContainsDalat(location);
        }

        private static bool ContainsDalat(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Contains("Đà Lạt", StringComparison.OrdinalIgnoreCase)
                || value.Contains("Da Lat", StringComparison.OrdinalIgnoreCase)
                || value.Contains("Dalat", StringComparison.OrdinalIgnoreCase);
        }
    }
}
