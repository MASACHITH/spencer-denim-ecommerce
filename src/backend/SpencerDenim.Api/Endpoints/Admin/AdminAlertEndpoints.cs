using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Text.Json.Nodes;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Ops.Alerts;
using SpencerDenim.Application.Ops.Alerts.Contracts;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The admin shell's alerts and their acknowledge (doc 08 §3.8 N2; ADM-04's banner, built in the shell
/// rather than on the dashboard, and drawn as the bell in the header — see <c>AlertBell</c> in the admin app).
/// <para>
/// <b>Doc 08 §3.8 R10 decides what comes back, per caller, inside the use case.</b> The route guard only
/// has to let a caller who could hold <i>any</i> alert in at all: GrantsAll holders see every type, and
/// <c>Orders.Approve</c> / <c>Orders.Refund</c> holders see <c>PaymentException</c> and
/// <c>CaptureRecoveryFailed</c>. The read model filters row by row against the caller's own effective
/// permissions, which is the same arrangement — and the same justification — as
/// <c>GET /api/admin/settings</c>: see <c>AnyPermissionRequirement</c>.
/// </para>
/// <para>
/// <b>Acknowledging records that a human saw the alert. It fixes nothing</b> (doc 08 §3.8; doc 11 §6.1:
/// "Nothing is ever resolved by silencing it"). This endpoint resends no email, retries no payment and
/// adjusts no stock, and its summaries say so because an admin screen built from them must not imply
/// otherwise.
/// </para>
/// <para>
/// <b>Antiforgery is validated by hand</b> on the mutating route, as every other admin endpoint file in
/// this project does, so a refusal is a problem document a client can read rather than an empty 400.
/// </para>
/// </summary>
internal static class AdminAlertEndpoints
{
    public static IEndpointRouteBuilder MapAdminAlertEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/alerts").WithTags("Admin Alerts");

        api.MapGet("/", GetOpenAsync)
            .RequireAnyAlertAudience()
            .WithName("AdminGetOpenAlerts")
            .WithSummary("Open admin alerts for the signed-in staff user (doc 08 §3.8 R10).")
            .WithDescription(
                "Newest first, at most ten, with a flag saying whether there are more than those ten — "
                + "not a total, because a count over the same predicate cannot use IX_AdminAlerts_Open "
                + "(5,020 logical reads against the list's 38 on 20,000 alerts). A Super Admin sees "
                + "every type; an Orders.Approve or Orders.Refund "
                + "holder sees PaymentException and CaptureRecoveryFailed only, and anybody else sees an "
                + "empty list. EmailDeliveryFailed is Super Admins only (doc 11 §7 G-37).")
            .Produces<AdminAlertBannerDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        api.MapPost("/{alertId:int}/acknowledge", AcknowledgeAsync)
            .RequireAnyAlertAudience()
            .WithName("AdminAcknowledgeAlert")
            .WithSummary("Record that you have seen an alert (doc 08 §3.8).")
            .WithDescription(
                "Stamps the row with your user id and the time, and writes an AlertAcknowledged audit "
                + "row in the same transaction. It does not resolve the underlying issue: nothing is "
                + "resent, retried or adjusted. The body takes no fields: doc 08 §3.8 gives the table "
                + "an AcknowledgementNote column and nothing in this build writes it (owner's decision, "
                + "2026-10-09 — ops.AdminAlerts is not in doc 08 §3.1 step 2's anonymisation deletion "
                + "list), so a request that carries a note is refused rather than having it dropped. An "
                + "alert somebody else acknowledged first answers 200 with their time, because "
                + "the banner is shown until one holder clears it; an id you have no path to answers 404, "
                + "the same as an id that does not exist.")
            .Produces<AdminAlertAcknowledgementDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> GetOpenAsync(
        HttpContext httpContext,
        IQueryHandler<GetOpenAdminAlertsQuery, AdminAlertBannerDto> handler,
        CancellationToken cancellationToken)
    {
        if (!AdminSessionCookies.TryGetUserId(httpContext.User, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        // A staff work list naming orders and payment problems. Never cached anywhere, and keyed on the
        // cookie if any intermediary ignores that — the same rule the bank queue follows, and it matters
        // more here because the answer differs per user (R10).
        NoStore(httpContext.Response);

        return TypedResults.Ok(
            await handler.HandleAsync(new GetOpenAdminAlertsQuery(userId), cancellationToken));
    }

    private static async Task<IResult> AcknowledgeAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<AcknowledgeAdminAlertCommand, AcknowledgeAdminAlertResult> handler,
        int alertId,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        // A JsonObject read by hand rather than a bound record, the pattern AdminSettingsEndpoints
        // established and AdminBankTransferEndpoints follows: a malformed body then answers a readable
        // 400 with a code rather than "malformed JSON" or, in Development, a 500 (issue #47).
        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<JsonObject>(
            httpContext.Request,
            cancellationToken);

        if (problem is not null)
        {
            return problem;
        }

        if (RejectNote(body!) is { } unwanted)
        {
            return unwanted;
        }

        var result = await handler.HandleAsync(
            new AcknowledgeAdminAlertCommand(alertId, AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        NoStore(httpContext.Response);

        return ToHttp(result);
    }

    /// <summary>
    /// Refuses a request that carries an acknowledgement note, and takes no other field.
    /// </summary>
    /// <remarks>
    /// <b>Refused, not ignored.</b> Doc 08 §3.8 defines <c>AcknowledgementNote</c> and the migration
    /// creates it; nothing in this build writes it (owner's decision, 2026-10-09 — see
    /// <c>AdminAlertAcknowledger</c>). A route that accepted the field and dropped it would be the shape
    /// of promise this project has already shipped twice: the caller believes their sentence was kept.
    /// A <c>null</c> note is accepted, because that is what a client sending "no note" looks like.
    /// </remarks>
    private static ValidationProblem? RejectNote(JsonObject body)
    {
        if (!body.TryGetPropertyValue("note", out var value) || value is null)
        {
            return null;
        }

        return TypedResults.ValidationProblem(
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["note"] =
                [
                    "This build does not store an acknowledgement note, so one cannot be sent. "
                    + "Acknowledging records who saw the alert and when.",
                ],
            },
            title: "One or more fields are invalid.");
    }

    private static IResult ToHttp(AcknowledgeAdminAlertResult result) => result switch
    {
        AcknowledgeAdminAlertResult.Acknowledged acknowledged => TypedResults.Ok(
            new AdminAlertAcknowledgementDto(
                acknowledged.AlertId,
                acknowledged.AlertType,
                acknowledged.AcknowledgedAtUtc,
                AlreadyAcknowledged: false)),

        // 200 and not 409: doc 08 §3.8 shows the banner "until one of them acknowledges it", so a second
        // Super Admin pressing the button has the outcome they wanted. The flag lets the screen say so.
        AcknowledgeAdminAlertResult.AlreadyAcknowledged already => TypedResults.Ok(
            new AdminAlertAcknowledgementDto(
                already.AlertId,
                already.AlertType,
                already.AcknowledgedAtUtc,
                AlreadyAcknowledged: true)),

        // A bare 404 with no body, the rule the order routes already follow: "no such alert" and "not a
        // type you may see" (R10) must be one answer, or the route becomes a way to count the shop's
        // security alerts.
        AcknowledgeAdminAlertResult.NotFound => TypedResults.NotFound(),

        // Unreachable from this route, which refuses a note in RejectNote before the handler is
        // reached. Mapped because the result type declares it and an unhandled case throws.
        AcknowledgeAdminAlertResult.NoteNotAccepted => TypedResults.ValidationProblem(
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["note"] = ["This build does not store an acknowledgement note."],
            },
            title: "One or more fields are invalid."),

        AcknowledgeAdminAlertResult.NoActor => TypedResults.Unauthorized(),

        _ => throw new InvalidOperationException($"Unhandled acknowledge result {result.GetType().Name}."),
    };

    /// <summary>
    /// A staff work list, so it is never stored by a browser or an intermediary.
    /// </summary>
    /// <remarks>
    /// <b>The same three lines as <c>AdminBankTransferEndpoints</c> and <c>AdminOrderEndpoints</c>, with
    /// the same values, deliberately.</b> There are already five private copies of this in
    /// <c>SpencerDenim.Api</c> and this is a sixth; extracting them is a change to five endpoint files,
    /// three of which are money paths, and is not this task's. What a sixth copy must not do is differ —
    /// a `must-revalidate` or a `Pragma` here and not there would be two admin work lists cached
    /// differently for no reason anybody could find — so the header is byte for byte theirs.
    /// </remarks>
    private static void NoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, private";
        response.Headers.Vary = "Cookie";
    }
}

/// <summary>
/// What an acknowledgement answers.
/// </summary>
/// <param name="AcknowledgedAtUtc">
/// When the alert was cleared — this request's time, or the earlier one's when
/// <paramref name="AlreadyAcknowledged"/> is true.
/// </param>
/// <param name="AlreadyAcknowledged">
/// True when somebody else had already acknowledged it. Not a failure: the screen says when it was
/// cleared instead of showing an error.
/// </param>
public sealed record AdminAlertAcknowledgementDto(
    int AlertId,
    string AlertType,
    DateTime AcknowledgedAtUtc,
    bool AlreadyAcknowledged);
