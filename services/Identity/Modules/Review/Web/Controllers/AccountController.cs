using System.Security.Claims;
using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tourbooking.Models;
using Tourbooking.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace Tourbooking.Controllers;

public class AccountController : Controller
{

        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _webHostEnvironment;

        public AccountController(
            ApplicationDbContext context,
            IWebHostEnvironment webHostEnvironment)
        {
            _context = context;
            _webHostEnvironment = webHostEnvironment;
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
        public async Task<IActionResult> Reviews()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return RedirectToAction("Login");
            }

            var completedBookings = await _context.Bookings
                .Include(b => b.Tour)
                .Where(b => b.UserId == userId
                    && b.Status != "Cancelled"
                    && (b.TravelDate.Date < DateTime.Today || b.Status == "Confirmed"))
                .OrderByDescending(b => b.TravelDate)
                .ToListAsync();

            var bookingIds = completedBookings.Select(b => b.BookingId).ToList();
            var reviews = await _context.TourReviews
                .Where(r => r.UserId == userId && bookingIds.Contains(r.BookingId))
                .ToListAsync();

            var model = new AccountReviewsViewModel
            {
                Reviews = completedBookings.Select(booking =>
                {
                    var review = reviews.FirstOrDefault(r => r.BookingId == booking.BookingId);
                    var canReview = review == null &&
                        (string.Equals(booking.Status, "Confirmed", StringComparison.OrdinalIgnoreCase)
                         || (booking.TravelDate.Date < DateTime.Today && !string.Equals(booking.Status, "Cancelled", StringComparison.OrdinalIgnoreCase)));

                    return new AccountReviewRow(
                        booking.BookingId,
                        booking.TourId,
                        booking.TourNameSnapshot ?? booking.Tour?.Name ?? "Tour",
                        booking.TourLocationSnapshot ?? booking.Tour?.Location ?? string.Empty,
                        ResolveTourImageUrl(booking.Tour?.ImageUrl, booking.Tour?.Location, booking.Tour?.Name),
                        booking.TravelDate,
                        booking.Status,
                        canReview,
                        review != null,
                        review?.Rating,
                        review?.Title,
                        review?.Content,
                        review?.CreatedAt);
                }).ToList()
            };

            return View(model);
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Reviews(int bookingId, int rating, string title, string content)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return RedirectToAction("Login");
            }

            var booking = await _context.Bookings
                .Include(b => b.Tour)
                .FirstOrDefaultAsync(b => b.BookingId == bookingId && b.UserId == userId);

            if (booking == null)
            {
                TempData["ReviewMessage"] = "Không tìm thấy booking cần đánh giá.";
                return RedirectToAction("Reviews");
            }

            var isCancelled = string.Equals(booking.Status, "Cancelled", StringComparison.OrdinalIgnoreCase);
            var isEligible = string.Equals(booking.Status, "Confirmed", StringComparison.OrdinalIgnoreCase)
                             || (booking.TravelDate.Date < DateTime.Today && !isCancelled);
            if (!isEligible)
            {
                TempData["ReviewMessage"] = "Bạn chỉ có thể đánh giá các tour đã hoàn thành hoặc đã được admin xác nhận.";
                return RedirectToAction("Reviews");
            }

            var existingReview = await _context.TourReviews
                .FirstOrDefaultAsync(r => r.BookingId == booking.BookingId && r.UserId == userId);

            if (existingReview != null)
            {
                TempData["ReviewMessage"] = "Tour này đã được đánh giá trước đó.";
                return RedirectToAction("Reviews");
            }

            if (rating < 1 || rating > 5 || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content))
            {
                TempData["ReviewMessage"] = "Vui lòng nhập đủ thông tin đánh giá.";
                return RedirectToAction("Reviews");
            }

            var review = new TourReview
            {
                BookingId = booking.BookingId,
                TourId = booking.TourId,
                UserId = userId,
                Rating = rating,
                Title = title.Trim(),
                Content = content.Trim(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.TourReviews.Add(review);
            await _context.SaveChangesAsync();

            TempData["ReviewMessage"] = "Đã lưu đánh giá của bạn.";
            return RedirectToAction("Reviews");
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VoteReview(int reviewId, int value)
        {
            if (value != 1 && value != -1)
            {
                return BadRequest();
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            var review = await _context.TourReviews.FirstOrDefaultAsync(r => r.ReviewId == reviewId);
            if (review == null)
            {
                return NotFound();
            }

            if (string.Equals(review.UserId, userId, StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            var existing = await _context.TourReviewVotes.FirstOrDefaultAsync(v => v.ReviewId == reviewId && v.UserId == userId);
            if (existing == null)
            {
                var vote = new TourReviewVote
                {
                    ReviewId = reviewId,
                    UserId = userId,
                    Value = value,
                    CreatedAt = DateTime.UtcNow
                };
                _context.TourReviewVotes.Add(vote);
            }
            else if (existing.Value == value)
            {
                _context.TourReviewVotes.Remove(existing);
            }
            else
            {
                existing.Value = value;
                existing.CreatedAt = DateTime.UtcNow;
                _context.TourReviewVotes.Update(existing);
            }

            await _context.SaveChangesAsync();

            var up = await _context.TourReviewVotes.CountAsync(v => v.ReviewId == reviewId && v.Value == 1);
            var down = await _context.TourReviewVotes.CountAsync(v => v.ReviewId == reviewId && v.Value == -1);
            var userVote = await _context.TourReviewVotes.Where(v => v.ReviewId == reviewId && v.UserId == userId).Select(v => (int?)v.Value).FirstOrDefaultAsync();

            return Json(new { upvotes = up, downvotes = down, userVote });
        }


    
}
