using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Sales.Orders.Admin;
using SpencerDenim.Application.Sales.Payments.Admin;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Admin;
using SpencerDenim.Application.Settings.Contracts;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The settings screen (ADM-22) and the shop's branding (GLB-02).
/// <para>
/// <b>Permissions.</b> The routes do <i>not</i> share one guard. The read is behind
/// <c>RequireAnySettingsPermission()</c>; <c>store-logo</c> and <c>contact-social</c> are behind
/// <c>Settings.Edit</c>, the permission doc 08 §3.8 names for the <c>Store</c>, <c>Contact</c> and
/// <c>Social</c> groups; and <c>bank</c> is behind <c>Settings.Payments</c>. The read then filters
/// again, per row, against the caller's effective permissions, so a <c>Settings.Edit</c> holder
/// reaching this route does not start seeing <c>Bank.*</c> — and when <c>Email.Smtp.*</c> arrives with
/// W3-06 it will not change that. The API is the security boundary (doc 03 §3a); the admin UI's hiding
/// is convenience.
/// </para>
/// <para>
/// <b>This paragraph used to describe the single-permission guard as a limitation W3-03 would meet, and
/// it was right.</b> It predicted that a caller holding <i>only</i> <c>Settings.Payments</c> could not
/// reach the screen at all, not even to read the group that was theirs, and said the guard "is what has
/// to widen when the payments group lands". W3-03 landed it and widened it, which is why the read now
/// takes any settings permission rather than one. The reasoning is kept rather than deleted because it
/// is the argument for <i>why</i> the read's guard and the writes' guards differ — not an oversight, but
/// a per-group boundary that the per-row filter in the read model completes.
/// </para>
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
internal static partial class AdminSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAdminSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/settings").WithTags("Admin Settings");

        api.MapGet("/", GetAsync)
            .RequireAnySettingsPermission()
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
            .Accepts<SetStoreLogoRequest>("application/json")
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
            .Accepts<UpdateContactAndSocialRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        api.MapPut("/bank", UpdateBankDetailsAsync)
            .RequirePermission(Permissions.Settings.Payments)
            .WithName("AdminUpdateBankDetails")
            .WithSummary("The bank account customers transfer to (PAY-01, doc 11 P7).")
            .WithDescription(
                "Settings.Payments only, which no default role holds \u2014 a Super Admin sets these. Bank "
                + "name, account name and account number are what a transfer needs: until all three are "
                + "filled in, the order page tells shoppers the details are being confirmed rather than "
                + "showing a half-filled form. A field the body leaves out is not written, so a screen "
                + "that cannot read this group cannot clear it; send null to clear one deliberately.")
            .Accepts<UpdateBankDetailsRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        api.MapPut("/invoice-and-fulfilment", UpdateInvoiceAndFulfilmentAsync)
            .RequirePermission(Permissions.Settings.Edit)
            .WithName("AdminUpdateInvoiceAndFulfilmentSettings")
            .WithSummary("The company's invoice details and the auto-deliver window (ADM-09, doc 08 §3.8).")
            .WithDescription(
                "The registered name, address, BR number and VAT number an invoice prints, and how many "
                + "days after dispatch the system closes an order nobody has confirmed. **Nothing is "
                + "invented for an unset value**: an invoice prints no line at all rather than a "
                + "placeholder, because it is a legal document. The registered name is the company's, not "
                + "the shop's \u2014 everything a shopper reads is the shop's name. A field the body "
                + "leaves out is not written; send null to clear one deliberately. Emptying the "
                + "auto-deliver window stops the job rather than defaulting it, so staff then confirm "
                + "every delivery by hand.")
            .Accepts<UpdateInvoiceAndFulfilmentSettingsRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        api.MapPut("/traffic", UpdateTrafficAsync)
            .RequirePermission(Permissions.Settings.Edit)
            .WithName("AdminUpdateTrafficSettings")
            .WithSummary("How long raw page views are kept before the nightly purge (ADM-20, doc 08 §8.4).")
            .WithDescription(
                "Whole days as digits: rawRetentionDays 30 to 365, botRetentionDays 7 to 90 and never more than "
                + "rawRetentionDays. A field the body leaves out is not written; an empty or null one is refused, "
                + "because an unset period would silently hand the purge back to the server's configuration. The "
                + "saved values win over Traffic__RawRetentionDays and Traffic__BotRetentionDays from the next "
                + "nightly run. The daily totals the Traffic page shows are kept whatever these say.")
            .Accepts<UpdateTrafficSettingsRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        api.MapPut("/reviews", UpdateReviewsAsync)
            .RequirePermission(Permissions.Settings.Edit)
            .WithName("AdminUpdateReviewSettings")
            .WithSummary("Whether customers' reviews wait for approval before they appear (PDP-08, doc 11 P12).")
            .WithDescription(
                "Body: { requireApproval, rowVersion }. requireApproval is true or false; left out, nothing is "
                + "written; null is refused. On (the default) a new or edited review waits on the Reviews screen; off, "
                + "it appears at once. A review staff have hidden stays hidden either way. Audited; applies to the "
                + "next review written.")
            .Accepts<UpdateReviewSettingsRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return endpoints;
    }

    /// <summary>The review approval switch (issue #189).</summary>
    private static async Task<IResult> UpdateReviewsAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateReviewSettingsCommand, SettingsWriteResult> handler,
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

        bool? requireApproval = null;
        const string field = UpdateReviewSettingsCommandHandler.RequireApprovalField;

        if (body!.ContainsKey(field))
        {
            if (AdminJsonBody.ReadBool(body, field, out requireApproval) is { } badFlag)
            {
                return badFlag;
            }

            if (requireApproval is null)
            {
                return AdminJsonBody.Problem([new ValidationError(field, "Choose on or off; this cannot be left empty.")]);
            }
        }

        if (ReadString(body, "rowVersion", out var rowVersion, out _) is { } badVersion)
        {
            return badVersion;
        }

        var result = await handler.HandleAsync(
            new UpdateReviewSettingsCommand(
                requireApproval,
                AdminWriteRequest.RowVersion(httpContext.Request, rowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result);
    }

    /// <summary>The Traffic form's two retention periods (issue #193).</summary>
    /// <remarks>
    /// Presence-aware like every other settings write: a field the body omits is not written. Numbers arrive as
    /// text, as the screen's other numeric boxes send them; a JSON number is a named field error rather than a
    /// silent coercion, for the reason <see cref="ReadString"/> gives.
    /// </remarks>
    private static async Task<IResult> UpdateTrafficAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateTrafficSettingsCommand, SettingsWriteResult> handler,
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

        foreach (var field in UpdateTrafficSettingsCommandHandler.Fields)
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
            new UpdateTrafficSettingsCommand(
                fields,
                AdminWriteRequest.RowVersion(httpContext.Request, rowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result);
    }

    /// <summary>
    /// ADM-09's invoice details and the auto-deliver window.
    /// </summary>
    /// <remarks>
    /// The same body reader as the bank group's, and for the same reason: presence, not just value. These
    /// are <c>Settings.Edit</c> so every caller who reaches the form receives every box — but a screen that
    /// omitted one would still clear it if absent meant null, and that is the defect four of this project's
    /// own have been.
    /// </remarks>
    private static async Task<IResult> UpdateInvoiceAndFulfilmentAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateInvoiceAndFulfilmentSettingsCommand, SettingsWriteResult> handler,
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

        foreach (var field in InvoiceAndFulfilmentFields)
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
            new UpdateInvoiceAndFulfilmentSettingsCommand(
                fields,
                AdminWriteRequest.RowVersion(httpContext.Request, rowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result);
    }

    private static async Task<IResult> UpdateBankDetailsAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateBankDetailsCommand, SettingsWriteResult> handler,
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

        // Presence, not just value \u2014 the same reading as contact-and-social and for a sharper reason.
        // These five keys need Settings.Payments, which no default role holds, so a Store Manager's screen
        // genuinely does not receive them; if absent meant null their first save would wipe the shop's bank
        // account number, and every order placed afterwards would show a customer no account to pay into.
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var field in BankFields)
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
            new UpdateBankDetailsCommand(
                fields,
                AdminWriteRequest.RowVersion(httpContext.Request, rowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result);
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

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<JsonObject>(
            httpContext.Request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (ReadGuid(body!, "logoAssetPublicId", out var asset, out var present) is { } badField)
        {
            return badField;
        }

        if (ReadString(body!, "rowVersion", out var rowVersion, out _) is { } badVersion)
        {
            return badVersion;
        }

        var result = await handler.HandleAsync(
            new SetStoreLogoCommand(
                present,
                asset,
                AdminWriteRequest.RowVersion(httpContext.Request, rowVersion),
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

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<JsonObject>(
            httpContext.Request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        // Presence, not just value: absent means "I am not writing this key" and null means "clear it". The
        // first version collapsed the two and would have cleared three social addresses for any caller whose
        // screen could not read them (see UpdateContactAndSocialCommand).
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var field in ContactAndSocialFields)
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
            new UpdateContactAndSocialCommand(
                fields,
                AdminWriteRequest.RowVersion(httpContext.Request, rowVersion),
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

        // 403 rather than 400: nothing is wrong with the field, the caller may not change that key. Named, so
        // the screen can say which box to leave alone rather than "something went wrong".
        SettingsWriteResult.Forbidden forbidden => TypedResults.Problem(
            title: "You cannot change these settings",
            detail: "You do not have permission to change: " + string.Join(", ", forbidden.Keys),
            statusCode: StatusCodes.Status403Forbidden,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = SettingsConflictCodes.SettingForbidden,
                ["keys"] = forbidden.Keys,
            }),

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

    /// <summary>The fields the contact-and-social body may carry, in the order they are read.</summary>
    private static readonly string[] ContactAndSocialFields =
    [
        "whatsApp",
        "phone",
        "email",
        "address",
        "openingHours",
        "mapEmbedUrl",
        "facebookUrl",
        "instagramUrl",
        "tikTokUrl",
        "youTubeUrl",
    ];

    /// <summary>The fields the bank body may carry, in the order the screen shows them.</summary>
    /// <summary>
    /// ADM-09's five, in the order the form shows them. One list, so the reader and the handler cannot
    /// disagree about the set of fields a request may carry.
    /// </summary>
    private static readonly string[] InvoiceAndFulfilmentFields =
    [
        "legalName",
        "registeredAddress",
        "brNumber",
        "vatNumber",
        "autoDeliverAfterDays",
    ];

    private static readonly string[] BankFields =
        ["bankName", "branch", "accountName", "accountNumber", "instructions"];

    /// <summary>
    /// Reads one optional string property, telling <i>absent</i> from <i>null</i>.
    /// <para>
    /// <b>And a value of the wrong JSON type is a named field error, not "malformed JSON".</b> That is the
    /// improvement this hand-reading buys over binding a record: <c>{"whatsApp": 123}</c> parses perfectly
    /// well, and the useful answer names the field rather than telling a staff user their request was not JSON
    /// (issue #47's family — the status must never depend on the environment, and the message must point at
    /// something a person can fix).
    /// </para>
    /// </summary>
    private static ValidationProblem? ReadString(
        JsonObject body,
        string field,
        out string? value,
        out bool present)
    {
        value = null;
        present = body.TryGetPropertyValue(field, out var node);

        if (!present || node is null)
        {
            return null;
        }

        if (node.GetValueKind() != JsonValueKind.String)
        {
            return ValidationProblem([new ValidationError(field, "Send this field as text, or as null to clear it.")]);
        }

        value = node.GetValue<string>();

        return null;
    }

    /// <inheritdoc cref="ReadString"/>
    private static ValidationProblem? ReadGuid(
        JsonObject body,
        string field,
        out Guid? value,
        out bool present)
    {
        value = null;

        if (ReadString(body, field, out var text, out present) is { } problem)
        {
            return problem;
        }

        if (text is null)
        {
            return null;
        }

        if (!Guid.TryParse(text, CultureInfo.InvariantCulture, out var parsed))
        {
            return ValidationProblem(
            [
                new ValidationError(
                    field,
                    "That is not a media asset id. Upload the image first and send the publicId it returns."),
            ]);
        }

        value = parsed;

        return null;
    }

    private static int? CurrentUserId(ClaimsPrincipal user) =>
        int.TryParse(
            user.FindFirstValue(ClaimTypes.NameIdentifier),
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
}
