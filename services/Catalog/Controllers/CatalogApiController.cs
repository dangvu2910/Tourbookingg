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

