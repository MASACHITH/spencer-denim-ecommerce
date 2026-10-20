using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Content;
using SpencerDenim.Application.Content.Admin;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Application.Content.Admin.Announcements;
using SpencerDenim.Application.Content.Admin.Home;
using SpencerDenim.Application.Content.Admin.SizeGuides;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The rest of the ADM-17 content screens (#192; doc 11 P13 parts B, C and E): the homepage blocks, the
/// announcement strip and the size guides. Content pages are <see cref="AdminContentEndpoints"/>.
/// <para>
/// <b>Content.View/Create/Edit/Delete, one per route</b>, which is doc 11 P13's own table — "Homepage
/// blocks, announcement bar, mega menu cards, size guides, content pages: Content.View/Create/Edit/Delete"
/// — enforced before the handler runs and without reference to anything in the body, so no answer here
/// depends on what a submitted value would have changed (no one-bit oracle).
/// </para>
/// <para>
/// <b>Every mutating route requires every field to be present</b> (<see cref="AdminJsonBody"/>): a missing
/// key is a 400 naming it, and null clears. Collapsing the two is how W1-12 cleared every category image.
/// Antiforgery is validated by hand on every mutating route, as everywhere else in the admin API.
/// </para>
/// <para>
/// <b>Every write evicts the homepage cache</b> — doc 08 §3.7's "evicted on save" — inside its handler,
/// after the save, so the next visitor sees the change rather than a five-minute-old page.
/// </para>
/// </summary>
internal static class AdminHomeContentEndpoints
{
    /// <summary>
    /// The largest body a size-guide save may send (#192 tester D4). Without it the whole body was parsed
    /// into a <c>JsonObject</c> before <c>SizeGuideTableInput</c>'s 128 KiB check could refuse the table.
    /// 512 KiB: the 128 KiB table, which travels as an escaped JSON <i>string</i> (every quote doubles), plus
    /// the intro and measuring-tips HTML before sanitising, plus the envelope — well above any real guide and
    /// far below what costs anything to refuse. Enforced twice: a declared <c>Content-Length</c> over it is a
    /// 413 before a byte is read, and <see cref="RequestSizeLimitAttribute"/> stops a body that does not
    /// declare one (chunked) as it streams.
    /// </summary>
    public const long MaxSizeGuideBodyBytes = 512 * 1024;

    public static IEndpointRouteBuilder MapAdminHomeContentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapHome(endpoints);
        MapAnnouncements(endpoints);
        MapSizeGuides(endpoints);

        return endpoints;
    }

    // ================================================================== homepage

    private static void MapHome(IEndpointRouteBuilder endpoints)
    {
        var home = endpoints.MapGroup("/api/admin/content/home").WithTags("Admin Homepage");

        home.MapGet("/", async (IHomeContentAdminReader reader, IClock clock, CancellationToken cancellationToken) =>
                TypedResults.Ok(await reader.GetAsync(clock.UtcNow.UtcDateTime, cancellationToken)))
            .RequirePermission(Permissions.Content.View)
            .WithName("AdminGetHomeContent")
            .WithSummary("Every homepage block and item, switched on or not.")
            .WithDescription(
                "HOME-01..10, doc 11 P13 part B. All ten blocks in page order, each with every item, its "
                + "schedule as **Colombo** wall-clock (`yyyy-MM-ddTHH:mm`), `isLiveNow`, and `rules` — which "
                + "section and item fields the block uses, which are required and how many items it holds. A "
                + "field outside `rules` must be sent as null. Each block carries the `rowVersion` that its "
                + "item writes send as well.")
            .Produces<AdminHomeContentDto>()
            .Produces(StatusCodes.Status403Forbidden);

        home.MapPut("/sections/{sectionId:int}", UpdateSectionAsync)
            .RequirePermission(Permissions.Content.Edit)
            .WithName("AdminUpdateHomeSection")
            .WithSummary("Edit a homepage block: heading, switch, position, schedule, settings.")
            .WithDescription(
                "Doc 11 P13 step 6. A full replacement — send every field: `title`, `subtitle`, `isEnabled`, "
                + "`sortOrder`, `startsAt`, `endsAt`, `mode`, `count`, `listingUrl`, `linkLabel`, `overlay`, "
                + "`rowVersion`. Fields the block does not use (see `rules.sectionFields`) must be null. "
                + "`listingUrl` is a path on this site the catalogue can answer. The homepage cache is evicted "
                + "on save.")
            .Produces<HomeContentSavedDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        home.MapPost("/sections/{sectionId:int}/items", CreateItemAsync)
            .RequirePermission(Permissions.Content.Create)
            .WithName("AdminCreateHomeSectionItem")
            .WithSummary("Add an item to a homepage block.")
            .WithDescription(
                "Doc 11 P13 step 7. Send every field; `rowVersion` is the **block's**. Images and video are "
                + "media-library public ids (upload first). A video needs a still. 409 `items_not_supported` "
                + "for a block with no items, `section_full` beyond `rules.maxItems`.")
            .Produces<HomeContentSavedDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        home.MapPut("/items/{itemId:int}", UpdateItemAsync)
            .RequirePermission(Permissions.Content.Edit)
            .WithName("AdminUpdateHomeSectionItem")
            .WithSummary("Edit a homepage item.")
            .Produces<HomeContentSavedDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        home.MapDelete("/items/{itemId:int}", DeleteItemAsync)
            .RequirePermission(Permissions.Content.Delete)
            .WithName("AdminDeleteHomeSectionItem")
            .WithSummary("Remove a homepage item. Send the block's row version in If-Match.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static Task<IResult> UpdateSectionAsync(
        int sectionId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateHomeSectionCommand, ContentWriteResult> handler,
        CancellationToken cancellationToken) =>
        WriteAsync(httpContext, antiforgery, async body =>
        {
            if (AdminJsonBody.ReadString(body, "title", out var title) is { } e1) return e1;
            if (AdminJsonBody.ReadString(body, "subtitle", out var subtitle) is { } e2) return e2;
            if (RequiredBool(body, "isEnabled", out var isEnabled) is { } e3) return e3;
            if (AdminJsonBody.ReadInt(body, "sortOrder", out var sortOrder) is { } e4) return e4;
            if (AdminJsonBody.ReadLocalDateTime(body, "startsAt", out var startsAt) is { } e5) return e5;
            if (AdminJsonBody.ReadLocalDateTime(body, "endsAt", out var endsAt) is { } e6) return e6;
            if (AdminJsonBody.ReadEnum<HomeSectionMode>(body, "mode", "Auto or Manual", out var mode) is { } e7) return e7;
            if (AdminJsonBody.ReadInt(body, "count", out var count) is { } e8) return e8;
            if (AdminJsonBody.ReadString(body, "listingUrl", out var listingUrl) is { } e9) return e9;
            if (AdminJsonBody.ReadString(body, "linkLabel", out var linkLabel) is { } e10) return e10;
            if (AdminJsonBody.ReadString(body, "overlay", out var overlay) is { } e11) return e11;

            var result = await handler.HandleAsync(
                new UpdateHomeSectionCommand(
                    sectionId,
                    title,
                    subtitle,
                    isEnabled,
                    sortOrder,
                    startsAt,
                    endsAt,
                    mode,
                    count,
                    listingUrl,
                    linkLabel,
                    overlay,
                    AdminWriteRequest.RowVersion(httpContext.Request, AdminJsonBody.RowVersionIn(body)),
                    AdminWriteRequest.Actor(httpContext)),
                cancellationToken);

            return ToHttp(result, saved => TypedResults.Ok(new HomeContentSavedDto(saved.EntityId, saved.RowVersion)));
        }, cancellationToken);

    private static Task<IResult> CreateItemAsync(
        int sectionId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<CreateHomeSectionItemCommand, ContentWriteResult> handler,
        CancellationToken cancellationToken) =>
        WriteAsync(httpContext, antiforgery, async body =>
        {
            if (ReadItemFields(body, out var fields) is { } bad)
            {
                return bad;
            }

            var result = await handler.HandleAsync(
                new CreateHomeSectionItemCommand(
                    sectionId,
                    fields!,
                    AdminWriteRequest.RowVersion(httpContext.Request, AdminJsonBody.RowVersionIn(body)),
                    AdminWriteRequest.Actor(httpContext)),
                cancellationToken);

            return ToHttp(result, saved => TypedResults.Created(
                string.Create(CultureInfo.InvariantCulture, $"/api/admin/content/home/items/{saved.EntityId}"),
                new HomeContentSavedDto(saved.EntityId, saved.RowVersion)));
        }, cancellationToken);

    private static Task<IResult> UpdateItemAsync(
        int itemId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateHomeSectionItemCommand, ContentWriteResult> handler,
        CancellationToken cancellationToken) =>
        WriteAsync(httpContext, antiforgery, async body =>
        {
            if (ReadItemFields(body, out var fields) is { } bad)
            {
                return bad;
            }

            var result = await handler.HandleAsync(
                new UpdateHomeSectionItemCommand(
                    itemId,
                    fields!,
                    AdminWriteRequest.RowVersion(httpContext.Request, AdminJsonBody.RowVersionIn(body)),
                    AdminWriteRequest.Actor(httpContext)),
                cancellationToken);

            return ToHttp(result, saved => TypedResults.Ok(new HomeContentSavedDto(saved.EntityId, saved.RowVersion)));
        }, cancellationToken);

    private static async Task<IResult> DeleteItemAsync(
        int itemId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<DeleteHomeSectionItemCommand, ContentWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var result = await handler.HandleAsync(
            new DeleteHomeSectionItemCommand(
                itemId,
                AdminWriteRequest.RowVersion(httpContext.Request, null),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, _ => TypedResults.NoContent());
    }

    private static ValidationProblem? ReadItemFields(JsonObject body, out HomeSectionItemFields? fields)
    {
        fields = null;

        if (AdminJsonBody.ReadString(body, "title", out var title) is { } e1) return e1;
        if (AdminJsonBody.ReadString(body, "subtitle", out var subtitle) is { } e2) return e2;
        if (AdminJsonBody.ReadString(body, "body", out var text) is { } e3) return e3;
        if (AdminJsonBody.ReadString(body, "buttonLabel", out var buttonLabel) is { } e4) return e4;
        if (AdminJsonBody.ReadString(body, "linkUrl", out var linkUrl) is { } e5) return e5;
        if (ReadGuid(body, "imagePublicId", out var image) is { } e6) return e6;
        if (ReadGuid(body, "mobileImagePublicId", out var mobileImage) is { } e7) return e7;
        if (ReadGuid(body, "videoPublicId", out var video) is { } e8) return e8;
        if (AdminJsonBody.ReadInt(body, "departmentId", out var departmentId) is { } e9) return e9;
        if (AdminJsonBody.ReadString(body, "productSlug", out var productSlug) is { } e10) return e10;
        if (AdminJsonBody.ReadString(body, "iconKey", out var iconKey) is { } e11) return e11;
        if (AdminJsonBody.ReadInt(body, "sortOrder", out var sortOrder) is { } e12) return e12;
        if (RequiredBool(body, "isEnabled", out var isEnabled) is { } e13) return e13;
        if (AdminJsonBody.ReadLocalDateTime(body, "startsAt", out var startsAt) is { } e14) return e14;
        if (AdminJsonBody.ReadLocalDateTime(body, "endsAt", out var endsAt) is { } e15) return e15;

        fields = new HomeSectionItemFields(
            title,
            subtitle,
            text,
            buttonLabel,
            linkUrl,
            image,
            mobileImage,
            video,
            departmentId,
            productSlug,
            iconKey,
            sortOrder,
            isEnabled,
            startsAt,
            endsAt);

        return null;
    }

    // ================================================================== announcements

    private static void MapAnnouncements(IEndpointRouteBuilder endpoints)
    {
        var announcements = endpoints.MapGroup("/api/admin/content/announcements").WithTags("Admin Announcements");

        announcements.MapGet("/", async (IAnnouncementAdminReader reader, IClock clock, CancellationToken cancellationToken) =>
                TypedResults.Ok(await reader.ListAsync(clock.UtcNow.UtcDateTime, cancellationToken)))
            .RequirePermission(Permissions.Content.View)
            .WithName("AdminListAnnouncements")
            .WithSummary("Every announcement-strip message, on or off.")
            .WithDescription(
                "GLB-03, doc 11 P13 step 9. In strip order, with `isLiveNow` and the `version` to send back. "
                + "Schedules are Colombo wall-clock.")
            .Produces<IReadOnlyList<AdminAnnouncementDto>>()
            .Produces(StatusCodes.Status403Forbidden);

        announcements.MapPost("/", CreateAnnouncementAsync)
            .RequirePermission(Permissions.Content.Create)
            .WithName("AdminCreateAnnouncement")
            .WithSummary("Add a message to the announcement strip.")
            .WithDescription(
                "Send `message` (plain text, up to 200 characters, one line), `linkUrl`, `sortOrder`, `isActive`, "
                + "`startsAt`, `endsAt`. 409 `too_many_announcements` beyond twenty.")
            .Produces<AnnouncementSavedDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        announcements.MapPut("/{messageId:int}", UpdateAnnouncementAsync)
            .RequirePermission(Permissions.Content.Edit)
            .WithName("AdminUpdateAnnouncement")
            .WithSummary("Edit an announcement. Send every field and the `version` it was read at.")
            .Produces<AnnouncementSavedDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        announcements.MapDelete("/{messageId:int}", DeleteAnnouncementAsync)
            .RequirePermission(Permissions.Content.Delete)
            .WithName("AdminDeleteAnnouncement")
            .WithSummary("Remove an announcement. Send its version in If-Match.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static Task<IResult> CreateAnnouncementAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<CreateAnnouncementCommand, ContentWriteResult> handler,
        CancellationToken cancellationToken) =>
        WriteAsync(httpContext, antiforgery, async body =>
        {
            if (ReadAnnouncementFields(body, out var fields) is { } bad)
            {
                return bad;
            }

            var result = await handler.HandleAsync(
                new CreateAnnouncementCommand(fields!, AdminWriteRequest.Actor(httpContext)), cancellationToken);

            return ToHttp(result, saved => TypedResults.Created(
                string.Create(CultureInfo.InvariantCulture, $"/api/admin/content/announcements/{saved.EntityId}"),
                new AnnouncementSavedDto(saved.EntityId, saved.RowVersion)));
        }, cancellationToken);

    private static Task<IResult> UpdateAnnouncementAsync(
        int messageId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateAnnouncementCommand, ContentWriteResult> handler,
        CancellationToken cancellationToken) =>
        WriteAsync(httpContext, antiforgery, async body =>
        {
            if (ReadAnnouncementFields(body, out var fields) is { } bad)
            {
                return bad;
            }

            var version = body["version"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

            var result = await handler.HandleAsync(
                new UpdateAnnouncementCommand(
                    messageId,
                    fields!,
                    AdminWriteRequest.RowVersion(httpContext.Request, version),
                    AdminWriteRequest.Actor(httpContext)),
                cancellationToken);

            return ToHttp(result, saved => TypedResults.Ok(new AnnouncementSavedDto(saved.EntityId, saved.RowVersion)));
        }, cancellationToken);

    private static async Task<IResult> DeleteAnnouncementAsync(
        int messageId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<DeleteAnnouncementCommand, ContentWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var result = await handler.HandleAsync(
            new DeleteAnnouncementCommand(
                messageId,
                AdminWriteRequest.RowVersion(httpContext.Request, null),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, _ => TypedResults.NoContent());
    }

    private static ValidationProblem? ReadAnnouncementFields(JsonObject body, out AnnouncementFields? fields)
    {
        fields = null;

        if (AdminJsonBody.ReadString(body, "message", out var message) is { } e1) return e1;
        if (AdminJsonBody.ReadString(body, "linkUrl", out var linkUrl) is { } e2) return e2;
        if (AdminJsonBody.ReadInt(body, "sortOrder", out var sortOrder) is { } e3) return e3;
        if (RequiredBool(body, "isActive", out var isActive) is { } e4) return e4;
        if (AdminJsonBody.ReadLocalDateTime(body, "startsAt", out var startsAt) is { } e5) return e5;
        if (AdminJsonBody.ReadLocalDateTime(body, "endsAt", out var endsAt) is { } e6) return e6;

        fields = new AnnouncementFields(message, linkUrl, sortOrder, isActive, startsAt, endsAt);
        return null;
    }

    // ================================================================== size guides

    private static void MapSizeGuides(IEndpointRouteBuilder endpoints)
    {
        var guides = endpoints.MapGroup("/api/admin/content/size-guides").WithTags("Admin Size Guides");

        guides.MapGet("/", async (ISizeGuideAdminReader reader, CancellationToken cancellationToken) =>
                TypedResults.Ok(await reader.ListAsync(cancellationToken)))
            .RequirePermission(Permissions.Content.View)
            .WithName("AdminListSizeGuides")
            .WithSummary("Every size guide, with how many categories and products use it.")
            .Produces<IReadOnlyList<AdminSizeGuideListItemDto>>()
            .Produces(StatusCodes.Status403Forbidden);

        guides.MapGet("/{sizeGuideId:int}", GetSizeGuideAsync)
            .RequirePermission(Permissions.Content.View)
            .WithName("AdminGetSizeGuide")
            .WithSummary("One size guide, with its table, its HTML and the limits the editor must hold to.")
            .Produces<AdminSizeGuideDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        guides.MapPost("/", CreateSizeGuideAsync)
            .RequirePermission(Permissions.Content.Create)
            .WithName("AdminCreateSizeGuide")
            .WithSummary("Create a size guide.")
            .WithDescription(
                "PDP-04, doc 11 P13 step 11. Send `name`, `introHtml`, `tableJson` (a **string** holding "
                + "`{\"columns\":[...],\"rows\":[[...]]}`), `measuringTipsHtml`, `imagePublicId`. The table is "
                + "bounded to 20 columns, 100 rows and 1,000 cells, checked before anything is allocated "
                + "(#70); every row must be as wide as the headings. Both HTML fields are cleaned with the "
                + "allow-list sanitiser on save and the response says what was removed.")
            .Produces<SizeGuideSavedDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .WithMetadata(new RequestSizeLimitAttribute(MaxSizeGuideBodyBytes));

        guides.MapPut("/{sizeGuideId:int}", UpdateSizeGuideAsync)
            .RequirePermission(Permissions.Content.Edit)
            .WithName("AdminUpdateSizeGuide")
            .WithSummary("Edit a size guide. Send every field and the row version.")
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .WithMetadata(new RequestSizeLimitAttribute(MaxSizeGuideBodyBytes))
            .Produces<SizeGuideSavedDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        guides.MapDelete("/{sizeGuideId:int}", DeleteSizeGuideAsync)
            .RequirePermission(Permissions.Content.Delete)
            .WithName("AdminDeleteSizeGuide")
            .WithSummary("Remove a size guide nothing uses. Send the row version in If-Match.")
            .WithDescription(
                "A soft delete. Refused with 409 `size_guide_in_use` while any category or product points at "
                + "it, with `categoryCount` and `productCount`.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> GetSizeGuideAsync(
        int sizeGuideId,
        ISizeGuideAdminReader reader,
        CancellationToken cancellationToken) =>
        await reader.GetAsync(sizeGuideId, cancellationToken) is { } guide
            ? TypedResults.Ok(guide)
            : NotFound(string.Create(CultureInfo.InvariantCulture, $"No size guide has the id {sizeGuideId}."));

    private static Task<IResult> CreateSizeGuideAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<CreateSizeGuideCommand, ContentWriteResult> handler,
        CancellationToken cancellationToken) =>
        TooLarge(httpContext) is { } tooLarge
            ? Task.FromResult<IResult>(tooLarge)
            : WriteAsync(httpContext, antiforgery, async body =>
        {
            if (ReadSizeGuideFields(body, out var fields) is { } bad)
            {
                return bad;
            }

            var result = await handler.HandleAsync(
                new CreateSizeGuideCommand(fields!, AdminWriteRequest.Actor(httpContext)), cancellationToken);

            return ToHttp(result, saved => TypedResults.Created(
                string.Create(CultureInfo.InvariantCulture, $"/api/admin/content/size-guides/{saved.EntityId}"),
                SizeGuideSaved(saved)));
        }, cancellationToken);

    private static Task<IResult> UpdateSizeGuideAsync(
        int sizeGuideId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateSizeGuideCommand, ContentWriteResult> handler,
        CancellationToken cancellationToken) =>
        TooLarge(httpContext) is { } tooLarge
            ? Task.FromResult<IResult>(tooLarge)
            : WriteAsync(httpContext, antiforgery, async body =>
        {
            if (ReadSizeGuideFields(body, out var fields) is { } bad)
            {
                return bad;
            }

            var result = await handler.HandleAsync(
                new UpdateSizeGuideCommand(
                    sizeGuideId,
                    fields!,
                    AdminWriteRequest.RowVersion(httpContext.Request, AdminJsonBody.RowVersionIn(body)),
                    AdminWriteRequest.Actor(httpContext)),
                cancellationToken);

            return ToHttp(result, saved => TypedResults.Ok(SizeGuideSaved(saved)));
        }, cancellationToken);

    private static async Task<IResult> DeleteSizeGuideAsync(
        int sizeGuideId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<DeleteSizeGuideCommand, ContentWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var result = await handler.HandleAsync(
            new DeleteSizeGuideCommand(
                sizeGuideId,
                AdminWriteRequest.RowVersion(httpContext.Request, null),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, _ => TypedResults.NoContent());
    }

    private static ValidationProblem? ReadSizeGuideFields(JsonObject body, out SizeGuideFields? fields)
    {
        fields = null;

        if (AdminJsonBody.ReadString(body, "name", out var name) is { } e1) return e1;
        if (AdminJsonBody.ReadString(body, "introHtml", out var introHtml) is { } e2) return e2;
        if (AdminJsonBody.ReadString(body, "tableJson", out var tableJson) is { } e3) return e3;
        if (AdminJsonBody.ReadString(body, "measuringTipsHtml", out var tipsHtml) is { } e4) return e4;
        if (ReadGuid(body, "imagePublicId", out var image) is { } e5) return e5;

        fields = new SizeGuideFields(name, introHtml, tableJson, tipsHtml, image);
        return null;
    }

    private static SizeGuideSavedDto SizeGuideSaved(ContentWriteResult.Succeeded saved) =>
        new(saved.EntityId, saved.RowVersion, saved.RemovedFromBody, saved.RemovedFromSecondary ?? []);

    // ================================================================== plumbing

    /// <summary>A size-guide body whose declared length is over the limit, refused before it is read.</summary>
    private static ProblemHttpResult? TooLarge(HttpContext httpContext) =>
        httpContext.Request.ContentLength > MaxSizeGuideBodyBytes
            ? AdminWriteRequest.Problem(
                StatusCodes.Status413PayloadTooLarge,
                "Request too large",
                "A size guide is at most 20 columns, 100 rows and 20,000 characters of notes; this request is far larger.",
                "too_large")
            : null;

    /// <summary>Antiforgery, then the body as an object, then the route's own work.</summary>
    private static async Task<IResult> WriteAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        Func<JsonObject, Task<IResult>> write,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<JsonObject>(httpContext.Request, cancellationToken);

        return problem ?? await write(body!);
    }

    /// <summary>A flag that must be sent and must not be null — a switch has no "leave it" state.</summary>
    private static ValidationProblem? RequiredBool(JsonObject body, string field, out bool value)
    {
        value = false;

        if (AdminJsonBody.ReadBool(body, field, out var flag) is { } bad)
        {
            return bad;
        }

        if (flag is null)
        {
            return AdminJsonBody.WrongType(field, "true or false");
        }

        value = flag.Value;
        return null;
    }

    /// <summary>A media public id: a GUID string, or null to clear the slot.</summary>
    private static ValidationProblem? ReadGuid(JsonObject body, string field, out Guid? value)
    {
        value = null;

        if (AdminJsonBody.ReadString(body, field, out var text) is { } bad)
        {
            return bad;
        }

        if (text is null)
        {
            return null;
        }

        if (!Guid.TryParse(text, CultureInfo.InvariantCulture, out var parsed))
        {
            return AdminJsonBody.WrongType(field, "a media library id");
        }

        value = parsed;
        return null;
    }

    /// <summary>The one mapping from these writes' answers to HTTP.</summary>
    private static IResult ToHttp(ContentWriteResult result, Func<ContentWriteResult.Succeeded, IResult> onSuccess) =>
        result switch
        {
            ContentWriteResult.Succeeded success => onSuccess(success),

            // Nothing changed and nothing was written; the request's intent holds. The row version is the
            // one the caller already has, which it sent.
            ContentWriteResult.Unchanged unchanged => onSuccess(new ContentWriteResult.Succeeded(unchanged.EntityId, [])),

            ContentWriteResult.Invalid invalid => AdminJsonBody.Problem(invalid.Errors),

            ContentWriteResult.NotFound missing => NotFound(missing.Message),

            ContentWriteResult.Conflict conflict => TypedResults.Problem(
                title: "Cannot be applied",
                detail: conflict.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: Extensions(conflict)),

            _ => throw new InvalidOperationException($"Unhandled content write result '{result}'."),
        };

    private static Dictionary<string, object?> Extensions(ContentWriteResult.Conflict conflict)
    {
        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = conflict.Code };

        foreach (var (key, value) in conflict.Details ?? new Dictionary<string, object?>())
        {
            extensions[key] = value;
        }

        return extensions;
    }

    private static ProblemHttpResult NotFound(string detail) =>
        AdminWriteRequest.Problem(StatusCodes.Status404NotFound, "Not found", detail, "not_found");
}
