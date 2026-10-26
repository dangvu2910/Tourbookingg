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

    public async Task<IActionResult> Contacts(string? status = null, int? id = null)
    {
        var query = _context.ContactInquiries
            .AsNoTracking()
            .OrderByDescending(c => c.UpdatedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(c => c.Status == status);
        }

        var model = new AdminContactsViewModel
        {
            SelectedStatus = status,
            Contacts = await query
                .Select(c => new AdminContactRow
                {
                    ContactInquiryId = c.ContactInquiryId,
                    FullName = c.FullName,
                    Email = c.Email,
                    PhoneNumber = c.PhoneNumber,
                    Subject = c.Subject,
                    Message = c.Message,
                    Status = c.Status,
                    AdminReply = c.AdminReply,
                    CreatedAt = c.CreatedAt,
                    RepliedAt = c.RepliedAt
                })
                .ToListAsync()
        };

        var selectedId = id ?? model.Contacts.FirstOrDefault()?.ContactInquiryId;
        if (selectedId.HasValue)
        {
            var ticket = await _context.ContactInquiries.AsNoTracking()
                .FirstOrDefaultAsync(c => c.ContactInquiryId == selectedId.Value);
            if (ticket == null) return NotFound();
            model.Conversation = new ContactDetailsViewModel
            {
                Ticket = ticket,
                Replies = await _context.ContactInquiryReplies.AsNoTracking()
                    .Where(r => r.ContactInquiryId == ticket.ContactInquiryId)
                    .OrderBy(r => r.ContactInquiryReplyId).ToListAsync(),
                ReplyForm = new ReplyCreateViewModel { ContactInquiryId = ticket.ContactInquiryId }
            };
        }
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReplyContact(int contactInquiryId, string adminReply)
    {
        if (string.IsNullOrWhiteSpace(adminReply) || adminReply.Trim().Length > 2000)
        {
            TempData["ContactReplyMessage"] = "Vui lòng nhập nội dung phản hồi.";
            return RedirectToAction(nameof(Contacts));
        }

        var contact = await _context.ContactInquiries.FirstOrDefaultAsync(c => c.ContactInquiryId == contactInquiryId);
        if (contact == null)
        {
            TempData["ContactReplyMessage"] = "Không tìm thấy yêu cầu liên hệ.";
            return RedirectToAction(nameof(Contacts));
        }

        var adminUser = await _userManager.GetUserAsync(User);
        contact.AdminReply = adminReply.Trim();
        contact.RepliedAt = DateTime.UtcNow;
        contact.RepliedByAdminId = adminUser?.Id;
        contact.Status = "Replied";
        contact.UpdatedAt = DateTime.UtcNow;

        var reply = new ContactInquiryReply
        {
            ContactInquiryId = contact.ContactInquiryId,
            Message = adminReply.Trim(),
            UserId = adminUser?.Id,
            IsFromAdmin = true,
        };

        _context.Add(reply);
        await _context.SaveChangesAsync();

        TempData["ContactReplyMessage"] = "Đã gửi phản hồi cho người dùng.";
        return RedirectToAction("ContactDetails", "Home", new { id = contactInquiryId });
    }

}
