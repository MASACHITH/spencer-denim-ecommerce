using Microsoft.AspNetCore.Antiforgery;

namespace SpencerDenim.Api.Http;

/// <summary>
/// Doc 03 §3a: every mutating admin endpoint validates the antiforgery token by hand (there is no global
/// <c>UseAntiforgery()</c> middleware in <c>Program.cs</c> — cookie auth alone is not CSRF-safe for a browser
/// client). Originally private to <c>AdminAuthEndpoints</c> (logout, change-password); pulled out here so
/// every later admin endpoint group — starting with Users &amp; Roles management, W1-08 — shares the exact
/// same check instead of re-implementing it and risking a group that forgets it.
/// </summary>
internal static class AntiforgeryValidation
{
    public static async Task<bool> IsRequestValidAsync(HttpContext httpContext, IAntiforgery antiforgery)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(antiforgery);

        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}
