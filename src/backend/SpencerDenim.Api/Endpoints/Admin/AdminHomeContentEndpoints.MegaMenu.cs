using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Content.Admin;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Application.Content.Admin.MegaMenu;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// Doc 11 P13 part D: the mega-menu cards (GLB-05, ADM-17; #215). The same arrangement as the rest of this
/// class — <c>Content.View/Create/Edit/Delete</c> one per route, antiforgery on every write, every field
/// required in the body, the row version in the body or <c>If-Match</c> — and the same plumbing, which is why
/// it is a part of this class rather than a class of its own.
/// </summary>
internal static partial class AdminHomeContentEndpoints
{
    private static void MapMegaMenuCards(IEndpointRouteBuilder endpoints)
    {
        var menu = endpoints.MapGroup("/api/admin/content/mega-menu").WithTags("Admin Mega Menu");

        menu.MapGet("/", async (IMegaMenuCardAdminReader reader, IClock clock, CancellationToken cancellationToken) =>
                TypedResults.Ok(await reader.GetAsync(clock.UtcNow.UtcDateTime, cancellationToken)))
            .RequirePermission(Permissions.Content.View)
            .WithName("AdminGetMegaMenuCards")
            .WithSummary("Every department with its mega-menu cards, switched on or not.")
            .WithDescription(
                "GLB-05, doc 11 P13 step 10. Departments in menu order, each with what the menu shows when no card "
                + "is on (its description and picture), and every card with its schedule as **Colombo** wall-clock "
                + "(`yyyy-MM-ddTHH:mm`), `isLiveNow`, `isShownNow` (the one card the shop draws) and `rowVersion`.")
            .Produces<AdminMegaMenuContentDto>()
            .Produces(StatusCodes.Status403Forbidden);

        menu.MapPost("/cards", CreateMegaMenuCardAsync)
            .RequirePermission(Permissions.Content.Create)
            .WithName("AdminCreateMegaMenuCard")
            .WithSummary("Add a card to a department's mega menu.")
            .WithDescription(
                "Send every field: `departmentId`, `title` (≤ 100), `subtitle` (≤ 200, optional), `buttonLabel` "
                + "(≤ 40), `linkUrl` (a path on this site or a full http(s) address), `imagePublicId` (a "
                + "media-library image with alt text; required to switch the card on), `sortOrder` (0–999), "
                + "`isActive`, `startsAt`, `endsAt`. 409 `too_many_mega_menu_cards` beyond six per department.")
            .Produces<MegaMenuCardSavedDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        menu.MapPut("/cards/{cardId:int}", UpdateMegaMenuCardAsync)
            .RequirePermission(Permissions.Content.Edit)
            .WithName("AdminUpdateMegaMenuCard")
            .WithSummary("Edit a mega-menu card. Send every field and the row version.")
            .Produces<MegaMenuCardSavedDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        menu.MapDelete("/cards/{cardId:int}", DeleteMegaMenuCardAsync)
            .RequirePermission(Permissions.Content.Delete)
            .WithName("AdminDeleteMegaMenuCard")
            .WithSummary("Remove a mega-menu card. Send its row version in If-Match.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static Task<IResult> CreateMegaMenuCardAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<CreateMegaMenuCardCommand, ContentWriteResult> handler,
        CancellationToken cancellationToken) =>
        WriteAsync(httpContext, antiforgery, async body =>
        {
            if (ReadMegaMenuCardFields(body, out var fields) is { } bad)
            {
                return bad;
            }

            var result = await handler.HandleAsync(
                new CreateMegaMenuCardCommand(fields!, AdminWriteRequest.Actor(httpContext)), cancellationToken);

            return ToHttp(result, saved => TypedResults.Created(
                string.Create(CultureInfo.InvariantCulture, $"/api/admin/content/mega-menu/cards/{saved.EntityId}"),
                new MegaMenuCardSavedDto(saved.EntityId, saved.RowVersion)));
        }, cancellationToken);

    private static Task<IResult> UpdateMegaMenuCardAsync(
        int cardId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateMegaMenuCardCommand, ContentWriteResult> handler,
        CancellationToken cancellationToken) =>
        WriteAsync(httpContext, antiforgery, async body =>
        {
            if (ReadMegaMenuCardFields(body, out var fields) is { } bad)
            {
                return bad;
            }

            var result = await handler.HandleAsync(
                new UpdateMegaMenuCardCommand(
                    cardId,
                    fields!,
                    AdminWriteRequest.RowVersion(httpContext.Request, AdminJsonBody.RowVersionIn(body)),
                    AdminWriteRequest.Actor(httpContext)),
                cancellationToken);

            return ToHttp(result, saved => TypedResults.Ok(new MegaMenuCardSavedDto(saved.EntityId, saved.RowVersion)));
        }, cancellationToken);

    private static async Task<IResult> DeleteMegaMenuCardAsync(
        int cardId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<DeleteMegaMenuCardCommand, ContentWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var result = await handler.HandleAsync(
            new DeleteMegaMenuCardCommand(
                cardId,
                AdminWriteRequest.RowVersion(httpContext.Request, null),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, _ => TypedResults.NoContent());
    }

    private static ValidationProblem? ReadMegaMenuCardFields(JsonObject body, out MegaMenuCardFields? fields)
    {
        fields = null;

        if (AdminJsonBody.ReadInt(body, "departmentId", out var departmentId) is { } e1) return e1;
        if (AdminJsonBody.ReadString(body, "title", out var title) is { } e2) return e2;
        if (AdminJsonBody.ReadString(body, "subtitle", out var subtitle) is { } e3) return e3;
        if (AdminJsonBody.ReadString(body, "buttonLabel", out var buttonLabel) is { } e4) return e4;
        if (AdminJsonBody.ReadString(body, "linkUrl", out var linkUrl) is { } e5) return e5;
        if (ReadGuid(body, "imagePublicId", out var image) is { } e6) return e6;
        if (AdminJsonBody.ReadInt(body, "sortOrder", out var sortOrder) is { } e7) return e7;
        if (RequiredBool(body, "isActive", out var isActive) is { } e8) return e8;
        if (AdminJsonBody.ReadLocalDateTime(body, "startsAt", out var startsAt) is { } e9) return e9;
        if (AdminJsonBody.ReadLocalDateTime(body, "endsAt", out var endsAt) is { } e10) return e10;

        fields = new MegaMenuCardFields(
            departmentId,
            title,
            subtitle,
            buttonLabel,
            linkUrl,
            image,
            sortOrder,
            isActive,
            startsAt,
            endsAt);

        return null;
    }
}
