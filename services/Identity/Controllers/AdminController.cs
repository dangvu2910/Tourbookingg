using System;
using System.Globalization;
using System.IO;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tourbooking.Models;
using Tourbooking.ViewModels;

namespace Tourbooking.Controllers;

[Authorize(Roles = "Admin")]
public class AdminController : Controller
{

    private static readonly CultureInfo VnCulture = CultureInfo.GetCultureInfo("vi-VN");
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public AdminController(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    private static string GetInitials(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "?";
        }

        var parts = value
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 1)
        {
            return parts[0].Length >= 2
                ? string.Concat(parts[0][0], parts[0][1]).ToUpperInvariant()
                : parts[0][0].ToString().ToUpperInvariant();
        }

        var first = parts[0][0];
        var last = parts[^1][0];
        return string.Concat(first, last).ToUpperInvariant();
    }

    public async Task<IActionResult> Users(int page = 1)
    {
        const int pageSize = 10;
        var usersQuery = _userManager.Users.AsNoTracking().OrderBy(u => u.Email);
        var totalUsers = await usersQuery.CountAsync();
        var totalPages = (int)Math.Ceiling(totalUsers / (double)pageSize);

        if (totalPages == 0)
        {
            totalPages = 1;
        }

        if (page < 1)
        {
            page = 1;
        }

        if (page > totalPages)
        {
            page = totalPages;
        }

        var users = await usersQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var rows = new List<AdminUserRow>(users.Count);
        foreach (var user in users)
        {
            var roles = await _userManager.GetRolesAsync(user);
            var role = roles.FirstOrDefault() ?? "User";
            var status = user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTimeOffset.UtcNow
                ? "Paused"
                : "Active";
            var displayName = string.IsNullOrWhiteSpace(user.UserName) ? user.Email ?? "" : user.UserName;
            var joinedDate = user.CreatedAt == default
                ? "-"
                : user.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy");
            rows.Add(new AdminUserRow(
                user.Id,
                GetInitials(displayName),
                displayName,
                user.Email ?? string.Empty,
                role,
                joinedDate,
                status));
        }

        var model = new AdminUsersViewModel
        {
            TotalUsers = totalUsers,
            CurrentPage = page,
            TotalPages = totalPages,
            PageSize = pageSize,
            Users = rows
        };

        return View(model);
    }

    public async Task<IActionResult> EditUser(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return NotFound();
        }

        var user = await _userManager.FindByIdAsync(id);
        if (user == null)
        {
            return NotFound();
        }

        var roles = await _userManager.GetRolesAsync(user);
        var model = new EditUserViewModel
        {
            Id = user.Id,
            Email = user.Email ?? string.Empty,
            FullName = user.FullName,
            PhoneNumber = user.PhoneNumber,
            Address = user.Address,
            Role = roles.FirstOrDefault() ?? "User"
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditUser(EditUserViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await _userManager.FindByIdAsync(model.Id);
        if (user == null)
        {
            return NotFound();
        }

        if (!string.Equals(user.Email, model.Email, StringComparison.OrdinalIgnoreCase))
        {
            var setEmailResult = await _userManager.SetEmailAsync(user, model.Email);
            if (!setEmailResult.Succeeded)
            {
                foreach (var error in setEmailResult.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                return View(model);
            }

            var setUserNameResult = await _userManager.SetUserNameAsync(user, model.Email);
            if (!setUserNameResult.Succeeded)
            {
                foreach (var error in setUserNameResult.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                return View(model);
            }
        }

        user.FullName = model.FullName;
        user.PhoneNumber = model.PhoneNumber;
        user.Address = model.Address;

        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            foreach (var error in updateResult.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return View(model);
        }

        if (!string.IsNullOrWhiteSpace(model.NewPassword))
        {
            var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
            var resetResult = await _userManager.ResetPasswordAsync(user, resetToken, model.NewPassword);
            if (!resetResult.Succeeded)
            {
                foreach (var error in resetResult.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                return View(model);
            }
        }

        var currentRoles = await _userManager.GetRolesAsync(user);
        var targetRole = string.IsNullOrWhiteSpace(model.Role) ? "User" : model.Role;
        if (!currentRoles.Contains(targetRole))
        {
            var removeResult = await _userManager.RemoveFromRolesAsync(user, currentRoles);
            if (!removeResult.Succeeded)
            {
                foreach (var error in removeResult.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                return View(model);
            }

            var addResult = await _userManager.AddToRoleAsync(user, targetRole);
            if (!addResult.Succeeded)
            {
                foreach (var error in addResult.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                return View(model);
            }
        }

        return RedirectToAction("Users");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteUser(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return NotFound();
        }

        var user = await _userManager.FindByIdAsync(id);
        if (user == null)
        {
            return NotFound();
        }

        await _userManager.DeleteAsync(user);
        return RedirectToAction("Users");
    }

    public IActionResult CreateUser()
    {
        var model = new CreateUserViewModel
        {
            Role = "User"
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateUser(CreateUserViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = new ApplicationUser
        {
            UserName = model.Email,
            Email = model.Email,
            FullName = model.FullName,
            PhoneNumber = model.PhoneNumber,
            Address = model.Address,
            CreatedAt = DateTime.UtcNow
        };

        var createResult = await _userManager.CreateAsync(user, model.Password);
        if (!createResult.Succeeded)
        {
            foreach (var error in createResult.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return View(model);
        }

        var role = string.IsNullOrWhiteSpace(model.Role) ? "User" : model.Role;
        var roleResult = await _userManager.AddToRoleAsync(user, role);
        if (!roleResult.Succeeded)
        {
            foreach (var error in roleResult.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return View(model);
        }

        return RedirectToAction("Users");
    }

}
