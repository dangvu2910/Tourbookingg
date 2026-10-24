using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Tourbooking.Controllers;

[ApiController]
[Route("api/identity")]
public sealed class IdentityApiController : ControllerBase
{
    [Authorize]
    [HttpGet("me")]
    public IActionResult Me() => Ok(new { id = User.FindFirstValue(ClaimTypes.NameIdentifier), name = User.Identity?.Name, roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value) });
}
