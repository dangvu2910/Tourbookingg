using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Tourbooking.Distributed;

// Small-demo read models are refreshed over private HTTP; each service opens only its own DB.
public sealed class ProjectionRefreshFilter(ApplicationDbContext db, IHttpClientFactory clients,
    IConfiguration config, ProjectionRefreshCoordinator coordinator, ILogger<ProjectionRefreshFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var ownedServices = ServiceData.OwnedServices(db.EventSource);
        foreach (var owner in ServiceData.Services.Where(owner =>
            !ownedServices.Contains(owner) && ownedServices.Any(service => ServiceData.Needs(service, owner))))
        {
            try
            {
                using var client = clients.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(4);
                client.DefaultRequestHeaders.Add("X-Service-Key", config["Services:InternalKey"]);
                client.DefaultRequestHeaders.Add("X-Correlation-ID", context.HttpContext.Items["CorrelationId"]?.ToString());
                var url = config[$"Services:{owner}"] ?? $"http://localhost:{5101 + Array.IndexOf(ServiceBoundary.Services, owner)}";
                await coordinator.RefreshAsync(owner,
                    async ct => await client.GetFromJsonAsync<List<TableSnapshot>>(url + "/internal/data/" + owner, ct)
                        ?? throw new HttpRequestException("Owner returned an empty projection response."),
                    (tables, ct) => ProjectionStore.ReplaceAsync(db, tables, ct), context.HttpContext.RequestAborted);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning("Using cached {Owner} projections: {Message}", owner, ex.Message);
            }
        }
        await next();
    }
}
