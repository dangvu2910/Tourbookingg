using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Prometheus;

namespace Tourbooking.Distributed;

// Resilience for service-to-service HTTP (chapter 8: circuit breaking).
// Applied to every IHttpClientFactory client, with one pipeline per target authority (identity:8080, catalog:8080...),
// so a failing service opens only its own circuit. YARP (web-router) and Kong do not use these clients.
//
// Pipeline, outermost first:
//   retry            - at most 2 retries with jittered backoff, ONLY for safe methods (GET/HEAD).
//                      Reservation/payment commands are POSTs and are never re-sent here; they rely on RequestId.
//   circuit breaker  - opens when >=50% of at least 4 calls in 30 s fail (5xx, 408, 429, network error, timeout);
//                      while open, calls fail immediately for 15 s instead of waiting; then one trial call (half-open).
//   attempt timeout  - 3 s per attempt. Callers still set HttpClient.Timeout as the overall budget.
public static class ServiceResilience
{
    private static readonly Gauge CircuitState = Metrics.CreateGauge(
        "tourbooking_http_circuit_state",
        "Circuit breaker state for outgoing service calls: 0 = closed, 0.5 = half-open, 1 = open.",
        new GaugeConfiguration { LabelNames = ["target"] });
    private static readonly Counter CircuitOpened = Metrics.CreateCounter(
        "tourbooking_http_circuit_opened_total",
        "Number of times the circuit to a target service opened.",
        new CounterConfiguration { LabelNames = ["target"] });

    public static IServiceCollection AddServiceResilience(this IServiceCollection services)
    {
        services.AddTransient<ResilienceExceptionTranslator>();
        services.ConfigureHttpClientDefaults(http =>
        {
            // Added first, so it wraps the resilience handler and sees its exceptions.
            http.AddHttpMessageHandler<ResilienceExceptionTranslator>();
            http.AddResilienceHandler("service-calls", (pipeline, context) =>
            {
                var logger = context.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Tourbooking.CircuitBreaker");

                var retry = new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 2,
                    Delay = TimeSpan.FromMilliseconds(200),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                };
                retry.DisableForUnsafeHttpMethods();
                pipeline.AddRetry(retry);

                pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    MinimumThroughput = 4,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    BreakDuration = TimeSpan.FromSeconds(15),
                    OnOpened = args =>
                    {
                        var target = Target(args.Context);
                        CircuitState.WithLabels(target).Set(1);
                        CircuitOpened.WithLabels(target).Inc();
                        logger.LogWarning("Circuit OPEN for {Target} for {BreakSeconds}s after {Failure}; calls now fail fast.",
                            target, args.BreakDuration.TotalSeconds,
                            args.Outcome.Exception?.GetType().Name ?? ((int?)args.Outcome.Result?.StatusCode)?.ToString());
                        return default;
                    },
                    OnHalfOpened = args =>
                    {
                        var target = Target(args.Context);
                        CircuitState.WithLabels(target).Set(0.5);
                        logger.LogInformation("Circuit HALF-OPEN for {Target}; sending a trial call.", target);
                        return default;
                    },
                    OnClosed = args =>
                    {
                        var target = Target(args.Context);
                        CircuitState.WithLabels(target).Set(0);
                        logger.LogInformation("Circuit CLOSED for {Target}; calls flow normally again.", target);
                        return default;
                    },
                });

                pipeline.AddTimeout(TimeSpan.FromSeconds(3));
            }).SelectPipelineByAuthority();
        });
        return services;
    }

    private static string Target(ResilienceContext context) =>
        context.GetRequestMessage()?.RequestUri?.Authority ?? "unknown";

    // Existing callers treat HttpRequestException/TaskCanceledException as "dependency unavailable" and fall back
    // (cached projections, 503 to the user...). Translate Polly's rejections so every caller keeps working unchanged.
    private sealed class ResilienceExceptionTranslator : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try { return await base.SendAsync(request, cancellationToken); }
            catch (BrokenCircuitException ex)
            {
                throw new HttpRequestException($"Circuit open for {request.RequestUri?.Authority}; failing fast.", ex,
                    System.Net.HttpStatusCode.ServiceUnavailable);
            }
            catch (TimeoutRejectedException ex)
            {
                throw new HttpRequestException($"Call to {request.RequestUri?.Authority} timed out.", ex,
                    System.Net.HttpStatusCode.GatewayTimeout);
            }
        }
    }
}
