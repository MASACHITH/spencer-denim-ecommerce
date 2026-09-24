using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using SpencerDenim.Api.Endpoints.Content;

namespace SpencerDenim.Api.Http;

/// <summary>
/// Rate limiting for the one public endpoint that writes: the newsletter signup (HOME-10).
/// <para>
/// A form that inserts a row on an anonymous POST is a form someone will point a script at, and
/// <c>marketing.NewsletterSubscribers</c> has a real cost — every address in it is a person the
/// client will eventually email. Doc 08 §3.3 already rate-limits the other anonymous write in the
/// system (the guest bank-slip upload, five per hour per IP); this is the same idea at the same
/// layer.
/// </para>
/// <para>
/// The partition is the socket peer, deliberately: <c>X-Forwarded-For</c> processing is off
/// (W1-01 round 3), so a caller cannot spoof its way into someone else's bucket. Behind Caddy every
/// request shares the proxy's address, which W0-15 has to answer when the real topology exists —
/// the note is on that task.
/// </para>
/// <para>
/// The ASP.NET Core rate limiter is part of the framework; nothing new is taken on for it.
/// </para>
/// </summary>
internal static class ContentRateLimiting
{
    /// <summary>Signups one address may make in a window. A person signs up once; a script does not.</summary>
    private const int PermitLimit = 10;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    public static IServiceCollection AddContentRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Tell the caller when to come back rather than leaving them to guess.
            options.OnRejected = (context, cancellationToken) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value)
                    ? value
                    : Window;

                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);

                return ValueTask.CompletedTask;
            };

            options.AddPolicy(
                ContentEndpoints.NewsletterRateLimitPolicy,
                httpContext => RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = PermitLimit,
                        Window = Window,
                        QueueLimit = 0,
                    }));
        });

        return services;
    }
}
