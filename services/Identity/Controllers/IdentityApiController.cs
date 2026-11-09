using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;
using Tourbooking.Models;
using Tourbooking.Distributed;

namespace Tourbooking.Controllers;

[ApiController]
[Route("api/identity")]
[Route("api/v1/auth")]
public sealed class IdentityApiController(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, IConfiguration config) : ControllerBase
{
    public sealed class LoginRequest
    {
        [Required, EmailAddress, StringLength(254)] public string Email { get; init; } = "";
        [Required, StringLength(256)] public string Password { get; init; } = "";
    }
    [AllowAnonymous, HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        if (string.IsNullOrEmpty(config["Jwt:SigningKey"])) return StatusCode(503);
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user == null) return Unauthorized();
        var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded) return Unauthorized();
        if (user.TwoFactorEnabled) return StatusCode(403, "Tài khoản bật xác thực hai bước phải dùng luồng đăng nhập tương ứng.");
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Name, user.UserName ?? user.Email ?? user.Id),
            new(ApiBearerAuthentication.StampClaim, ApiBearerAuthentication.HashStamp(user.SecurityStamp))
        };
        claims.AddRange((await users.GetRolesAsync(user)).Select(role => new Claim(ClaimTypes.Role, role)));
        return Ok(new { access_token = ApiBearerAuthentication.Issue(config, claims, DateTime.UtcNow), token_type = "Bearer", expires_in = 900 });
    }
    [Authorize, HttpPost("logout")]
    public async Task<IActionResult> LogoutToken()
    {
        if (string.IsNullOrEmpty(config["Jwt:SigningKey"])) return StatusCode(503);
        if (!Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Unauthorized();
        var user = await users.GetUserAsync(User);
        if (user == null) return Unauthorized();
        var result = await users.UpdateSecurityStampAsync(user);
        return result.Succeeded ? NoContent() : StatusCode(500);
    }
    [Authorize]
    [HttpGet("me")]
    public IActionResult Me() => Ok(new { id = User.FindFirstValue(ClaimTypes.NameIdentifier), name = User.Identity?.Name, roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value) });
}
