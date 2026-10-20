using System.Globalization;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.Application.Content.Admin.Announcements;

/// <summary>One line of the announcement strip as the editor lists it (GLB-03, doc 11 P13 step 9).</summary>
/// <param name="StartsAt">Colombo wall-clock, or null.</param>
/// <param name="EndsAt">Colombo wall-clock, or null.</param>
/// <param name="IsLiveNow">Switched on, not blank, and inside its schedule right now.</param>
/// <param name="Version">The concurrency token to send back. See <see cref="AnnouncementVersion"/>.</param>
public sealed record AdminAnnouncementDto(
    int Id,
    string Message,
    string? LinkUrl,
    short SortOrder,
    bool IsActive,
    string? StartsAt,
    string? EndsAt,
    bool IsLiveNow,
    string Version);

/// <summary>What an announcement write answers with.</summary>
public sealed record AnnouncementSavedDto(int Id, string? Version);

/// <summary>The fields of one announcement as the form sends them.</summary>
public sealed record AnnouncementFields(
    string? Message,
    string? LinkUrl,
    int? SortOrder,
    bool IsActive,
    DateTime? StartsAt,
    DateTime? EndsAt);

public sealed record CreateAnnouncementCommand(AnnouncementFields Fields, AdminActor Actor);

public sealed record UpdateAnnouncementCommand(int MessageId, AnnouncementFields Fields, string? Version, AdminActor Actor);

public sealed record DeleteAnnouncementCommand(int MessageId, string? Version, AdminActor Actor);

/// <summary>
/// The concurrency token for an announcement.
/// <para>
/// <b><c>content.AnnouncementMessages</c> has no <c>rowversion</c> column</b> (doc 08 §3.7 marks it [A],
/// not [RV]), and adding one is a migration this task does not own. Its audit columns are the next best
/// thing: <c>UpdatedAtUtc</c> (or <c>CreatedAtUtc</c> for a row never edited) moves on every save, to the
/// tick, so "the version you read is not the version that is there" is still answerable. The handler checks
/// the token the screen sent; the race between two saves read from one version is closed in the database,
/// because <c>UpdatedAtUtc</c> is also an EF concurrency token (#192 tester D1): the second UPDATE or
/// DELETE matches no row and its save answers 409. A row never edited has a null <c>UpdatedAtUtc</c>, and
/// EF's predicate is then <c>IS NULL</c>, which the first save changes — so that case is covered too.
/// </para>
/// </summary>
public static class AnnouncementVersion
{
    /// <summary>
    /// To the <b>millisecond</b>, because the column is <c>datetime2(3)</c>: the value the save stamps in
    /// memory carries ticks the database then drops, and a token built from the ticks would never match the
    /// row read back a moment later.
    /// </summary>
    public static string Of(DateTime createdAtUtc, DateTime? updatedAtUtc) =>
        ((updatedAtUtc ?? createdAtUtc).Ticks / TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture);

    public static bool Matches(AnnouncementMessage message, string? supplied)
    {
        ArgumentNullException.ThrowIfNull(message);

        return !string.IsNullOrWhiteSpace(supplied)
            && string.Equals(Of(message.CreatedAtUtc, message.UpdatedAtUtc), supplied.Trim(), StringComparison.Ordinal);
    }
}

/// <summary>
/// The announcement-strip writes of ADM-17 (GLB-03, doc 11 P13 part C): add, edit, remove. The message is
/// plain text — the strip renders it by interpolation — so nothing here sanitises markup; a line break is
/// refused because the strip is one line.
/// </summary>
public sealed class AnnouncementWriteHandlers(
    IAnnouncementAdminStore store,
    IAuditLogWriter auditLog,
    IHomePageCache homePageCache,
    IClock clock)
    : ICommandHandler<CreateAnnouncementCommand, ContentWriteResult>,
      ICommandHandler<UpdateAnnouncementCommand, ContentWriteResult>,
      ICommandHandler<DeleteAnnouncementCommand, ContentWriteResult>
{
    /// <summary><c>ops.AuditLogs.EntityType</c> for a message.</summary>
    public const string EntityType = "AnnouncementMessage";

    /// <summary>The most messages the strip holds, on or off.</summary>
    public const int MaxMessages = 20;

    public async Task<ContentWriteResult> HandleAsync(CreateAnnouncementCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();
        var values = Read(errors, command.Fields);

        if (errors.Count > 0)
        {
            return new ContentWriteResult.Invalid(errors);
        }

        if (await store.CountAsync(cancellationToken) >= MaxMessages)
        {
            return new ContentWriteResult.Conflict(
                ContentConflictCodes.TooManyAnnouncements,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The strip already holds {MaxMessages} messages. Remove one you no longer need first."));
        }

        var message = new AnnouncementMessage();
        values.ApplyTo(message);
        store.Add(message);

        auditLog.Enlist(ContentAuditTrail.Row(
            AuditActions.Create,
            EntityType,
            "new",
            before: null,
            Snapshot(message),
            command.Actor,
            clock.UtcNow.UtcDateTime));

        if (await store.SaveAsync(command.Actor.UserId, cancellationToken) == ContentSaveOutcome.ConcurrencyConflict)
        {
            return Stale("lost-race");
        }

        homePageCache.Evict();

        return new ContentWriteResult.Succeeded(
            message.Id, [], AnnouncementVersion.Of(message.CreatedAtUtc, message.UpdatedAtUtc));
    }

    public async Task<ContentWriteResult> HandleAsync(UpdateAnnouncementCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var message = await store.FindAsync(command.MessageId, cancellationToken);
        if (message is null)
        {
            return NotFound(command.MessageId);
        }

        if (!AnnouncementVersion.Matches(message, command.Version))
        {
            return Stale(command.Version);
        }

        var errors = new List<ValidationError>();
        var values = Read(errors, command.Fields);

        if (errors.Count > 0)
        {
            return new ContentWriteResult.Invalid(errors);
        }

        var before = Snapshot(message);
        values.ApplyTo(message);

        var audit = ContentAuditTrail.ForChange(
            AuditActions.Update,
            EntityType,
            Id(message.Id),
            before,
            Snapshot(message),
            command.Actor,
            clock.UtcNow.UtcDateTime);

        if (audit is null)
        {
            return new ContentWriteResult.Unchanged(message.Id);
        }

        auditLog.Enlist(audit);
        if (await store.SaveAsync(command.Actor.UserId, cancellationToken) == ContentSaveOutcome.ConcurrencyConflict)
        {
            return Stale("lost-race");
        }

        homePageCache.Evict();

        return new ContentWriteResult.Succeeded(
            message.Id, [], AnnouncementVersion.Of(message.CreatedAtUtc, message.UpdatedAtUtc));
    }

    public async Task<ContentWriteResult> HandleAsync(DeleteAnnouncementCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var message = await store.FindAsync(command.MessageId, cancellationToken);
        if (message is null)
        {
            return NotFound(command.MessageId);
        }

        if (!AnnouncementVersion.Matches(message, command.Version))
        {
            return Stale(command.Version);
        }

        auditLog.Enlist(ContentAuditTrail.Row(
            AuditActions.Delete,
            EntityType,
            Id(message.Id),
            Snapshot(message),
            after: null,
            command.Actor,
            clock.UtcNow.UtcDateTime));

        store.Remove(message);
        if (await store.SaveAsync(command.Actor.UserId, cancellationToken) == ContentSaveOutcome.ConcurrencyConflict)
        {
            return Stale("lost-race");
        }

        homePageCache.Evict();

        return new ContentWriteResult.Succeeded(message.Id);
    }

    private static Values Read(List<ValidationError> errors, AnnouncementFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var message = ContentFieldRules.RequiredText(errors, "message", fields.Message, ContentFieldLimits.AnnouncementMaxLength);
        var linkUrl = ContentFieldRules.OptionalLink(errors, "linkUrl", fields.LinkUrl, mayLeaveSite: true);
        var sortOrder = ContentFieldRules.SortOrder(errors, "sortOrder", fields.SortOrder);
        var (startsAtUtc, endsAtUtc) = ContentFieldRules.Schedule(errors, "endsAt", fields.StartsAt, fields.EndsAt);

        return new Values(message ?? string.Empty, linkUrl, sortOrder, fields.IsActive, startsAtUtc, endsAtUtc);
    }

    private static Dictionary<string, object?> Snapshot(AnnouncementMessage message) => new(StringComparer.Ordinal)
    {
        ["message"] = message.Message,
        ["linkUrl"] = message.LinkUrl,
        ["sortOrder"] = message.SortOrder,
        ["isActive"] = message.IsActive,
        ["startsAtUtc"] = message.StartsAtUtc,
        ["endsAtUtc"] = message.EndsAtUtc,
    };

    private static ContentWriteResult.NotFound NotFound(int id) =>
        new(string.Create(CultureInfo.InvariantCulture, $"No announcement has the id {id}."));

    private static ContentWriteResult.Conflict Stale(string? supplied) =>
        new(
            ContentConflictCodes.StaleRowVersion,
            string.IsNullOrWhiteSpace(supplied)
                ? "This request did not carry the message's current version. Reload and try again."
                : "Somebody else changed this message while you were editing it. Reload to see their change.");

    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    private sealed record Values(
        string Message,
        string? LinkUrl,
        short SortOrder,
        bool IsActive,
        DateTime? StartsAtUtc,
        DateTime? EndsAtUtc)
    {
        public void ApplyTo(AnnouncementMessage message)
        {
            message.Message = Message;
            message.LinkUrl = LinkUrl;
            message.SortOrder = SortOrder;
            message.IsActive = IsActive;
            message.StartsAtUtc = StartsAtUtc;
            message.EndsAtUtc = EndsAtUtc;
        }
    }
}
