using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tourbooking.Chat;
using Tourbooking.Models;
using Tourbooking.ViewModels;
namespace Tourbooking.Controllers;
[Authorize(Roles = "Admin")]
public class AdminController(IChatStore store) : Controller
{
    private CancellationToken Ct => HttpContext.RequestAborted;
    public async Task<IActionResult> Contacts(string? status = null, int? id = null)
    {
        var contacts = await store.ListAsync(null, true, string.IsNullOrWhiteSpace(status) ? null : status, Ct);
        var model = new AdminContactsViewModel
        {
            SelectedStatus = status, Contacts = contacts.Select(c => new AdminContactRow
            {
                ContactInquiryId = c.ContactInquiryId, FullName = c.FullName, Email = c.Email,
                PhoneNumber = c.PhoneNumber, Subject = c.Subject, Message = c.Message, Status = c.Status,
                AdminReply = c.AdminReply, CreatedAt = c.CreatedAt, RepliedAt = c.RepliedAt
            }).ToList()
        };
        var selectedId = id ?? contacts.FirstOrDefault()?.ContactInquiryId;
        if (selectedId.HasValue)
        {
            var ticket = await store.GetAsync(selectedId.Value, Ct);
            if (ticket == null) return NotFound();
            model.Conversation = new ContactDetailsViewModel
            {
                Ticket = ticket, Replies = await store.RepliesAsync(ticket.ContactInquiryId, 0, Ct),
                ReplyForm = new ReplyCreateViewModel { ContactInquiryId = ticket.ContactInquiryId }
            };
        }
        return View(model);
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReplyContact(int contactInquiryId, string adminReply)
    {
        if (string.IsNullOrWhiteSpace(adminReply) || adminReply.Trim().Length > 2000)
        {
            TempData["ContactReplyMessage"] = "Vui lòng nhập phản hồi từ 1 đến 2000 ký tự.";
            return RedirectToAction(nameof(Contacts));
        }
        var ticket = await store.GetAsync(contactInquiryId, Ct);
        if (ticket == null) return NotFound();
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        var reply = new ContactInquiryReply
        { ContactInquiryId = contactInquiryId, Message = adminReply.Trim(), UserId = userId, IsFromAdmin = true };
        ticket.AdminReply = reply.Message; ticket.RepliedAt = reply.CreatedAt; ticket.RepliedByAdminId = userId;
        ticket.Status = "Replied"; ticket.UpdatedAt = DateTime.UtcNow;
        await store.ReplyAsync(ticket, reply, Ct);
        return RedirectToAction("ContactDetails", "Home", new { id = contactInquiryId });
    }
}
