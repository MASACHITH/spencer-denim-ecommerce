namespace SpencerDenim.Application.Content.Contracts;

/// <summary>
/// One line of the rotating announcement strip above the header (GLB-03), already filtered to the
/// messages that are live right now and ordered the way the client ordered them.
/// </summary>
/// <param name="Id"><c>content.AnnouncementMessages.Id</c>.</param>
/// <param name="Message">The line itself. Never blank: a blank message is dropped by the reader.</param>
/// <param name="LinkUrl">Where the line goes, when the client gave it somewhere to go.</param>
public sealed record AnnouncementDto(int Id, string Message, string? LinkUrl);
