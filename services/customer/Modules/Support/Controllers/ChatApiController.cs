using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tourbooking.Chat;

namespace Tourbooking.Controllers;

[ApiController, Authorize, Route("api/v1/chat")]
public sealed class ChatApiController(IChatStore store) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var items = await store.ListAsync(User.FindFirstValue(ClaimTypes.NameIdentifier), false, null, ct);
        HttpContext.Items["Pagination"] = new { page = 1, page_size = 200 };
        return Ok(items.Select(c => new { id = c.ContactInquiryId, c.Subject, c.Status, c.CreatedAt, c.UpdatedAt }));
    }
    [HttpGet("{id:int}/messages")]
    public async Task<IActionResult> Messages(int id, int after = 0, CancellationToken ct = default)
    {
        var conversation = await store.GetAsync(id, ct);
        if (conversation == null || conversation.UserId != User.FindFirstValue(ClaimTypes.NameIdentifier)) return NotFound();
        return Ok((await store.RepliesAsync(id, Math.Max(0, after), ct))
            .Select(r => new { id = r.ContactInquiryReplyId, r.Message, r.IsFromAdmin, r.CreatedAt }));
    }
}
