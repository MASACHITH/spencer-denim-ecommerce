using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SpencerDenim.Infrastructure.Health;

namespace SpencerDenim.Api.Health;

internal static class HealthEndpoints
{
    public const string LivenessPath = "/health";
    public const string ReadinessPath = "/health/ready";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IServiceCollection AddApiHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy("API process is running."), tags: [HealthCheckTags.Live]);

        return services;
    }

    public static IEndpointRouteBuilder MapApiHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        // Liveness: the process answers. It must not depend on SQL Server, or a DB outage would restart the API.
        endpoints.MapHealthChecks(LivenessPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(HealthCheckTags.Live),
            ResponseWriter = WriteResponseAsync,
        });

        // Readiness: dependencies (SQL Server) are reachable.
        endpoints.MapHealthChecks(ReadinessPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(HealthCheckTags.Ready),
            ResponseWriter = WriteResponseAsync,
        });

        return endpoints;
    }

    private static Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
            }),
        };

        return JsonSerializer.SerializeAsync(context.Response.Body, payload, JsonOptions, context.RequestAborted);
    }
}
