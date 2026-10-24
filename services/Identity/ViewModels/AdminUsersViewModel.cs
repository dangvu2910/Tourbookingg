namespace Tourbooking.ViewModels;

public class AdminUsersViewModel
{
    public int TotalUsers { get; set; }
    public int CurrentPage { get; set; }
    public int TotalPages { get; set; }
    public int PageSize { get; set; }
    public List<AdminUserRow> Users { get; set; } = new();
}

public record AdminUserRow(
    string Id,
    string Initials,
    string Name,
    string Email,
    string Role,
    string JoinedDate,
    string Status);
