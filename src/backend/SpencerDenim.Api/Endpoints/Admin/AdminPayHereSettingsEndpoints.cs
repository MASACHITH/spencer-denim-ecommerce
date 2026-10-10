using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Sales.Payments.PayHere;
using SpencerDenim.Application.Settings;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The PayHere panel of the settings screen (doc 11 P8b; <c>Settings.Payments</c>): the merchant account, the
/// mode and the two windows, and a read of whether checkout would offer PayHere and which address to register
/// with PayHere.
/// <para>
/// <b>Beside <c>AdminSettingsEndpoints</c> and not in it</b>, so that a second payment gateway's panel is a
/// second file and the shared one is untouched; it reuses that class's body reader and its one mapping from a
/// write result to HTTP, so the two cannot answer the same situation differently.
/// </para>
/// <para>
/// <b>The secret is write-only at every layer.</b> The settings read (<c>GET /api/admin/settings</c>) returns
/// <c>hasValue</c> for it and never the value; this panel's <c>PUT</c> accepts it and keeps the stored one when
/// the box is blank; the status read below does not read it at all. Nothing here, and nothing in a response
/// to a request made here, carries it back.
/// </para>
/// </summary>
internal static class AdminPayHereSettingsEndpoints
{
    /// <summary>The fields the body may carry, in the order the panel shows them.</summary>
    private static readonly string[] Fields =
        ["mode", "merchantId", "merchantSecret", "pendingMaxDays", "expiryMinutes"];

    public static IEndpointRouteBuilder MapAdminPayHereSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/settings/payhere").WithTags("Admin Settings");

        api.MapPut("/", UpdateAsync)
            .RequirePermission(Permissions.Settings.Payments)
            .WithName("AdminUpdatePayHereSettings")
            .WithSummary("The PayHere merchant account, mode and windows (PAY-03, doc 11 P8b).")
            .WithDescription(
                "Settings.Payments only, which no default role holds — a Super Admin sets these. mode is "
                + "sandbox or live; merchantId and merchantSecret come from the PayHere merchant portal. "
                + "The secret is stored encrypted and is never returned: a blank or absent merchantSecret "
                + "keeps the stored one. A field the body leaves out is not written; send null to clear "
                + "the merchant ID deliberately. Every save writes one audit row per changed key (secret "
                + "values logged as [secret changed]) and raises a SensitiveSettingsChanged alert.")
            .Accepts<UpdatePayHereSettingsRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        api.MapGet("/status", StatusAsync)
            .RequirePermission(Permissions.Settings.Payments)
            .WithName("AdminGetPayHereStatus")
            .WithSummary("Whether checkout offers PayHere, what is missing, and the notify address to register.")
            .WithDescription(
                "Settings.Payments only. notifyUrl is the address to enter in the PayHere merchant portal "
                + "(Settings, Domains and Credentials); PayHere also refuses checkout from a domain that "
                + "has not been approved there. providerEnabled is the switch on the payment provider row, "
                + "which is changed in the database — there is no screen for it. Never carries the secret.")
            .Produces<PayHereStatusDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdatePayHereSettingsCommand, SettingsWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<JsonObject>(
            httpContext.Request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        // Presence, not just value, for the reason every group in ADM-22 gives: absent means "leave this
        // key alone" and null means "clear it" - and for the secret, where the screen never receives the
        // stored value, a blank means "keep it".
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var field in Fields)
        {
            if (AdminSettingsEndpoints.ReadString(body!, field, out var value, out var present) is { } badField)
            {
                return badField;
            }

            if (present)
            {
                fields[field] = value;
            }
        }

        if (AdminSettingsEndpoints.ReadString(body!, "rowVersion", out var rowVersion, out _) is { } badVersion)
        {
            return badVersion;
        }

        var result = await handler.HandleAsync(
            new UpdatePayHereSettingsCommand(
                fields,
                AdminWriteRequest.RowVersion(httpContext.Request, rowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return AdminSettingsEndpoints.ToHttp(result);
    }

    private static async Task<IResult> StatusAsync(
        IQueryHandler<GetPayHereStatusQuery, PayHereStatusDto> handler,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(new GetPayHereStatusQuery(), cancellationToken));
}

/// <summary>
/// The body of <c>PUT /api/admin/settings/payhere</c>, for the OpenAPI document only: the endpoint reads a
/// <c>JsonObject</c> so that an absent field can be told from a null one.
/// </summary>
/// <param name="Mode"><c>sandbox</c> or <c>live</c>.</param>
/// <param name="MerchantId">The PayHere merchant ID. Null clears it.</param>
/// <param name="MerchantSecret">The merchant secret. Write-only; blank or absent keeps the stored one.</param>
/// <param name="PendingMaxDays">Whole days, as digits, 1 to 14.</param>
/// <param name="ExpiryMinutes">Whole minutes, as digits, 15 to 1440.</param>
/// <param name="RowVersion">The Payments.PayHere group's token from the settings read.</param>
public sealed record UpdatePayHereSettingsRequest(
    string? Mode,
    string? MerchantId,
    string? MerchantSecret,
    string? PendingMaxDays,
    string? ExpiryMinutes,
    string? RowVersion);
