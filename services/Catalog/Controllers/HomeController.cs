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

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }

}
