namespace Tourbooking.ViewModels;

public class AdminDashboardViewModel
{
    public int TotalTours { get; set; }
    public int TotalBookings { get; set; }
    public int TotalUsers { get; set; }
    public List<AdminTourCard> TopTours { get; set; } = new();
    public List<AdminActivity> RecentActivities { get; set; } = new();
}

public record AdminActivity(string Title, string Description);
