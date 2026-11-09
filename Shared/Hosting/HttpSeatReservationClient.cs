using System.Net.Http.Json;
using Tourbooking.Contracts;

namespace Tourbooking.Distributed;

public sealed class HttpSeatReservationClient(HttpClient http, IConfiguration configuration) : ISeatReservationClient
{
    public Task<ReservedSeats> ReserveAsync(ReserveSeats request, CancellationToken ct) => SendAsync("", request, ct);
    public Task<ReservedSeats> ConfirmAsync(Guid id, CancellationToken ct) => SendAsync($"/{id}/confirm", null, ct);
    public Task<ReservedSeats> ReleaseAsync(Guid id, CancellationToken ct) => SendAsync($"/{id}/release", null, ct);
    private async Task<ReservedSeats> SendAsync(string suffix, ReserveSeats? request, CancellationToken ct)
    {
        var address = (configuration["Services:catalog"] ?? "http://localhost:5102").TrimEnd('/');
        using var message = new HttpRequestMessage(HttpMethod.Post, address + "/internal/v1/reservations" + suffix);
        message.Headers.Add("X-Service-Key", configuration["Services:InternalKey"]);
        message.Headers.Add("X-Correlation-ID", System.Diagnostics.Activity.Current?.GetTagItem("correlation.id")?.ToString());
        if (request != null) message.Content = JsonContent.Create(request);
        using var response = await http.SendAsync(message, ct);
        if ((int)response.StatusCode is 400 or 404 or 409)
        {
            var error = await response.Content.ReadFromJsonAsync<ReservationError>(ct);
            throw new ReservationException(error?.Code ?? "INVALID_STATE_TRANSITION", error?.Message ?? "Giữ chỗ không hợp lệ.");
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ReservedSeats>(ct) ?? throw new HttpRequestException("Catalog returned an empty reservation response.");
    }
    private sealed record ReservationError(string Code, string Message);
}
