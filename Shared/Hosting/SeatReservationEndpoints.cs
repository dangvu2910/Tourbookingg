using Tourbooking.Catalog.Reservations;
using Tourbooking.Contracts;

namespace Tourbooking.Distributed;

public static class SeatReservationEndpoints
{
    public static void MapSeatReservations(this WebApplication app)
    {
        var group = app.MapGroup("/internal/v1/reservations");
        group.AddEndpointFilter(async (context, next) =>
        {
            var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            if (string.IsNullOrEmpty(config["Services:InternalKey"]) || context.HttpContext.Request.Headers["X-Service-Key"] != config["Services:InternalKey"])
                return Results.Unauthorized();
            try { return await next(context); }
            catch (ReservationException ex)
            {
                var status = ex.Code == "RESOURCE_NOT_FOUND" ? 404 : ex.Code == "INVALID_REQUEST" ? 400 : 409;
                return Results.Json(new { code = ex.Code, message = ex.Message }, statusCode: status);
            }
        });
        group.MapPost("", (ReserveSeats request, SeatReservationService service, CancellationToken ct) => service.ReserveAsync(request, ct));
        group.MapPost("/{id:guid}/confirm", (Guid id, SeatReservationService service, CancellationToken ct) => service.ConfirmAsync(id, ct));
        group.MapPost("/{id:guid}/release", (Guid id, SeatReservationService service, CancellationToken ct) => service.ReleaseAsync(id, ct));
    }
}
