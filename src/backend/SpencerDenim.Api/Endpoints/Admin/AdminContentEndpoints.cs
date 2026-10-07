using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Content.Admin;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Application.Content.Admin.Contracts;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The admin content pages and the contact-enquiry inbox (ADM-17, GLB-10, doc 11 P13 part F and P15 part B).
/// <para>
/// <b>Two modules in one file because they are two modules in one screen group, and their permissions are
/// deliberately different.</b> Content pages are <c>Content.View/Create/Edit/Delete</c>. The enquiry inbox
/// is <c>Customers.View/Edit/Delete</c> — doc 08 §3.7's G-06 resolution: "Contact submissions are customer
/// data, so they are governed by the <b>Customers</b> module, not by Content." A Content Editor can rewrite
/// the Contact page and cannot read a single enquiry, which is the right division.
/// </para>
/// <para>
/// <b>The permission check is the API's, and it reads the user's roles rather than their cookie.</b>
/// <c>RequirePermission</c> resolves through <c>PermissionAuthorizationHandler</c>, which asks
/// <c>IEffectivePermissionsProvider</c> on every request (doc 08 §3.1 R4) — so a staff member demoted five
/// minutes ago cannot still publish a policy page out of their own session.
/// </para>
/// <para>
/// <b>There is no one-bit oracle here</b>, which ADM-22's settings save had and had to be fixed for. That
/// bug came from per-row permissions evaluated <i>after</i> a diff: submitting a value equal to the stored
/// one answered 204 and a different one answered 403, so one guess leaked one bit about a value the caller
/// could not read. These routes carry a single permission each, enforced before the handler runs and
/// without reference to anything the body contains, so no answer here depends on what the submitted value
/// would have changed.
/// </para>
/// <para>
/// <b>Antiforgery</b> is validated by hand on every mutating route, as <c>AdminCatalogEndpoints</c> and
/// <c>AdminSettingsEndpoints</c> do, so the refusal is a problem document a client can read.
/// </para>
/// </summary>
internal static class AdminContentEndpoints
{
    /// <summary>The inbox's page size ceiling. A staff member reads enquiries, not a thousand of them.</summary>
    private const int MaxPageSize = 100;

    private const int DefaultPageSize = 25;

    public static IEndpointRouteBuilder MapAdminContentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var pages = endpoints.MapGroup("/api/admin/content/pages").WithTags("Admin Content Pages");

        pages.MapGet("/", GetPagesAsync)
            .RequirePermission(Permissions.Content.View)
            .WithName("AdminListContentPages")
            .WithSummary("Every content page, published or not.")
            .WithDescription(
                "GLB-10. The six system pages first, in their seeded order, then any custom pages by "
                + "title. Unlike GET /api/content/pages this does not hide a draft: on a fresh shop every "
                + "page is one, and a screen that could not see a draft could not publish it. `hasBody` "
                + "is false for a page the client has not written yet, which is how all six ship — doc 11 "
                + "P13: they “stay unpublished until the client supplies the copy”. Each row carries its "
                + "rowVersion for an optimistic-concurrency check on the way back.")
            .Produces<IReadOnlyList<AdminContentPageListItemDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        pages.MapGet("/{pageId:int}", GetPageAsync)
            .RequirePermission(Permissions.Content.View)
            .WithName("AdminGetContentPage")
            .WithSummary("One content page, with its body and SEO fields.")
            .Produces<AdminContentPageDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        pages.MapPost("/", CreatePageAsync)
            .RequirePermission(Permissions.Content.Create)
            .WithName("AdminCreateContentPage")
            .WithSummary("Create an extra content page.")
            .WithDescription(
                "GLB-10, doc 11 P13 step 12. It is created as a draft and always as a *custom* page: there "
                + "is no way to ask for one of the six system keys, because that would allow a second "
                + "`privacy` page or a custom page claiming the protection against deletion. The address "
                + "must be a slug, must be free, and must not be one the storefront already answers "
                + "(/bag, /search, a department listing — a page saved there could never be opened).")
            .Accepts<AdminContentPageRequest>("application/json")
            .Produces<ContentPageSavedResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        pages.MapPut("/{pageId:int}", UpdatePageAsync)
            .RequirePermission(Permissions.Content.Edit)
            .WithName("AdminUpdateContentPage")
            .WithSummary("Edit a page's heading, address, body and SEO fields.")
            .WithDescription(
                "GLB-10, doc 11 P13 step 12. **A field this body does not carry is left exactly as it is**, "
                + "and a field it carries as null is cleared — two different instructions, and collapsing "
                + "them is how W1-12 wiped every category image. So a screen may send only what it changed. "
                + "The body is cleaned with the allow-list sanitiser on save (doc 08 §3.7) and the response "
                + "says what was removed. Send the row version in the If-Match header or the body.")
            .Accepts<AdminContentPageRequest>("application/json")
            .Produces<ContentPageSavedResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        pages.MapPut("/{pageId:int}/published", SetPagePublishedAsync)
            .RequirePermission(Permissions.Content.Edit)
            .WithName("AdminSetContentPagePublished")
            .WithSummary("Put a page on the shop, or take it off.")
            .WithDescription(
                "Its own route rather than a field on the edit form, so that putting a policy page in front "
                + "of shoppers is a deliberate act with its own StatusChange audit row — and never a side "
                + "effect of saving a form whose checkbox was stale. Publishing is refused with 409 "
                + "'alt_text_required' while the body holds an image with no alt text (doc 08 §3.3, GLB-12).")
            .Accepts<SetContentPagePublishedRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        pages.MapDelete("/{pageId:int}", DeletePageAsync)
            .RequirePermission(Permissions.Content.Delete)
            .WithName("AdminDeleteContentPage")
            .WithSummary("Remove a custom content page.")
            .WithDescription(
                "A soft delete, which releases the address for reuse. Refused with 409 "
                + "'system_page_not_deletable' for one of the six built-in pages (doc 11 P13: “System "
                + "pages cannot be deleted”) — a 409 and not a 403, because the caller does hold the "
                + "permission and nothing about it would change the answer. Send the row version in "
                + "If-Match.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        MapEnquiryEndpoints(endpoints);

        return endpoints;
    }

    /// <summary>
    /// The contact-enquiry inbox (GLB-10, doc 11 P15 step 7), under the <b>Customers</b> permissions that
    /// doc 08 §3.7's G-06 resolution assigns it.
    /// </summary>
    private static void MapEnquiryEndpoints(IEndpointRouteBuilder endpoints)
    {
        var enquiries = endpoints.MapGroup("/api/admin/contact-enquiries").WithTags("Admin Contact Enquiries");

        enquiries.MapGet("/", GetEnquiriesAsync)
            .RequirePermission(Permissions.Customers.View)
            .WithName("AdminListContactEnquiries")
            .WithSummary("The enquiry inbox, newest first.")
            .WithDescription(
                "GLB-10, doc 11 P15 step 7. Filter by status with ?status=New|Read|Replied|Spam. The "
                + "message body is **not** in this payload — only a short preview — because doc 11 P8 "
                + "records that a customer may paste bank account details into it, and a list is the "
                + "payload most likely to be cached or screenshotted. `newCount` counts the whole inbox "
                + "rather than the filter, because it is the badge's number.")
            .Produces<AdminContactSubmissionsDto>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden);

        enquiries.MapGet("/{submissionId:int}", GetEnquiryAsync)
            .RequirePermission(Permissions.Customers.View)
            .WithName("AdminGetContactEnquiry")
            .WithSummary("One enquiry, with its message.")
            .WithDescription(
                "Reading one does **not** mark it Read: that is POST /{id}/read, because a GET that "
                + "changes a row is a row a link checker, a prefetch or a browser's back button can "
                + "change. The screen sends the two together.")
            .Produces<AdminContactSubmissionDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // `Customers.View`, not `Customers.Edit`, and the difference is a defect this fixes.
        //
        // Doc 11 P15 makes *reading* the enquiry the thing that marks it Read, so the permission to read
        // one has to be enough to record that it was read. With `Customers.Edit` here, `Order Staff` —
        // which holds `Customers.View` and not `Customers.Edit` (doc 08 §9) — could open every enquiry
        // and never clear one: the screen fires this as it opens, the answer was 403, and the screen
        // swallowed it deliberately ("they can read the message, which is what they came for"). So the
        // New badge counted the same messages for ever, nothing on the screen said why, and the one
        // role most likely to be answering enquiries was the one it happened to. Found by the W2-11
        // tester.
        //
        // Replied and Spam stay behind `Customers.Edit` below, which is the line that matters: those
        // are claims a staff member makes about what they did, and they are stamped with who and when.
        // "I opened it" is not a claim of that kind.
        enquiries.MapPost("/{submissionId:int}/read", MarkEnquiryReadAsync)
            .RequirePermission(Permissions.Customers.View)
            .WithName("AdminMarkContactEnquiryRead")
            .WithSummary("Mark a New enquiry as Read (doc 11 P15 step 7).")
            .WithDescription(
                "Doc 11 P15 makes opening the enquiry the thing that marks it Read, so the screen calls "
                + "this as it opens one, and it needs only `Customers.View` for that reason — a role "
                + "that may read an enquiry must be able to clear its New badge. It is a no-op for an "
                + "enquiry that is already Read, Replied or Spam — opening a Spam enquiry to check it "
                + "really was spam must not reclassify it, and opening a Replied one must not take the "
                + "reply off it.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        enquiries.MapPut("/{submissionId:int}/status", SetEnquiryStatusAsync)
            .RequirePermission(Permissions.Customers.Edit)
            .WithName("AdminSetContactEnquiryStatus")
            .WithSummary("Mark an enquiry Replied or Spam (doc 11 P15 step 7).")
            .WithDescription(
                "Records who moved it and when. **Replying happens outside the system** — by email or "
                + "telephone (doc 11 P15) — so there is no reply body here and the API never sends one.")
            .Accepts<SetContactEnquiryStatusRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        enquiries.MapPost("/{submissionId:int}/delete", DeleteEnquiryAsync)
            .RequirePermission(Permissions.Customers.Delete)
            .WithName("AdminDeleteContactEnquiry")
            .WithSummary("Delete an enquiry for good (doc 08 §3.7).")
            .WithDescription(
                "The case this exists for is a customer having pasted card or bank account details into "
                + "the message: doc 08 §3.7 says it “must be removed rather than merely hidden”, so this "
                + "is a hard delete with no soft-delete flag to restore from. A reason is required and is "
                + "audited; the audit row holds **no copy of the message body**. POST rather than DELETE "
                + "because the reason travels in a body, and a DELETE with a body is not reliably "
                + "forwarded by proxies.")
            .Accepts<DeleteContactEnquiryRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GetPagesAsync(
        IAdminContentReader reader,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await reader.GetPagesAsync(cancellationToken));

    private static async Task<IResult> GetPageAsync(
        int pageId,
        IAdminContentReader reader,
        CancellationToken cancellationToken)
    {
        var page = await reader.GetPageAsync(pageId, cancellationToken);

        return page is null
            ? NotFound(string.Create(CultureInfo.InvariantCulture, $"No content page has the id {pageId}."))
            : TypedResults.Ok(page);
    }

    private static async Task<IResult> CreatePageAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<CreateContentPageCommand, ContentWriteResult> handler,
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

        if (ReadFields(body!, out var fields) is { } badField)
        {
            return badField;
        }

        var result = await handler.HandleAsync(
            new CreateContentPageCommand(
                fields.Title.Value,
                fields.Slug.Value,
                fields.BodyHtml.Value,
                fields.SeoTitle.Value,
                fields.SeoDescription.Value,
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(
            result,
            (id, removed) => TypedResults.Created(
                string.Create(CultureInfo.InvariantCulture, $"/api/admin/content/pages/{id}"),
                new ContentPageSavedResponse(id, removed)));
    }

    /// <summary>
    /// Edits a page.
    /// <para>
    /// <b>Presence, not just value</b>, which is the whole reason this reads a <see cref="JsonObject"/>
    /// rather than binding a record: a missing property and an explicit <c>null</c> both bind to null, and
    /// "leave this alone" and "clear it" are different instructions. Collapsing them is how W1-12 cleared
    /// every category image, and how W1-03's mapper cleared four of five collections.
    /// </para>
    /// </summary>
    private static async Task<IResult> UpdatePageAsync(
        int pageId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateContentPageCommand, ContentWriteResult> handler,
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

        if (ReadFields(body!, out var fields) is { } badField)
        {
            return badField;
        }

        if (ReadOptionalString(body!, "rowVersion", out var rowVersion, out _) is { } badVersion)
        {
            return badVersion;
        }

        var result = await handler.HandleAsync(
            new UpdateContentPageCommand(
                pageId,
                fields.Title,
                fields.Slug,
                fields.BodyHtml,
                fields.SeoTitle,
                fields.SeoDescription,
                AdminWriteRequest.RowVersion(httpContext.Request, rowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, (id, removed) => TypedResults.Ok(new ContentPageSavedResponse(id, removed)));
    }

    private static async Task<IResult> SetPagePublishedAsync(
        int pageId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<SetContentPagePublishedCommand, ContentWriteResult> handler,
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

        if (ReadBoolean(body!, "isPublished", out var isPublished) is { } badField)
        {
            return badField;
        }

        if (isPublished is null)
        {
            return ValidationProblem(
                [new ValidationError("isPublished", "Say whether the page should be on the shop or off it.")]);
        }

        if (ReadOptionalString(body!, "rowVersion", out var rowVersion, out _) is { } badVersion)
        {
            return badVersion;
        }

        var result = await handler.HandleAsync(
            new SetContentPagePublishedCommand(
                pageId,
                isPublished.Value,
                AdminWriteRequest.RowVersion(httpContext.Request, rowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, (_, _) => TypedResults.NoContent());
    }

    private static async Task<IResult> DeletePageAsync(
        int pageId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<DeleteContentPageCommand, ContentWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var result = await handler.HandleAsync(
            new DeleteContentPageCommand(
                pageId,
                AdminWriteRequest.RowVersion(httpContext.Request, null),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, (_, _) => TypedResults.NoContent());
    }

    private static async Task<IResult> GetEnquiriesAsync(
        HttpContext httpContext,
        IQueryHandler<GetContactSubmissionsQuery, AdminContactSubmissionsDto> handler,
        CancellationToken cancellationToken)
    {
        var query = httpContext.Request.Query;

        ContactSubmissionStatus? status = null;
        if (query["status"].ToString() is { Length: > 0 } wanted)
        {
            if (!Enum.TryParse(wanted, ignoreCase: true, out ContactSubmissionStatus parsed))
            {
                return ValidationProblem(
                [
                    new ValidationError(
                        "status",
                        "Use one of New, Read, Replied or Spam, or leave it out for all of them."),
                ]);
            }

            status = parsed;
        }

        var skip = Math.Max(0, ReadInt(query["skip"], 0));

        // Clamped rather than refused: a page size is a screen's business, and a 400 on ?take=500 would be
        // a client bug the staff member meets as an error page. The ceiling is what protects the server.
        var take = Math.Clamp(ReadInt(query["take"], DefaultPageSize), 1, MaxPageSize);

        return TypedResults.Ok(
            await handler.HandleAsync(new GetContactSubmissionsQuery(status, skip, take), cancellationToken));
    }

    private static async Task<IResult> GetEnquiryAsync(
        int submissionId,
        IAdminContentReader reader,
        CancellationToken cancellationToken)
    {
        var submission = await reader.GetSubmissionAsync(submissionId, cancellationToken);

        return submission is null
            ? NotFound(string.Create(CultureInfo.InvariantCulture, $"No enquiry has the id {submissionId}."))
            : TypedResults.Ok(submission);
    }

    private static async Task<IResult> MarkEnquiryReadAsync(
        int submissionId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<OpenContactSubmissionCommand, ContactSubmissionWriteOutcome> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var outcome = await handler.HandleAsync(
            new OpenContactSubmissionCommand(submissionId, AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(outcome, submissionId);
    }

    private static async Task<IResult> SetEnquiryStatusAsync(
        int submissionId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<SetContactSubmissionStatusCommand, ContactSubmissionWriteOutcome> handler,
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

        if (ReadOptionalString(body!, "status", out var statusText, out _) is { } badField)
        {
            return badField;
        }

        if (!Enum.TryParse(statusText, ignoreCase: true, out ContactSubmissionStatus status)
            || status == ContactSubmissionStatus.New)
        {
            // New is deliberately not settable. "Unread it" is not one of doc 11 P15's transitions, and the
            // status would then say nobody had looked at an enquiry somebody had.
            return ValidationProblem(
                [new ValidationError("status", "Use one of Read, Replied or Spam.")]);
        }

        var outcome = await handler.HandleAsync(
            new SetContactSubmissionStatusCommand(submissionId, status, AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(outcome, submissionId);
    }

    private static async Task<IResult> DeleteEnquiryAsync(
        int submissionId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<DeleteContactSubmissionCommand, ContactSubmissionWriteOutcome> handler,
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

        if (ReadOptionalString(body!, "reason", out var reason, out _) is { } badField)
        {
            return badField;
        }

        var outcome = await handler.HandleAsync(
            new DeleteContactSubmissionCommand(submissionId, reason, AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return outcome == ContactSubmissionWriteOutcome.ReasonRequired
            ? ValidationProblem(
            [
                new ValidationError(
                    "reason",
                    "Say why this enquiry is being deleted. It is recorded in the audit log, and the "
                    + "message itself is not."),
            ])
            : ToHttp(outcome, submissionId);
    }

    /// <summary>
    /// The five page fields, each read for presence as well as value.
    /// </summary>
    private static ValidationProblem? ReadFields(JsonObject body, out ContentPageFields fields)
    {
        fields = default;

        if (ReadOptionalString(body, "title", out var title, out var titleSent) is { } badTitle)
        {
            return badTitle;
        }

        if (ReadOptionalString(body, "slug", out var slug, out var slugSent) is { } badSlug)
        {
            return badSlug;
        }

        if (ReadOptionalString(body, "bodyHtml", out var bodyHtml, out var bodySent) is { } badBody)
        {
            return badBody;
        }

        if (ReadOptionalString(body, "seoTitle", out var seoTitle, out var seoTitleSent) is { } badSeoTitle)
        {
            return badSeoTitle;
        }

        if (ReadOptionalString(body, "seoDescription", out var seoDescription, out var seoDescriptionSent)
            is { } badSeoDescription)
        {
            return badSeoDescription;
        }

        fields = new ContentPageFields(
            titleSent ? Patched.Set(title) : Patched.Unset<string?>(),
            slugSent ? Patched.Set(slug) : Patched.Unset<string?>(),
            bodySent ? Patched.Set(bodyHtml) : Patched.Unset<string?>(),
            seoTitleSent ? Patched.Set(seoTitle) : Patched.Unset<string?>(),
            seoDescriptionSent ? Patched.Set(seoDescription) : Patched.Unset<string?>());

        return null;
    }

    /// <summary>
    /// Reads one optional string property, telling <i>absent</i> from <i>null</i>.
    /// <para>
    /// <b>A second copy of <c>AdminSettingsEndpoints.ReadString</c>, and a deliberate one.</b> That one is
    /// <c>private</c>, is pinned by <c>AdminSettingsShapeTests</c>, and sits in the file the payments
    /// branch is editing for the <c>Bank.*</c> keys — promoting it to a shared helper in this task would
    /// put a conflict in the middle of somebody else's work for six lines. The reasoning both copies rest
    /// on is stated in both: a value of the wrong JSON type becomes a named field error rather than
    /// "malformed JSON", which is issue #47's family, and presence is read rather than inferred, which is
    /// W1-12's blocker.
    /// </para>
    /// </summary>
    private static ValidationProblem? ReadOptionalString(
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
            return ValidationProblem(
                [new ValidationError(field, "Send this field as text, or as null to clear it.")]);
        }

        value = node.GetValue<string>();

        return null;
    }

    /// <inheritdoc cref="ReadOptionalString"/>
    private static ValidationProblem? ReadBoolean(JsonObject body, string field, out bool? value)
    {
        value = null;

        if (!body.TryGetPropertyValue(field, out var node) || node is null)
        {
            return null;
        }

        var kind = node.GetValueKind();
        if (kind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return ValidationProblem([new ValidationError(field, "Send this field as true or false.")]);
        }

        value = kind == JsonValueKind.True;

        return null;
    }

    private static int ReadInt(string? raw, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    /// <summary>
    /// The one mapping from a page write's answer to HTTP, shared by all four routes so a fifth cannot
    /// invent a different status for the same situation.
    /// </summary>
    private static IResult ToHttp(
        ContentWriteResult result,
        Func<int, IReadOnlyList<string>, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);

        return result switch
        {
            ContentWriteResult.Succeeded success => onSuccess(success.EntityId, success.RemovedFromBody),

            // The caller asked for a state the row is already in. Nothing was written and no audit row
            // exists, and the answer is still success: the request's intent holds.
            ContentWriteResult.Unchanged unchanged => onSuccess(unchanged.EntityId, []),

            ContentWriteResult.Invalid invalid => ValidationProblem(invalid.Errors),

            ContentWriteResult.NotFound missing => NotFound(missing.Message),

            ContentWriteResult.Conflict conflict => AdminWriteRequest.Problem(
                StatusCodes.Status409Conflict,
                "Cannot be applied",
                conflict.Message,
                conflict.Code) is var problem && conflict.Details is { Count: > 0 } details
                ? TypedResults.Problem(
                    title: "Cannot be applied",
                    detail: conflict.Message,
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: Extensions(conflict.Code, details))
                : problem,

            _ => throw new InvalidOperationException($"Unhandled content write result '{result}'."),
        };
    }

    /// <summary>The one mapping from an enquiry write's answer to HTTP.</summary>
    private static IResult ToHttp(ContactSubmissionWriteOutcome outcome, int submissionId) => outcome switch
    {
        // Unchanged is a 204 as well: the caller asked for the status it is already in, and the request's
        // intent holds. Nothing was written and no audit row exists.
        ContactSubmissionWriteOutcome.Done or ContactSubmissionWriteOutcome.Unchanged =>
            TypedResults.NoContent(),

        ContactSubmissionWriteOutcome.NotFound => NotFound(string.Create(
            CultureInfo.InvariantCulture,
            $"No enquiry has the id {submissionId}.")),

        // Handled by the caller, which can name the field. Reaching here is a programming error.
        _ => throw new InvalidOperationException($"Unhandled enquiry write outcome '{outcome}'."),
    };

    private static Dictionary<string, object?> Extensions(
        string code,
        IReadOnlyDictionary<string, object?> details)
    {
        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = code };

        foreach (var (key, value) in details)
        {
            extensions[key] = value;
        }

        return extensions;
    }

    private static ValidationProblem ValidationProblem(IReadOnlyList<ValidationError> errors) =>
        TypedResults.ValidationProblem(
            errors
                .GroupBy(error => error.Field, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.Message).Distinct(StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal),
            title: "One or more fields are invalid.");

    private static ProblemHttpResult NotFound(string detail) =>
        AdminWriteRequest.Problem(StatusCodes.Status404NotFound, "Not found", detail, "not_found");

    /// <summary>The five page fields a create or an edit may carry, each with its presence.</summary>
    private readonly record struct ContentPageFields(
        Patched<string?> Title,
        Patched<string?> Slug,
        Patched<string?> BodyHtml,
        Patched<string?> SeoTitle,
        Patched<string?> SeoDescription);
}

/*
 * The records below are the declared contract for the bodies these routes accept: they are what the OpenAPI
 * document shows a client (`.Accepts<T>`) and what `AdminContentShapeTests` pins against the commands.
 *
 * **They are deliberately not what the endpoints bind.** The page routes read a `JsonObject`, because
 * telling an *absent* field from a `null` one is the difference between "leave this alone" and "clear it"
 * — and collapsing the two is W1-12's Blocker shape. Binding a record cannot express that: a missing
 * property and an explicit null both arrive as null.
 */

/// <summary>
/// A content page's editable fields (ADM-17, GLB-10).
/// <para>
/// <b>On a create, <c>title</c> and <c>slug</c> are required.</b> On an edit, <b>every field is
/// optional</b>: one the body omits is left exactly as it is, and one it carries as <c>null</c> is
/// cleared. Those are two different instructions, and a screen may send only what it changed.
/// </para>
/// </summary>
/// <param name="Title">The heading. Cannot be cleared.</param>
/// <param name="Slug">The storefront address. Cannot be cleared.</param>
/// <param name="BodyHtml">
/// The copy, as HTML. <b>Cleaned with the allow-list sanitiser on save</b> (doc 08 §3.7): scripts, event
/// handlers, <c>javascript:</c> and <c>data:</c> URLs, <c>&lt;iframe&gt;</c>, <c>&lt;style&gt;</c> and
/// every attribute outside the allow-list are removed, and the response says what went. Send the empty
/// string to empty the page.
/// </param>
/// <param name="SeoTitle">The <c>&lt;title&gt;</c> override. Null clears it.</param>
/// <param name="SeoDescription">The meta description. Null clears it.</param>
/// <param name="RowVersion">
/// The row's token from the read model, on an edit. May travel in <c>If-Match</c> instead; see
/// <c>AdminWriteRequest.RowVersion</c>.
/// </param>
public sealed record AdminContentPageRequest(
    string? Title,
    string? Slug,
    string? BodyHtml,
    string? SeoTitle,
    string? SeoDescription,
    string? RowVersion);

/// <summary>Put a page on the shop or take it off (ADM-17, GLB-10).</summary>
/// <param name="IsPublished">Where it should end up. Required.</param>
/// <param name="RowVersion">The row's token.</param>
public sealed record SetContentPagePublishedRequest(bool? IsPublished, string? RowVersion);

/// <summary>
/// What a page save answers with.
/// </summary>
/// <param name="Id">The page's id.</param>
/// <param name="RemovedFromBody">
/// What the sanitiser took out of the body, as short descriptions — <c>&lt;script&gt;</c>, <c>onerror</c>.
/// <b>Shown to the editor even though the save succeeded</b>: a save that silently dropped half a paste is
/// the defect shape this project has shipped three times. Empty when the body was already clean, and
/// always empty when the request did not send a body.
/// </param>
public sealed record ContentPageSavedResponse(int Id, IReadOnlyList<string> RemovedFromBody);

/// <summary>Move an enquiry's status (GLB-10, doc 11 P15 step 7).</summary>
/// <param name="Status"><c>Read</c>, <c>Replied</c> or <c>Spam</c>. <c>New</c> is not settable.</param>
public sealed record SetContactEnquiryStatusRequest(string? Status);

/// <summary>Delete an enquiry for good (doc 08 §3.7).</summary>
/// <param name="Reason">
/// Why, in the staff member's own words. Required, and audited. The message body is not audited.
/// </param>
public sealed record DeleteContactEnquiryRequest(string? Reason);
