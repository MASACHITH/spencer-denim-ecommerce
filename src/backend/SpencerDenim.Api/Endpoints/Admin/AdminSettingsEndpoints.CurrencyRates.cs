using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Admin;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The display-currency panel on the settings screen (issue #248): <c>PUT /api/admin/settings/currency-rates</c>.
/// <para>
/// A second file of <see cref="AdminSettingsEndpoints"/>, as PayPal's and the mail account's are, so the shared file
/// stays a list of routes that other tasks do not all edit. It shares that file's body reader, antiforgery check and
/// the one mapping from a use case's answer to HTTP, which are the things a tester established there.
/// </para>
/// <para>
/// <b><c>Settings.Edit</c>, so a Store Manager can keep the rates current</b>, and no sensitive-change alert: these
/// rates only change what a price <i>looks like</i>. The read is the existing <c>GET /api/admin/settings</c>, which
/// already returns each row with its <c>UpdatedAtUtc</c> (the rate's age) and filters it by its own permission.
/// </para>
/// </summary>
internal static partial class AdminSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAdminCurrencyRatesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/settings").WithTags("Admin Settings");

        api.MapPut("/currency-rates", UpdateCurrencyRatesAsync)
            .RequirePermission(Permissions.Settings.Edit)
            .WithName("AdminUpdateCurrencyRates")
            .WithSummary("The exchange rates prices are shown in (issue #248, doc 08 §3.8 group Currency.Display).")
            .WithDescription(
                "Display only: usd, gbp, eur, aud and aed, each the rupees one unit costs, as digits with an optional "
                + "decimal point and at most four decimal places, above 0 and no more than 1,000,000. A field the "
                + "body leaves out is not written; an empty or null one CLEARS that rate, and the currency is then "
                + "not offered to shoppers. Nothing here changes what PayPal charges. Audited, one row per changed "
                + "rate, and a rate's age is the row's UpdatedAtUtc. No sensitive-change alert.")
            .Accepts<UpdateCurrencyRatesRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return endpoints;
    }

    /// <summary>The five display rates (issue #248).</summary>
    /// <remarks>
    /// Presence-aware like every other settings write: a field the body omits is not written. Numbers arrive as
    /// text, as the screen's other numeric boxes send them; a JSON number is a named field error rather than a
    /// silent coercion, for the reason <see cref="ReadString"/> gives.
    /// </remarks>
    private static async Task<IResult> UpdateCurrencyRatesAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateCurrencyRatesCommand, SettingsWriteResult> handler,
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

        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var field in UpdateCurrencyRatesCommandHandler.Fields)
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

        if (ReadString(body!, "rowVersion", out var rowVersion, out _) is { } badVersion)
        {
            return badVersion;
        }

        var result = await handler.HandleAsync(
            new UpdateCurrencyRatesCommand(
                fields,
                AdminWriteRequest.RowVersion(httpContext.Request, rowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result);
    }
}

/// <summary>
/// The display rates (issue #248). <b>For the OpenAPI document only</b>: the body is read as a <c>JsonObject</c> so an
/// absent field can be told from an empty one. A shape test pins these names against the handler's own list.
/// </summary>
/// <param name="Usd">Rupees for one US dollar, e.g. <c>321.5</c>. Empty or null clears it.</param>
/// <param name="Gbp">Rupees for one pound sterling. Empty or null clears it.</param>
/// <param name="Eur">Rupees for one euro. Empty or null clears it.</param>
/// <param name="Aud">Rupees for one Australian dollar. Empty or null clears it.</param>
/// <param name="Aed">Rupees for one UAE dirham. Empty or null clears it.</param>
/// <param name="RowVersion">The <c>Currency.Display</c> group's concurrency token, as the screen read it.</param>
public sealed record UpdateCurrencyRatesRequest(
    string? Usd,
    string? Gbp,
    string? Eur,
    string? Aud,
    string? Aed,
    string? RowVersion);
