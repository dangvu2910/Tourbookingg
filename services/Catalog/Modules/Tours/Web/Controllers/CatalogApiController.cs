using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Tourbooking.Controllers;

[ApiController]
[Route("api/tours")]
[Route("api/v1/catalog")]
public sealed class CatalogApiController(ApplicationDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(string? query, int page = 1)
    {
        var tours = db.Tours.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query)) tours = tours.Where(t => t.Name.Contains(query) || t.Location.Contains(query));
        page = Math.Clamp(page, 1, 10000);
        HttpContext.Items["Pagination"] = new { page, page_size = 20, total_items = await tours.CountAsync() };
        return Ok(await tours.OrderBy(t => t.TourId).Skip((page - 1) * 20).Take(20)
            .Select(t => new { t.TourId, t.Name, t.Location, t.Price, currency = "VND", t.DepartureTime,
                t.ReturnTime, t.Description, t.ImageUrl, t.CategoryId }).ToListAsync());
    }
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var tour = await db.Tours.AsNoTracking().Where(t => t.TourId == id)
            .Select(t => new { t.TourId, t.Name, t.Location, t.Price, currency = "VND", t.DepartureTime,
                t.ReturnTime, t.Description, t.ImageUrl, t.CategoryId }).FirstOrDefaultAsync();
        return tour == null ? NotFound() : Ok(tour);
    }
}

