using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using Tourbooking.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Tourbooking.ViewModels;

namespace Tourbooking.Controllers;

public class HomeController : Controller
{

        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IConfiguration _configuration;

        public HomeController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IConfiguration configuration)
        {
            _context = context;
            _userManager = userManager;
            _configuration = configuration;
        }

        private async Task<ContactPageViewModel> BuildContactPageViewModelAsync(ContactViewModel form)
        {
            var user = await _userManager.GetUserAsync(User);
            var lookupEmail = !string.IsNullOrWhiteSpace(form.Email)
                ? form.Email.Trim()
                : user?.Email;

            var ticketsQuery = _context.ContactInquiries.AsNoTracking().AsQueryable();
            var ownerId = user?.Id;
            ticketsQuery = ticketsQuery.Where(c => ownerId != null && c.UserId == ownerId);
            if (!string.IsNullOrWhiteSpace(lookupEmail))
            {
                ticketsQuery = ticketsQuery.Where(c => c.Email == lookupEmail);
            }

            return new ContactPageViewModel
            {
                LookupEmail = lookupEmail,
                Form = form,
                Tickets = await ticketsQuery
                    .OrderByDescending(c => c.CreatedAt)
                    .Select(c => new ContactInquiryRow
                    {
                        ContactInquiryId = c.ContactInquiryId,
                        FullName = c.FullName,
                        Email = c.Email,
                        Subject = c.Subject,
                        Message = c.Message,
                        Status = c.Status,
                        AdminReply = c.AdminReply,
                        CreatedAt = c.CreatedAt,
                        RepliedAt = c.RepliedAt
                    })
                    .ToListAsync()
            };
        }

        [Authorize]
        [HttpGet]
        public async Task<IActionResult> Contact()
        {
            if (User.IsInRole("Admin")) return Redirect("/Admin/Contacts");
            var user = await _userManager.GetUserAsync(User);
            var lookupEmail = !string.IsNullOrWhiteSpace(user?.Email) ? user!.Email : null;

            var ticketsQuery = _context.ContactInquiries.AsNoTracking().AsQueryable();
            var ownerId = user?.Id;
            ticketsQuery = ticketsQuery.Where(c => ownerId != null && c.UserId == ownerId);
            if (!string.IsNullOrWhiteSpace(lookupEmail))
            {
                ticketsQuery = ticketsQuery.Where(c => c.Email == lookupEmail);
            }

            var model = new ContactPageViewModel
            {
                LookupEmail = lookupEmail,
                Form = new ContactViewModel
                {
                    FullName = user?.FullName ?? string.Empty,
                    Email = lookupEmail ?? string.Empty
                },
                Tickets = await ticketsQuery
                    .OrderByDescending(c => c.CreatedAt)
                    .Select(c => new ContactInquiryRow
                    {
                        ContactInquiryId = c.ContactInquiryId,
                        FullName = c.FullName,
                        Email = c.Email,
                        Subject = c.Subject,
                        Message = c.Message,
                        Status = c.Status,
                        AdminReply = c.AdminReply,
                        CreatedAt = c.CreatedAt,
                        RepliedAt = c.RepliedAt
                    })
                    .ToListAsync()
            };

            return View(model);
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Contact(ContactViewModel model)
        {
            if (!ModelState.IsValid)
            {
                var invalidPageModel = await BuildContactPageViewModelAsync(model);
                return View(invalidPageModel);
            }

            var user = await _userManager.GetUserAsync(User);
            var ticket = new ContactInquiry
            {
                FullName = model.FullName.Trim(),
                Email = model.Email.Trim(),
                PhoneNumber = string.IsNullOrWhiteSpace(model.PhoneNumber) ? null : model.PhoneNumber.Trim(),
                Subject = model.Subject.Trim(),
                Message = model.Message.Trim(),
                UserId = user?.Id,
                Status = "Pending"
            };

            _context.ContactInquiries.Add(ticket);
            await _context.SaveChangesAsync();

            TempData["ContactMessage"] = "Yêu cầu của bạn đã được gửi. Khi admin trả lời, trạng thái sẽ hiển thị ngay tại đây.";
            return RedirectToAction("ContactDetails", new { id = ticket.ContactInquiryId });
        }

        [Authorize]
        [HttpGet]
        public async Task<IActionResult> ContactDetails(int id)
        {
            var ticket = await _context.ContactInquiries
                .AsNoTracking()
                .Include(c => c.User)
                .FirstOrDefaultAsync(c => c.ContactInquiryId == id);

            if (ticket == null)
            {
                return NotFound();
            }

            if (!User.IsInRole("Admin") && ticket.UserId != _userManager.GetUserId(User)) return Forbid();

            var replies = await _context.ContactInquiryReplies
                .AsNoTracking()
                .Where(r => r.ContactInquiryId == id)
                .OrderBy(r => r.CreatedAt).ThenBy(r => r.ContactInquiryReplyId)
                .ToListAsync();

            var model = new ContactDetailsViewModel
            {
                Ticket = ticket,
                Replies = replies,
                ReplyForm = new ReplyCreateViewModel { ContactInquiryId = id }
            };

            return View(model);
        }

        [Authorize]
        [HttpGet]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> ContactMessages(int id, int after = 0)
        {
            var ticket = await _context.ContactInquiries.AsNoTracking()
                .FirstOrDefaultAsync(c => c.ContactInquiryId == id);
            if (ticket == null) return NotFound();
            if (!User.IsInRole("Admin") && ticket.UserId != _userManager.GetUserId(User)) return Forbid();
            var messages = await _context.ContactInquiryReplies.AsNoTracking()
                .Where(r => r.ContactInquiryId == id && r.ContactInquiryReplyId > after)
                .OrderBy(r => r.ContactInquiryReplyId)
                .Select(r => new { id = r.ContactInquiryReplyId, message = r.Message,
                    isFromAdmin = r.IsFromAdmin, createdAt = r.CreatedAt }).ToListAsync();
            return Json(new { status = ticket.Status, messages });
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PostReply(ReplyCreateViewModel model)
        {
            if (!ModelState.IsValid)
            {
                TempData["ChatError"] = "Tin nhắn phải có từ 1 đến 2000 ký tự.";
                return RedirectToAction("ContactDetails", new { id = model.ContactInquiryId });
            }

            var ticket = await _context.ContactInquiries.FirstOrDefaultAsync(c => c.ContactInquiryId == model.ContactInquiryId);
            if (ticket == null)
            {
                return NotFound();
            }

            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return Challenge();
            }
            if (!User.IsInRole("Admin") && ticket.UserId != user.Id) return Forbid();

            var reply = new ContactInquiryReply
            {
                ContactInquiryId = model.ContactInquiryId,
                Message = model.Message.Trim(),
                UserId = user.Id,
                IsFromAdmin = User.IsInRole("Admin")
            };

            ticket.UpdatedAt = DateTime.UtcNow;
            ticket.Status = reply.IsFromAdmin ? "Replied" : "Pending";
            if (reply.IsFromAdmin)
            {
                ticket.AdminReply = reply.Message;
                ticket.RepliedAt = reply.CreatedAt;
                ticket.RepliedByAdminId = user.Id;
            }
            _context.Add(reply);
            await _context.SaveChangesAsync();

            if (Request.Headers.Accept.Any(v => v != null && v.Contains("application/json")))
                return Json(new { saved = true });
            return RedirectToAction("ContactDetails", new { id = model.ContactInquiryId });
        }


    
}
