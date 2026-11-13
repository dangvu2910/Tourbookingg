using Microsoft.EntityFrameworkCore;
using Tourbooking.Models;

namespace Tourbooking.Chat;

public sealed class SqliteChatStore(ApplicationDbContext db) : IChatStore
{
    public Task<List<ContactInquiry>> ListAsync(string? userId, bool admin, string? status, CancellationToken ct) =>
        db.ContactInquiries.AsNoTracking().Where(c => (admin || c.UserId == userId) && (status == null || c.Status == status))
            .OrderByDescending(c => c.UpdatedAt).ThenByDescending(c => c.ContactInquiryId).Take(200).ToListAsync(ct);
    public Task<ContactInquiry?> GetAsync(int id, CancellationToken ct) => db.ContactInquiries.FirstOrDefaultAsync(c => c.ContactInquiryId == id, ct);
    public Task<List<ContactInquiryReply>> RepliesAsync(int id, int after, CancellationToken ct) =>
        db.ContactInquiryReplies.AsNoTracking().Where(r => r.ContactInquiryId == id && r.ContactInquiryReplyId > after)
            .OrderBy(r => r.ContactInquiryReplyId).ToListAsync(ct);
    public async Task CreateAsync(ContactInquiry inquiry, CancellationToken ct)
    {
        db.ContactInquiries.Add(inquiry);
        await db.SaveChangesAsync(ct);
    }
    public async Task ReplyAsync(ContactInquiry inquiry, ContactInquiryReply reply, CancellationToken ct)
    {
        db.ContactInquiryReplies.Add(reply);
        await db.SaveChangesAsync(ct);
    }
}
