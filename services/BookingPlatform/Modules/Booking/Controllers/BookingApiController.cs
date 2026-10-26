using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Tourbooking.Controllers;

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

