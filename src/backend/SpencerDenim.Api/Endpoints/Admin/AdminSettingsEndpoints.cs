using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Admin;
using SpencerDenim.Application.Settings.Contracts;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The settings screen (ADM-22) and the shop's branding (GLB-02).
/// <para>
/// <b>Permissions.</b> Every route is behind <c>Settings.Edit</c>, which is the permission doc 08 §3.8
/// names for the <c>Store</c>, <c>Contact</c> and <c>Social</c> groups. The read then filters again, per
/// row, against the caller's effective permissions — so when <c>Bank.*</c> and <c>Email.Smtp.*</c> arrive
/// with #53 and W3, a <c>Settings.Edit</c> holder reaching this route does not start seeing them. The API is
/// the security boundary (doc 03 §3a); the admin UI's hiding is convenience.
/// </para>
/// <para>
/// <b>The converse is a limitation #53 will meet, and it is written down rather than left to be found.</b>
/// The route guard is a single permission, so a caller holding <i>only</i> <c>Settings.Payments</c> — a
/// custom role somebody could build — could not reach this screen at all, not even to read the group that is
/// theirs. No such role exists today: docs/02's Store Manager holds <c>Settings.Edit</c> and not
/// <c>Settings.Payments</c>, and a Super Admin holds everything through GrantsAll. Inventing a "holds any
/// settings permission" policy for a caller who does not exist would be a security-shaped abstraction with
/// nothing able to test it, so this guard is what has to widen when the payments group lands — the per-row
/// filter in the read model already does the rest.
/// </para>
/// <para>
/// <b>Antiforgery</b> is validated by hand on every mutating route, as <c>AdminAuthEndpoints.Logout</c>,
/// <c>AdminMediaEndpoints</c> and <c>AdminCatalogEndpoints</c> do, so the refusal is a problem document a
/// client can read rather than an empty 400.
/// </para>
/// <para>
/// <b>Why the logo is its own route rather than a field on the settings form.</b> Same reason
/// <c>SetCategoryActive</c> is separate from the category form: changing the logo changes every page of the
/// shop instantly, and that should be a deliberate act with its own audit row — never a side effect of
/// saving a form whose hidden field was stale. It also keeps the upload flow honest: the screen posts the
/// file to the media library first and then points the setting at the result, so a failed upload cannot half
/// apply.
/// </para>
/// <para>
/// <b>Not here.</b> Everything doc 08 §3.8 lists that this build does not seed — <c>Bank.*</c>,
/// <c>Payments.PayPal.*</c>, <c>Email.Smtp.*</c>, <c>Security.*</c>, <c>Privacy.RetainInvoiceBillTo</c>, the
/// delivery methods, zone rates and couriers. Each needs a permission, a table or an alert that does not
/// exist yet; see <c>SettingKeys</c>. They arrive as rows and as routes beside these, not as a redesign.
/// </para>
/// </summary>
internal static class AdminSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAdminSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/settings").WithTags("Admin Settings");

        api.MapGet("/", GetAsync)
            .RequirePermission(Permissions.Settings.Edit)
            .WithName("AdminGetSettings")
            .WithSummary("The settings this user may read, group by group.")
            .WithDescription(
                "ADM-22. Groups the caller has no permission for are absent rather than empty, so the API "
                + "does not confirm that a group exists to somebody who may not read it. A secret's value is "
                + "never returned to anybody through this payload — only whether one is set (hasValue). Each "
                + "group carries a rowVersion covering all of its rows; send it straight back with a save.")
            .Produces<AdminSettingsDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        api.MapPut("/store-logo", SetStoreLogoAsync)
            .RequirePermission(Permissions.Settings.Edit)
            .WithName("AdminSetStoreLogo")
            .WithSummary("Set or remove the shop's logo (GLB-02).")
            .WithDescription(
                "Upload the image to POST /api/admin/media/images first, then send its publicId here. "
                + "Send null to remove the logo, and the header goes back to the text wordmark. Replacing a "
                + "logo releases the previous asset's MediaUsages row in the same transaction, so the media "
                + "library will then let you delete it; the new one is guarded immediately.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        api.MapPut("/contact-social", UpdateContactAndSocialAsync)
            .RequirePermission(Permissions.Settings.Edit)
            .WithName("AdminUpdateContactAndSocial")
            .WithSummary("The WhatsApp number and the social profile addresses (GLB-07, GLB-08).")
            .WithDescription(
                "PUT replaces, so send every field — one the body omits is one the API is told to clear. An "
                + "empty or absent value clears the key, which hides that social icon on the storefront "
                + "rather than linking it somewhere that is not the shop. Social addresses must be absolute "
                + "http(s) URLs with a host and no credentials.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        HttpContext httpContext,
        IQueryHandler<GetAdminSettingsQuery, AdminSettingsDto> handler,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(
            new GetAdminSettingsQuery(CurrentUserId(httpContext.User)),
            cancellationToken));

    private static async Task<IResult> SetStoreLogoAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<SetStoreLogoCommand, SettingsWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<SetStoreLogoRequest>(
            httpContext.Request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var result = await handler.HandleAsync(
            new SetStoreLogoCommand(
                body!.LogoAssetPublicId,
                AdminWriteRequest.RowVersion(httpContext.Request, body.RowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result);
    }

    private static async Task<IResult> UpdateContactAndSocialAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateContactAndSocialCommand, SettingsWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<UpdateContactAndSocialRequest>(
            httpContext.Request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var result = await handler.HandleAsync(
            new UpdateContactAndSocialCommand(
                body!.WhatsApp,
                body.FacebookUrl,
                body.InstagramUrl,
                body.TikTokUrl,
                AdminWriteRequest.RowVersion(httpContext.Request, body.RowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result);
    }

    /// <summary>
    /// The one mapping from a use case's answer to HTTP, shared by both write routes so a third cannot
    /// invent a different status for the same situation.
    /// </summary>
    private static IResult ToHttp(SettingsWriteResult result) => result switch
    {
        SettingsWriteResult.Succeeded => TypedResults.NoContent(),

        // The caller asked for the state the settings are already in. Nothing was written and no audit row
        // exists, and the answer is still success: the request's intent holds.
        SettingsWriteResult.Unchanged => TypedResults.NoContent(),

        SettingsWriteResult.Invalid invalid => ValidationProblem(invalid.Errors),

        // 500, not 404 and not 400. A missing seeded row is this deployment's fault, not the caller's, and
        // there is nothing a staff user can do about it on the screen — telling them to fix a field would
        // send them looking for one that is not there. The detail names the seeder.
        SettingsWriteResult.NotSeeded notSeeded => AdminWriteRequest.Problem(
            StatusCodes.Status500InternalServerError,
            "Settings are not set up",
            notSeeded.Message,
            "settings_not_seeded"),

        SettingsWriteResult.Conflict conflict => TypedResults.Problem(
            title: "This change cannot be applied",
            detail: conflict.Message,
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = conflict.Code }),

        _ => throw new InvalidOperationException($"Unhandled write result {result.GetType().Name}."),
    };

    /// <summary>One RFC 9457 response carrying every rejected field, keyed by its name in the request body.</summary>
    private static ValidationProblem ValidationProblem(IReadOnlyList<ValidationError> errors) =>
        TypedResults.ValidationProblem(
            errors
                .GroupBy(error => error.Field, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.Message).Distinct(StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal),
            title: "One or more fields are invalid.");

    private static int? CurrentUserId(ClaimsPrincipal user) =>
        int.TryParse(
            user.FindFirstValue(ClaimTypes.NameIdentifier),
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
}
