using System.Collections.Generic;
using Tourbooking.Models;

namespace Tourbooking.ViewModels;

public class HomePageViewModel
{
    public List<Tour> HeroTours { get; set; } = new();
    public List<Tour> DealsToday { get; set; } = new();
    public List<Tour> MonthlyDeals { get; set; } = new();
    public List<Tour> TopChoices { get; set; } = new();
    public List<HomePromoBanner> PromoBanners { get; set; } = new();
}

public class HomePromoBanner
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Cta { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public int? TourId { get; set; }
}
