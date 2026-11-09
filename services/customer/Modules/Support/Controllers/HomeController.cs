using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tourbooking.Chat;
using Tourbooking.Models;
using Tourbooking.ViewModels;
namespace Tourbooking.Controllers;
[Authorize]
public class HomeController(IChatStore store) : Controller
{
    private string? UserId => User.FindFirstValue(ClaimTypes.NameIdentifier);
    private CancellationToken Ct => HttpContext.RequestAborted;
    private async Task<ContactPageViewModel> PageAsync(ContactViewModel form) => new()
    {
        LookupEmail = form.Email, Form = form,
        Tickets = (await store.ListAsync(UserId, false, null, Ct)).Select(c => new ContactInquiryRow
        {
            ContactInquiryId = c.ContactInquiryId, FullName = c.FullName, Email = c.Email,
            Subject = c.Subject, Message = c.Message, Status = c.Status, AdminReply = c.AdminReply,
            CreatedAt = c.CreatedAt, RepliedAt = c.RepliedAt
        }).ToList()
    };
    [HttpGet]
    public async Task<IActionResult> Contact()
    {
        if (User.IsInRole("Admin")) return Redirect("/Admin/Contacts");
        return View(await PageAsync(new ContactViewModel { Email = User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name ?? "" }));
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Contact(ContactViewModel model)
    {
        if (!ModelState.IsValid) return View(await PageAsync(model));
        if (UserId == null) return Challenge();
        var ticket = new ContactInquiry
        {
            FullName = model.FullName.Trim(), Email = model.Email.Trim(), PhoneNumber = model.PhoneNumber?.Trim(),
            Subject = model.Subject.Trim(), Message = model.Message.Trim(), UserId = UserId, Status = "Pending"
        };
        await store.CreateAsync(ticket, Ct);
        return RedirectToAction(nameof(ContactDetails), new { id = ticket.ContactInquiryId });
    }
    [HttpGet]
    public async Task<IActionResult> ContactDetails(int id)
    {
        var ticket = await store.GetAsync(id, Ct);
        if (ticket == null) return NotFound();
        if (!User.IsInRole("Admin") && ticket.UserId != UserId) return Forbid();
        return View(new ContactDetailsViewModel
        {
            Ticket = ticket, Replies = await store.RepliesAsync(id, 0, Ct),
            ReplyForm = new ReplyCreateViewModel { ContactInquiryId = id }
        });
    }
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ContactMessages(int id, int after = 0)
    {
        var ticket = await store.GetAsync(id, Ct);
        if (ticket == null) return NotFound();
        if (!User.IsInRole("Admin") && ticket.UserId != UserId) return Forbid();
        var messages = await store.RepliesAsync(id, Math.Max(0, after), Ct);
        return Json(new { status = ticket.Status, messages = messages.Select(r => new
        { id = r.ContactInquiryReplyId, message = r.Message, isFromAdmin = r.IsFromAdmin, createdAt = r.CreatedAt }) });
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PostReply(ReplyCreateViewModel model)
    {
        if (!ModelState.IsValid)
        {
            TempData["ChatError"] = "Tin nhắn phải có từ 1 đến 2000 ký tự.";
            return RedirectToAction(nameof(ContactDetails), new { id = model.ContactInquiryId });
        }
        var ticket = await store.GetAsync(model.ContactInquiryId, Ct);
        if (ticket == null) return NotFound();
        if (UserId == null) return Challenge();
        if (!User.IsInRole("Admin") && ticket.UserId != UserId) return Forbid();
        var reply = new ContactInquiryReply
        {
            ContactInquiryId = model.ContactInquiryId, Message = model.Message.Trim(),
            UserId = UserId, IsFromAdmin = User.IsInRole("Admin")
        };
        ticket.UpdatedAt = DateTime.UtcNow;
        ticket.Status = reply.IsFromAdmin ? "Replied" : "Pending";
        if (reply.IsFromAdmin)
        {
            ticket.AdminReply = reply.Message; ticket.RepliedAt = reply.CreatedAt; ticket.RepliedByAdminId = UserId;
        }
        await store.ReplyAsync(ticket, reply, Ct);
        if (Request.Headers.Accept.Any(v => v?.Contains("application/json") == true)) return Json(new { saved = true });
        return RedirectToAction(nameof(ContactDetails), new { id = model.ContactInquiryId });
    }
}
