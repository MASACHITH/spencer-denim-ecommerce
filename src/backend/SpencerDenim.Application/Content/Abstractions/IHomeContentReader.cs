using SpencerDenim.Application.Content.Contracts;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.Application.Content.Abstractions;

/// <summary>
/// Read port for admin-managed homepage and shell content (HOME-01..10, GLB-03).
/// <para>
/// Implementations project straight to DTOs with <c>AsNoTracking</c>, so no EF entity leaves the
/// infrastructure layer (doc 08 §10.3), and they apply the schedule of
/// <see cref="ContentSchedule"/> themselves — a caller never receives something that is off or out
/// of its window and has to remember to hide it.
/// </para>
/// </summary>
public interface IHomeContentReader
{
    /// <summary>
    /// The sections that are live at <paramref name="utcNow"/>, ascending by sort order, each with
    /// its live items in their own order. Content only: a product row is filled in by the use case,
    /// which owns the catalogue.
    /// </summary>
    Task<IReadOnlyList<HomeSectionContent>> GetLiveSectionsAsync(DateTime utcNow, CancellationToken cancellationToken);

    /// <summary>
    /// The soonest instant strictly after <paramref name="utcNow"/> at which the homepage's content changes
    /// by itself: a <c>StartsAtUtc</c> or <c>EndsAtUtc</c> of a switched-on section, or of a switched-on item
    /// in a switched-on section. Null when nothing is scheduled to change. The homepage cache expires its
    /// entry there (issue #37).
    /// </summary>
    Task<DateTime?> GetNextScheduleBoundaryAsync(DateTime utcNow, CancellationToken cancellationToken);

    /// <summary>The announcement lines that are live at <paramref name="utcNow"/> (GLB-03), in order.</summary>
    Task<IReadOnlyList<AnnouncementDto>> GetLiveAnnouncementsAsync(DateTime utcNow, CancellationToken cancellationToken);
}

/// <summary>
/// A live section as the content store hands it over: everything except the products, which only
/// the catalogue can supply.
/// </summary>
/// <param name="Id"><c>content.HomeSections.Id</c>.</param>
/// <param name="Type">Which block this is (HOME-01..10).</param>
/// <param name="Title">Heading.</param>
/// <param name="Subtitle">Standfirst.</param>
/// <param name="SortOrder">Position on the page.</param>
/// <param name="SettingsJson">The raw settings column, read by <see cref="HomeSectionSettings.Parse"/>.</param>
/// <param name="Items">Live items, in sort order.</param>
public sealed record HomeSectionContent(
    int Id,
    HomeSectionType Type,
    string? Title,
    string? Subtitle,
    short SortOrder,
    string? SettingsJson,
    IReadOnlyList<HomeSectionItemDto> Items);
