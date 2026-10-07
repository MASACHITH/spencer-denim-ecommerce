using SpencerDenim.Domain.Content;

namespace SpencerDenim.Application.Content.Admin.Contracts;

/// <summary>
/// One content page as the admin list shows it (ADM-17, GLB-10). No body: the list of pages does not need
/// six policy documents in it, and the editor fetches one page at a time.
/// </summary>
/// <param name="Id">The row id, which is how the admin addresses it — never the slug, which is editable.</param>
/// <param name="SystemKey">One of the six, or null for a custom page.</param>
/// <param name="Slug">The storefront address.</param>
/// <param name="Title">The heading.</param>
/// <param name="IsPublished">Whether a visitor can read it.</param>
/// <param name="HasBody">
/// Whether the client has written anything yet. The list's whole job on a fresh shop is to say "these six
/// are waiting for your copy", and <c>false</c> here is what the screen draws that from — without it the
/// only difference between a written page and an empty one is opening it.
/// </param>
/// <param name="ImagesMissingAltText">
/// How many images in the body have no alt text (doc 08 §3.3, GLB-12). Above zero means publishing will be
/// refused, so the list can say so before a staff member tries.
/// </param>
/// <param name="PublishedAtUtc">When it was first published.</param>
/// <param name="UpdatedAtUtc">When it last changed.</param>
/// <param name="CanBeDeleted">
/// Doc 11 P13: "System pages cannot be deleted." Sent rather than inferred from
/// <paramref name="SystemKey"/> on the client, so the screen and the API cannot disagree about the rule —
/// <see cref="ContentPage.CanBeDeleted"/> is the one implementation of it.
/// </param>
/// <param name="RowVersion">The concurrency token, round-tripped and never interpreted by the client.</param>
public sealed record AdminContentPageListItemDto(
    int Id,
    string? SystemKey,
    string Slug,
    string Title,
    bool IsPublished,
    bool HasBody,
    int ImagesMissingAltText,
    DateTime? PublishedAtUtc,
    DateTime? UpdatedAtUtc,
    bool CanBeDeleted,
    string RowVersion);

/// <summary>One content page as the editor loads it (ADM-17, GLB-10).</summary>
/// <param name="Id"><inheritdoc cref="AdminContentPageListItemDto.Id" path="/node()"/></param>
/// <param name="SystemKey"><inheritdoc cref="AdminContentPageListItemDto.SystemKey" path="/node()"/></param>
/// <param name="Slug"><inheritdoc cref="AdminContentPageListItemDto.Slug" path="/node()"/></param>
/// <param name="Title"><inheritdoc cref="AdminContentPageListItemDto.Title" path="/node()"/></param>
/// <param name="BodyHtml">
/// The stored, already-sanitised HTML. What the editor loads is what the storefront renders, which is the
/// property that makes a preview honest.
/// </param>
/// <param name="SeoTitle">The <c>&lt;title&gt;</c> override, or null.</param>
/// <param name="SeoDescription">The meta description, or null.</param>
/// <param name="IsPublished"><inheritdoc cref="AdminContentPageListItemDto.IsPublished" path="/node()"/></param>
/// <param name="PublishedAtUtc"><inheritdoc cref="AdminContentPageListItemDto.PublishedAtUtc" path="/node()"/></param>
/// <param name="UpdatedAtUtc"><inheritdoc cref="AdminContentPageListItemDto.UpdatedAtUtc" path="/node()"/></param>
/// <param name="CanBeDeleted"><inheritdoc cref="AdminContentPageListItemDto.CanBeDeleted" path="/node()"/></param>
/// <param name="ImagesMissingAltText"><inheritdoc cref="AdminContentPageListItemDto.ImagesMissingAltText" path="/node()"/></param>
/// <param name="RowVersion"><inheritdoc cref="AdminContentPageListItemDto.RowVersion" path="/node()"/></param>
public sealed record AdminContentPageDto(
    int Id,
    string? SystemKey,
    string Slug,
    string Title,
    string BodyHtml,
    string? SeoTitle,
    string? SeoDescription,
    bool IsPublished,
    DateTime? PublishedAtUtc,
    DateTime? UpdatedAtUtc,
    bool CanBeDeleted,
    int ImagesMissingAltText,
    string RowVersion);

/// <summary>
/// One contact enquiry as the inbox lists it (GLB-10, doc 11 P15 step 7).
/// <para>
/// <b>The message body is not here, and that is not a size optimisation.</b> Doc 11 P8 records that a
/// customer may paste bank account details into it. A list payload is the thing most likely to be left in
/// a browser's cache, screenshotted into a support ticket or widened later into a CSV export, so the body
/// is read only when a staff member opens one enquiry — which is also the act that marks it Read.
/// </para>
/// </summary>
/// <param name="Id">The row id.</param>
/// <param name="Name">Who sent it.</param>
/// <param name="Email">Their address, so staff can reply outside the system.</param>
/// <param name="Subject">What they said it was about, or null.</param>
/// <param name="Preview">
/// The first few words of the message, so the list is usable at all. Deliberately short, and it is still
/// personal data: see the type remarks.
/// </param>
/// <param name="OrderNumber">The order it is about, as typed, or null.</param>
/// <param name="Status">New, Read, Replied or Spam.</param>
/// <param name="HandledByUserEmail">Who last moved the status, or null.</param>
/// <param name="HandledAtUtc">When they did.</param>
/// <param name="CreatedAtUtc">When it arrived. The list is ordered by this, newest first.</param>
public sealed record AdminContactSubmissionListItemDto(
    int Id,
    string Name,
    string Email,
    string? Subject,
    string Preview,
    string? OrderNumber,
    ContactSubmissionStatus Status,
    string? HandledByUserEmail,
    DateTime? HandledAtUtc,
    DateTime CreatedAtUtc);

/// <summary>A page of the enquiry inbox, newest first.</summary>
/// <param name="Items">The enquiries on this page.</param>
/// <param name="Total">How many match the filter, so the screen can page.</param>
/// <param name="NewCount">
/// How many are still <see cref="ContactSubmissionStatus.New"/> across the whole inbox, not this page —
/// it is the number the sidebar badge wants, and it must not change when a filter does.
/// </param>
public sealed record AdminContactSubmissionsDto(
    IReadOnlyList<AdminContactSubmissionListItemDto> Items,
    int Total,
    int NewCount);

/// <summary>One enquiry, opened (GLB-10, doc 11 P15 step 7).</summary>
/// <param name="Id">The row id.</param>
/// <param name="Name">Who sent it.</param>
/// <param name="Email">Their address.</param>
/// <param name="Phone">Their number, or null.</param>
/// <param name="Subject">What they said it was about, or null.</param>
/// <param name="Message">
/// What they wrote, as plain text. Rendered by interpolation and never as markup: it is a visitor's free
/// text on an authenticated screen, so binding it as HTML would be a stored-XSS surface aimed at staff.
/// </param>
/// <param name="OrderNumber">The order it is about, as typed, or null.</param>
/// <param name="Status">New, Read, Replied or Spam. Opening a New enquiry has already made it Read.</param>
/// <param name="HandledByUserEmail">Who last moved the status, or null.</param>
/// <param name="HandledAtUtc">When they did.</param>
/// <param name="IpAddress">
/// Where it came from, or <see langword="null"/> — which means either that the server could not see an
/// address or that the ninety-day retention step has cleared it (doc 08 §8.4). The screen says which it
/// cannot tell, rather than drawing a blank field.
/// </param>
/// <param name="CreatedAtUtc">When it arrived.</param>
public sealed record AdminContactSubmissionDto(
    int Id,
    string Name,
    string Email,
    string? Phone,
    string? Subject,
    string Message,
    string? OrderNumber,
    ContactSubmissionStatus Status,
    string? HandledByUserEmail,
    DateTime? HandledAtUtc,
    string? IpAddress,
    DateTime CreatedAtUtc);
