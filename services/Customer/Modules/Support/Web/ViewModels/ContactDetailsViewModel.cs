using Tourbooking.Models;

namespace Tourbooking.ViewModels;

public class ContactDetailsViewModel
{
    public ContactInquiry Ticket { get; set; } = new();
    public List<ContactInquiryReply> Replies { get; set; } = new();
    public ReplyCreateViewModel ReplyForm { get; set; } = new();
}
