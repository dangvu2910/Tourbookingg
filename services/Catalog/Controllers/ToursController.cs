using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Tourbooking.Models;
using Tourbooking.ViewModels;

namespace Tourbooking.Controllers;

public class ToursController : Controller
{

        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _webHostEnvironment;

        public ToursController(ApplicationDbContext context, IWebHostEnvironment webHostEnvironment)
        {
            _context = context;
            _webHostEnvironment = webHostEnvironment;
        }

        private bool TourExists(int id)
        {
            return _context.Tours.Any(e => e.TourId == id);
        }

        private void TryNormalizePriceFromRequest(Tour tour)
        {
            var rawPrice = Request.Form["Price"].ToString();

            if (string.IsNullOrWhiteSpace(rawPrice))
            {
                return;
            }

            var digitsOnly = new string(rawPrice.Where(char.IsDigit).ToArray());
            if (string.IsNullOrWhiteSpace(digitsOnly))
            {
                return;
            }

            if (decimal.TryParse(digitsOnly, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedPrice))
            {
                tour.Price = parsedPrice;
                ModelState.Remove(nameof(Tour.Price));
            }
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
                    "TP Hồ Chí Minh", "Ho Chi Minh", "Sài Gòn", "Sai Gon",
                    "Vũng Tàu", "Vung Tau", "Bà Rịa", "Ba Ria", "Cần Thơ", "Can Tho",
                    "Kiên Giang", "Kien Giang", "Phú Quốc", "Phu Quoc", "Côn Đảo", "Con Dao",
                    "Bình Ba", "Binh Ba", "Miền Tây", "Mien Tay"
                }
            };

            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var region in regions)
            {
                if (regionKeywords.TryGetValue(region, out var values))
                {
                    foreach (var value in values)
                    {
                        keywords.Add(value);
                    }
                }
            }

            return keywords.ToList();
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

        public async Task<IActionResult> Index(string? query, decimal? minPrice, decimal? maxPrice, string[]? regions, int page = 1)
        {
            const int pageSize = 6;

            var toursQuery = _context.Tours.AsQueryable();

            var selectedRegions = (regions ?? Array.Empty<string>())
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (selectedRegions.Length > 0)
            {
                var keywords = GetRegionKeywords(selectedRegions);
                if (keywords.Count > 0)
                {
                    toursQuery = toursQuery.Where(t => keywords.Any(k => t.Location.Contains(k)));
                }
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                toursQuery = toursQuery.Where(t => t.Name.Contains(query) || t.Location.Contains(query));
            }

            if (minPrice.HasValue)
            {
                toursQuery = toursQuery.Where(t => t.Price >= minPrice.Value);
            }

            if (maxPrice.HasValue)
            {
                toursQuery = toursQuery.Where(t => t.Price <= maxPrice.Value);
            }

            var totalTours = await toursQuery.CountAsync();
            var totalPages = (int)Math.Ceiling(totalTours / (double)pageSize);

            if (page < 1)
            {
                page = 1;
            }
            if (totalPages > 0 && page > totalPages)
            {
                page = totalPages;
            }

            var tours = await toursQuery
                .OrderBy(t => t.TourId)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewData["CurrentPage"] = page;
            ViewData["TotalPages"] = totalPages;
            ViewData["TotalTours"] = totalTours;
            ViewData["Query"] = query;
            ViewData["MinPrice"] = minPrice;
            ViewData["MaxPrice"] = maxPrice;
            ViewData["Regions"] = selectedRegions;

            return View(tours);
        }

        // GET: Tours/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tour = await _context.Tours
                .FirstOrDefaultAsync(m => m.TourId == id);
            if (tour == null)
            {
                return NotFound();
            }

            var currentUserId = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            var publicReviews = await _context.TourReviews
                .Include(r => r.User)
                .Include(r => r.Votes)
                .Where(r => r.TourId == tour.TourId)
                .OrderByDescending(r => r.CreatedAt)
                .Take(8)
                .Select(r => new PublicTourReviewRow(
                    r.ReviewId,
                    !string.IsNullOrWhiteSpace(r.User!.FullName) ? r.User!.FullName! : (r.User!.UserName ?? "Khách du lịch"),
                    r.Rating,
                    r.Title,
                    r.Content,
                    r.CreatedAt,
                    r.Votes.Count(v => v.Value == 1),
                    r.Votes.Count(v => v.Value == -1),
                    r.Votes.Where(v => v.UserId == currentUserId).Select(v => (int?)v.Value).FirstOrDefault()))
                .ToListAsync();

            ViewData["PublicReviews"] = publicReviews;
            ViewData["ReviewCount"] = publicReviews.Count;
            ViewData["AverageRating"] = publicReviews.Count > 0 ? publicReviews.Average(r => r.Rating) : 0d;

            return View(tour);
        }

        // GET: Tours/Create
        [Authorize(Roles = "Admin")]
        public IActionResult Create()
        {
            return View();
        }

        // POST: Tours/Create
        [Authorize(Roles = "Admin")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("TourId,Name,Location,Price,Description,CategoryId")] Tour tour, IFormFile? imageFile)
        {
            TryNormalizePriceFromRequest(tour);

            if (ModelState.IsValid)
            {
                try
                {
                    // Xử lý upload file
                    if (imageFile != null && imageFile.Length > 0)
                    {
                        string uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "images");
                        
                        if (!Directory.Exists(uploadsFolder))
                        {
                            Directory.CreateDirectory(uploadsFolder);
                        }

                        string uniqueFileName = Guid.NewGuid().ToString() + "_" + Path.GetFileName(imageFile.FileName);
                        string filePath = Path.Combine(uploadsFolder, uniqueFileName);

                        using (var fileStream = new FileStream(filePath, FileMode.Create))
                        {
                            await imageFile.CopyToAsync(fileStream);
                        }

                        tour.ImageUrl = "/images/" + uniqueFileName;
                    }

                    tour.TourId = 0;
                    _context.Add(tour);
                    await _context.SaveChangesAsync();
                    return RedirectToAction("Index");
                }
                catch (Exception ex)
                {
                    ModelState.AddModelError(string.Empty, "Lỗi: " + ex.Message);
                }
            }
            return View(tour);
        }

        // GET: Tours/Edit/5
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tour = await _context.Tours.FindAsync(id);
            if (tour == null)
            {
                return NotFound();
            }
            return View(tour);
        }

        // POST: Tours/Edit/5
        [Authorize(Roles = "Admin")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("TourId,Name,Location,Price,Description,CategoryId")] Tour tour, IFormFile? imageFile)
        {
            TryNormalizePriceFromRequest(tour);

            if (id != tour.TourId)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    var existingTour = await _context.Tours.FirstOrDefaultAsync(t => t.TourId == id);
                    if (existingTour == null)
                    {
                        return NotFound();
                    }

                    existingTour.Name = tour.Name;
                    existingTour.Location = tour.Location;
                    existingTour.Price = tour.Price;
                    existingTour.Description = tour.Description;
                    existingTour.CategoryId = tour.CategoryId;

                    if (imageFile != null && imageFile.Length > 0)
                    {
                        string uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "images");
                        
                        if (!Directory.Exists(uploadsFolder))
                        {
                            Directory.CreateDirectory(uploadsFolder);
                        }

                        if (!string.IsNullOrEmpty(existingTour.ImageUrl))
                        {
                            string oldImagePath = Path.Combine(_webHostEnvironment.WebRootPath, existingTour.ImageUrl.TrimStart('/'));
                            if (System.IO.File.Exists(oldImagePath))
                            {
                                System.IO.File.Delete(oldImagePath);
                            }
                        }

                        string uniqueFileName = Guid.NewGuid().ToString() + "_" + Path.GetFileName(imageFile.FileName);
                        string filePath = Path.Combine(uploadsFolder, uniqueFileName);

                        using (var fileStream = new FileStream(filePath, FileMode.Create))
                        {
                            await imageFile.CopyToAsync(fileStream);
                        }

                        existingTour.ImageUrl = "/images/" + uniqueFileName;
                    }

                    await _context.SaveChangesAsync();
                    return RedirectToAction("Tours", "Admin");
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!TourExists(tour.TourId))
                    {
                        return NotFound();
                    }
                    else
                    {
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    ModelState.AddModelError("imageFile", "Lỗi: " + ex.Message);
                }
            }
            return View(tour);
        }

        // GET: Tours/Delete/5
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var tour = await _context.Tours
                .FirstOrDefaultAsync(m => m.TourId == id);
            if (tour == null)
            {
                return NotFound();
            }

            return View(tour);
        }

        // POST: Tours/Delete/5
        [Authorize(Roles = "Admin")]
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var tour = await _context.Tours.FindAsync(id);
            if (tour == null)
            {
                return RedirectToAction("Tours", "Admin");
            }

            if (await _context.Bookings.AnyAsync(b => b.TourId == id))
            {
                TempData["BookingError"] = "Tour đã có booking, không thể xóa lịch sử đặt tour và thanh toán.";
                return RedirectToAction("Tours", "Admin");
            }

            _context.Tours.Remove(tour);
            await _context.SaveChangesAsync();
            return RedirectToAction("Tours", "Admin");
        }

}
