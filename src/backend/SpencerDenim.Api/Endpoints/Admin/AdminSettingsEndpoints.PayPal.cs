using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Sales.Payments.PayPal.Admin;
using SpencerDenim.Application.Settings;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// PayPal's panel on the settings screen (PAY-02, ADM-22): <c>PUT /api/admin/settings/paypal</c>.
/// <para>
/// A second file of <see cref="AdminSettingsEndpoints"/> rather than another method in the first, so the
/// payment providers that follow add their panels beside this one and the shared file stays a list of routes
/// that other tasks do not all edit. It shares the first file's body reader, its antiforgery check and its one
/// mapping from a use case's answer to HTTP — the things a tester established there and that a new route
/// should not re-derive.
/// </para>
/// <para>
/// <b>Settings.Payments only</b> (doc 08 §3.8 DB2), which no default role holds; a Super Admin's
/// <c>GrantsAll</c> does. The read is the existing <c>GET /api/admin/settings</c>, which already filters each
/// row by its own permission and <b>never returns a secret</b> — only that one is set.
/// </para>
/// </summary>
internal static partial class AdminSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAdminPayPalSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/settings").WithTags("Admin Settings");

        api.MapPut("/paypal", UpdatePayPalAsync)
            .RequirePermission(Permissions.Settings.Payments)
            .WithName("AdminUpdatePayPalSettings")
            .WithSummary("PayPal's credentials, exchange rate and timings (PAY-02, doc 08 §3.8).")
            .WithDescription(
                "Settings.Payments only. A field the body leaves out is not written, so a screen that cannot "
                + "read this group cannot clear it; send null or an empty string to clear one deliberately. "
                + "clientSecret is write-only: it is never returned, and an empty or missing value keeps the "
                + "stored secret. The rate's timestamp is stamped by the server when lkrPerUsd changes, or "
                + "when confirmRate is true; it cannot be sent. Every change is audited, and raises a "
                + "SensitiveSettingsChanged alert for the Super Admins. PayPal is not offered at checkout "
                + "until the client ID, secret and webhook ID are set and the rate is within its age limit.")
            .Accepts<UpdatePayPalSettingsRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return endpoints;
    }

    private static async Task<IResult> UpdatePayPalAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdatePayPalSettingsCommand, SettingsWriteResult> handler,
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

        // Presence, not just value, for the reason every settings group states: these fields need
        // Settings.Payments, which no default role holds, so a screen that cannot read them never sends them,
        // and reading that silence as null would clear the shop's PayPal credentials.
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var field in UpdatePayPalSettingsCommandHandler.FieldNames)
        {
            if (ReadString(body!, field, out var value, out var present) is { } badField)
            {
                return badField;
            }

            if (present)
            {
                fields[field] = value;
            }
        }

        if (ReadBool(body!, "confirmRate", out var confirmRate) is { } badConfirm)
        {
            return badConfirm;
        }

        if (ReadString(body!, "rowVersion", out var rowVersion, out _) is { } badVersion)
        {
            return badVersion;
        }

        var result = await handler.HandleAsync(
            new UpdatePayPalSettingsCommand(
                fields,
                confirmRate,
                AdminWriteRequest.RowVersion(httpContext.Request, rowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result);
    }

    /// <summary>
    /// Reads one optional boolean. Absent and null are false; a value of any other JSON type is a named field
    /// error rather than "malformed JSON".
    /// </summary>
    private static ValidationProblem? ReadBool(JsonObject body, string field, out bool value)
    {
        value = false;

        if (!body.TryGetPropertyValue(field, out var node) || node is null)
        {
            return null;
        }

        switch (node.GetValueKind())
        {
            case JsonValueKind.True:
                value = true;
                return null;

            case JsonValueKind.False:
                return null;

            default:
                return ValidationProblem([new ValidationError(field, "Send this field as true or false.")]);
        }
    }
}

/// <summary>
/// For the OpenAPI document only: the body is read as a <c>JsonObject</c> so that an absent field can be told
/// from a null one (see <see cref="UpdatePayPalSettingsCommand"/>). <c>AdminSettingsShapeTests</c>' sibling for
/// this group pins these names against the handler's own list.
/// </summary>
/// <param name="Mode"><c>sandbox</c> or <c>live</c>.</param>
/// <param name="ClientId">The REST app's client ID.</param>
/// <param name="ClientSecret">Write-only. Empty or missing keeps the stored secret.</param>
/// <param name="WebhookId">The webhook's ID from the PayPal dashboard.</param>
/// <param name="LkrPerUsd">Rupees per US dollar, e.g. <c>300.00</c>. Changing it stamps the rate's age.</param>
/// <param name="FxBufferPercent">The conversion buffer, 0 to 20.</param>
/// <param name="RateMaxAgeDays">How old the rate may be before PayPal is hidden, 1 to 30.</param>
/// <param name="CaptureLockHours">How long a capture in flight keeps its order from expiring, 1 to 24.</param>
/// <param name="CaptureRecoveryMaxHours">How long recovery keeps asking PayPal, 1 to 168.</param>
/// <param name="PendingCaptureMaxDays">How long a pending capture holds the order, 1 to 14.</param>
/// <param name="ExpiryMinutes">How long a customer has to pay by PayPal, 10 to 180.</param>
/// <param name="ConfirmRate">True to re-stamp the rate's age without changing it.</param>
/// <param name="RowVersion">The group's token from the read model.</param>
public sealed record UpdatePayPalSettingsRequest(
    string? Mode,
    string? ClientId,
    string? ClientSecret,
    string? WebhookId,
    string? LkrPerUsd,
    string? FxBufferPercent,
    string? RateMaxAgeDays,
    string? CaptureLockHours,
    string? CaptureRecoveryMaxHours,
    string? PendingCaptureMaxDays,
    string? ExpiryMinutes,
    bool? ConfirmRate,
    string? RowVersion);
