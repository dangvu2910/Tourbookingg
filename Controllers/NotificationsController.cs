using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Tourbooking.Controllers;

[Authorize]
public sealed class NotificationsController(IHttpClientFactory clients, IConfiguration configuration) : Controller
{
    public async Task<IActionResult> Index()
    {
        try
        {
            using var client = clients.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                (configuration["Services:notification"] ?? "http://localhost:5104") + "/api/notifications");
            request.Headers.TryAddWithoutValidation("Cookie", Request.Headers.Cookie.ToString());
            using var response = await client.SendAsync(request, HttpContext.RequestAborted);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return Challenge();
            response.EnsureSuccessStatusCode();
            return View(await response.Content.ReadFromJsonAsync<List<NotificationItem>>() ?? []);
        }
        catch (HttpRequestException) { ViewBag.Unavailable = true; }
        catch (TaskCanceledException) { ViewBag.Unavailable = true; }
        return View(new List<NotificationItem>());
    }
}

public sealed record NotificationItem(Guid EventId, string Type, string ResourceId, string Message, DateTime CreatedAt);
