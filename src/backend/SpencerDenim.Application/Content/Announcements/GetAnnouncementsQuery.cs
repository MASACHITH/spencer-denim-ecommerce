using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Contracts;

namespace SpencerDenim.Application.Content.Announcements;

/// <summary>Query behind <c>GET /api/content/announcements</c> (GLB-03).</summary>
public sealed record GetAnnouncementsQuery;

/// <summary>
/// The announcement lines the shell shows above the header. Its own endpoint rather than part of
/// the homepage payload, because the strip is on every page and the homepage is one of them.
/// </summary>
public sealed class GetAnnouncementsQueryHandler(IHomeContentReader content, IClock clock)
    : IQueryHandler<GetAnnouncementsQuery, IReadOnlyList<AnnouncementDto>>
{
    public Task<IReadOnlyList<AnnouncementDto>> HandleAsync(
        GetAnnouncementsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return content.GetLiveAnnouncementsAsync(clock.UtcNow.UtcDateTime, cancellationToken);
    }
}
