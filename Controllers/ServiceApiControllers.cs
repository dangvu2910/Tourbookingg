using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Tourbooking.Controllers;

[ApiController]
[Route("api/tours")]
public sealed class CatalogApiController(ApplicationDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(string? query, int page = 1)
    {
        var tours = db.Tours.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query)) tours = tours.Where(t => t.Name.Contains(query) || t.Location.Contains(query));
        return Ok(await tours.OrderBy(t => t.TourId).Skip((Math.Clamp(page, 1, 10000) - 1) * 20).Take(20).ToListAsync());
    }
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id) => await db.Tours.AsNoTracking().FirstOrDefaultAsync(t => t.TourId == id) is { } tour ? Ok(tour) : NotFound();
}

[ApiController]
[Authorize]
[Route("api/bookings")]
public sealed class BookingApiController(ApplicationDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List() => Ok(await db.Bookings.AsNoTracking()
        .Where(b => b.UserId == User.FindFirstValue(ClaimTypes.NameIdentifier))
        .OrderByDescending(b => b.CreatedAt).Take(100)
        .Select(b => new { b.BookingId, b.TourId, b.TravelDate, b.GuestCount, b.TotalPrice, b.Status, b.CreatedAt }).ToListAsync());

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var booking = await db.Bookings.AsNoTracking()
            .Where(b => b.BookingId == id && b.UserId == User.FindFirstValue(ClaimTypes.NameIdentifier))
            .Select(b => new { b.BookingId, b.TourId, b.TravelDate, b.GuestCount, b.TotalPrice, b.Status, b.CreatedAt }).FirstOrDefaultAsync();
        return booking == null ? NotFound() : Ok(booking);
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("outbox")]
    public async Task<IActionResult> Outbox() => Ok(new
    {
        pending = await db.OutboxMessages.CountAsync(m => m.PublishedAt == null),
        published = await db.OutboxMessages.CountAsync(m => m.PublishedAt != null),
        retrying = await db.OutboxMessages.CountAsync(m => m.PublishedAt == null && m.Attempts > 0)
    });
}

[ApiController]
[Route("api/identity")]
public sealed class IdentityApiController : ControllerBase
{
    [Authorize]
    [HttpGet("me")]
    public IActionResult Me() => Ok(new { id = User.FindFirstValue(ClaimTypes.NameIdentifier), name = User.Identity?.Name, roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value) });
}
