using Microsoft.EntityFrameworkCore;
using Tourbooking.Models;

namespace Tourbooking.Distributed;

public static class BookingVersionGuard
{
    public static void RequireCurrent(ApplicationDbContext db, Booking booking, string? submittedVersion)
    {
        byte[] original;
        try { original = Convert.FromBase64String(submittedVersion ?? ""); }
        catch (FormatException) { throw new DbUpdateConcurrencyException("Invalid booking version; reload the form."); }

        if (original.Length == 0 || !original.AsSpan().SequenceEqual(booking.Version))
            throw new DbUpdateConcurrencyException("The booking has changed since this form was loaded.");

        // Also protect the interval between this check and SaveChangesAsync.
        db.Entry(booking).Property(b => b.Version).OriginalValue = original;
    }
}
