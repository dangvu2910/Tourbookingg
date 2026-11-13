using Tourbooking.Models;

namespace Tourbooking.Chat;

public interface IChatStore
{
    Task<List<ContactInquiry>> ListAsync(string? userId, bool admin, string? status, CancellationToken ct);
    Task<ContactInquiry?> GetAsync(int id, CancellationToken ct);
    Task<List<ContactInquiryReply>> RepliesAsync(int id, int after, CancellationToken ct);
    Task CreateAsync(ContactInquiry inquiry, CancellationToken ct);
    Task ReplyAsync(ContactInquiry inquiry, ContactInquiryReply reply, CancellationToken ct);
}
