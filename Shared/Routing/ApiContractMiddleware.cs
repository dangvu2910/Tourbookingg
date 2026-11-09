using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Tourbooking.Distributed;

// Versioned API responses follow the new contract; legacy MVC/JSON clients keep their wire format.
public sealed class ApiContractMiddleware(RequestDelegate next, ILogger<ApiContractMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers["X-Correlation-ID"].ToString();
        var correlation = Guid.TryParse(incoming, out var id) ? id.ToString("N") :
            Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        context.Items["CorrelationId"] = correlation;
        Activity.Current?.SetTag("correlation.id", correlation);
        context.Response.Headers["X-Correlation-ID"] = correlation;
        if (!context.Request.Path.StartsWithSegments("/api/v1")) { await next(context); return; }
        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            try { await next(context); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "API failed trace={CorrelationId}", correlation);
                context.Response.StatusCode = 500;
                buffer.SetLength(0);
            }
            if (context.Response.StatusCode == 204) return;
            buffer.Position = 0;
            JsonNode? data = null;
            if (buffer.Length > 0 && context.Response.ContentType?.Contains("json") == true)
            {
                try { data = SnakeCase(await JsonNode.ParseAsync(buffer, cancellationToken: context.RequestAborted)); }
                catch (JsonException ex)
                {
                    logger.LogError(ex, "API returned invalid JSON trace={CorrelationId}", correlation);
                    context.Response.StatusCode = 500;
                }
            }
            var success = context.Response.StatusCode is >= 200 and < 300;
            var envelope = new JsonObject
            {
                ["success"] = success, ["data"] = success ? data : null,
                ["error"] = success ? null : new JsonObject
                {
                    ["code"] = Code(context.Response.StatusCode),
                    ["message"] = context.Response.StatusCode >= 500 ? "Dịch vụ tạm thời không xử lý được yêu cầu." : "Yêu cầu không hợp lệ hoặc không được phép.",
                    ["details"] = context.Response.StatusCode < 500 ? data : null
                },
                ["meta"] = new JsonObject
                {
                    ["correlation_id"] = correlation, ["timestamp"] = DateTime.UtcNow.ToString("O")
                }
            };
            if (context.Items.TryGetValue("Pagination", out var pagination))
                envelope["meta"]!["pagination"] = JsonSerializer.SerializeToNode(pagination);
            context.Response.Body = original;
            context.Response.ContentLength = null;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(envelope.ToJsonString(), context.RequestAborted);
        }
        finally { context.Response.Body = original; }
    }
    private static string Code(int status) => status switch
    {
        400 => "INVALID_REQUEST", 401 => "UNAUTHENTICATED", 403 => "PERMISSION_DENIED",
        404 => "RESOURCE_NOT_FOUND", 409 => "CONCURRENT_CONFLICT", 422 => "VALIDATION_FAILED",
        405 => "METHOD_NOT_ALLOWED", 413 => "PAYLOAD_TOO_LARGE", 415 => "UNSUPPORTED_MEDIA_TYPE", 429 => "RATE_LIMITED",
        503 => "DEPENDENCY_UNAVAILABLE", 504 => "DEPENDENCY_TIMEOUT", _ => "INTERNAL_ERROR"
    };
    private static JsonNode? SnakeCase(JsonNode? value) => value switch
    {
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(
            JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Key), SnakeCase(p.Value)))),
        JsonArray array => new JsonArray(array.Select(SnakeCase).ToArray()),
        _ => value?.DeepClone()
    };
}
