using SpencerDenim.Application.Content.Admin.Home;
using SpencerDenim.Application.Content.Admin.Announcements;
using SpencerDenim.Application.Content.Admin.SizeGuides;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Media;

namespace SpencerDenim.Application.Content.Admin.Abstractions;

/// <summary>
/// One media asset, as much of it as a content write needs in order to decide whether it may point at it.
/// </summary>
/// <param name="Id">The row id the content column stores.</param>
/// <param name="MediaType">Image or video.</param>
/// <param name="Status">Only a <see cref="MediaAssetStatus.Ready"/> asset has a file to serve.</param>
/// <param name="Visibility">Only a public asset can be shown on the storefront.</param>
/// <param name="AltText">Doc 08 §3.3 and GLB-12: a public image needs it before content using it is shown.</param>
public sealed record ContentMediaAsset(
    int Id,
    MediaAssetType MediaType,
    MediaAssetStatus Status,
    MediaVisibility Visibility,
    string? AltText);

/// <summary>A <c>media.MediaUsages</c> row a content row should own (doc 08 §3.3).</summary>
public sealed record ContentMediaUsage(int MediaAssetId, string UsageRole);

/// <summary>
/// The usage rows one owner should hold after a save. <see cref="OwnerId"/> is read <b>after</b> the first
/// save, because a new row has no id until then; an empty <see cref="Desired"/> removes them all, which
/// is what a deletion needs.
/// </summary>
/// <param name="OwnerType">A <c>MediaUsageOwnerTypes</c> value.</param>
/// <param name="OwnerId">The owner's id, read once the row has one.</param>
/// <param name="Desired">Exactly the rows that should exist afterwards.</param>
public sealed record ContentMediaUsageChange(
    string OwnerType,
    Func<int> OwnerId,
    IReadOnlyCollection<ContentMediaUsage> Desired);

/// <summary>Lookups every content write shares.</summary>
public interface IContentLookups
{
    /// <summary>One asset by public id, or <see langword="null"/> when there is none (or it is soft-deleted).</summary>
    Task<ContentMediaAsset?> FindMediaAsync(Guid publicId, CancellationToken cancellationToken);
}

/// <summary>
/// Write port for the homepage blocks (ADM-17, HOME-01..10).
/// <para>
/// The arrangement <see cref="IContentPageStore"/> established: tracked entities out, one unit of work
/// in, every rule in the handler. The only judgement here is the database's own — a row somebody else
/// changed first.
/// </para>
/// </summary>
public interface IHomeContentAdminStore : IContentLookups
{
    /// <summary>One section for update, with its items, or <see langword="null"/>.</summary>
    Task<HomeSection?> FindSectionAsync(int sectionId, CancellationToken cancellationToken);

    /// <summary>The section an item belongs to, for update, with its items; or <see langword="null"/>.</summary>
    Task<HomeSection?> FindSectionOfItemAsync(int itemId, CancellationToken cancellationToken);

    /// <summary>Whether a department with this id exists.</summary>
    Task<bool> DepartmentExistsAsync(int departmentId, CancellationToken cancellationToken);

    /// <summary>The id of the product with this slug, or <see langword="null"/>.</summary>
    Task<int?> FindProductIdAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Adds an item to the unit of work.</summary>
    void AddItem(HomeSectionItem item);

    /// <summary>
    /// Removes an item from the unit of work. A hard delete: doc 08 §1.6 makes it a pure child row with
    /// no soft-delete flag, and its usage rows go with it.
    /// </summary>
    void RemoveItem(HomeSectionItem item);

    /// <summary>
    /// Makes the section part of the save even when none of its own columns changed, so an item write is
    /// checked against — and moves — the section's row version.
    /// </summary>
    void MarkChanged(HomeSection section);

    /// <summary>
    /// Commits the unit of work, then rewrites the usage rows <paramref name="usage"/> describes, in one
    /// transaction. Answers <see cref="ContentSaveOutcome.ConcurrencyConflict"/> when the row version no
    /// longer matches, and nothing is written.
    /// </summary>
    Task<ContentSaveOutcome> SaveAsync(
        int? actingUserId,
        ContentMediaUsageChange? usage,
        CancellationToken cancellationToken);
}

/// <summary>Read port for the homepage editor.</summary>
public interface IHomeContentAdminReader
{
    /// <summary>Every block and item, with <c>IsLiveNow</c> worked out at <paramref name="utcNow"/>.</summary>
    Task<AdminHomeContentDto> GetAsync(DateTime utcNow, CancellationToken cancellationToken);
}

/// <summary>Write port for the announcement strip (ADM-17, GLB-03).</summary>
public interface IAnnouncementAdminStore
{
    /// <summary>One message for update, or <see langword="null"/>.</summary>
    Task<AnnouncementMessage?> FindAsync(int messageId, CancellationToken cancellationToken);

    /// <summary>How many messages exist, on or off.</summary>
    Task<int> CountAsync(CancellationToken cancellationToken);

    void Add(AnnouncementMessage message);

    /// <summary>A hard delete: the table has no soft-delete flag (doc 08 §3.7).</summary>
    void Remove(AnnouncementMessage message);

    Task SaveAsync(int? actingUserId, CancellationToken cancellationToken);
}

/// <summary>Read port for the announcement editor.</summary>
public interface IAnnouncementAdminReader
{
    Task<IReadOnlyList<AdminAnnouncementDto>> ListAsync(DateTime utcNow, CancellationToken cancellationToken);
}

/// <summary>Write port for the size guides (ADM-17, PDP-04).</summary>
public interface ISizeGuideAdminStore : IContentLookups
{
    /// <summary>One live guide for update, or <see langword="null"/>.</summary>
    Task<SizeGuide?> FindAsync(int sizeGuideId, CancellationToken cancellationToken);

    /// <summary>How many live categories and products point at this guide.</summary>
    Task<(int Categories, int Products)> UsageAsync(int sizeGuideId, CancellationToken cancellationToken);

    void Add(SizeGuide sizeGuide);

    /// <inheritdoc cref="IHomeContentAdminStore.SaveAsync"/>
    Task<ContentSaveOutcome> SaveAsync(
        int? actingUserId,
        ContentMediaUsageChange? usage,
        CancellationToken cancellationToken);
}

/// <summary>Read port for the size-guide editor.</summary>
public interface ISizeGuideAdminReader
{
    Task<IReadOnlyList<AdminSizeGuideListItemDto>> ListAsync(CancellationToken cancellationToken);

    Task<AdminSizeGuideDto?> GetAsync(int sizeGuideId, CancellationToken cancellationToken);
}
