using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Settings.Admin;

/// <summary>
/// The contact number and the social profile addresses, saved together (ADM-22, GLB-07, GLB-08).
/// <para>
/// <b>Every field is sent on every save, and a field left out means "clear it".</b> That is PUT's own
/// semantics and it is what the ADM-11 endpoints already document — but it is also the exact shape of
/// W1-12's blocker, where a request model that omitted three image columns wiped them on every save while
/// 1,571 tests round-tripped null to null. So these are <c>string?</c> on the command deliberately, the
/// screen sends all four, and <c>AdminSettingsWriteShapeTests</c> pins the pairing with fixtures where no
/// value is null.
/// </para>
/// </summary>
/// <param name="WhatsApp">Digits only, international form, or null/blank to clear.</param>
/// <param name="FacebookUrl">An absolute http(s) address, or null/blank to hide the icon.</param>
/// <param name="InstagramUrl"><inheritdoc cref="FacebookUrl" path="/node()"/></param>
/// <param name="TikTokUrl"><inheritdoc cref="FacebookUrl" path="/node()"/></param>
/// <param name="RowVersion">The group's token, as the screen read it.</param>
/// <param name="Actor">Who is asking, for the audit rows.</param>
public sealed record UpdateContactAndSocialCommand(
    string? WhatsApp,
    string? FacebookUrl,
    string? InstagramUrl,
    string? TikTokUrl,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// Validates and saves the contact and social keys, writing <b>one audit row per changed key</b> (doc 08
/// §3.8).
/// <para>
/// Two groups in one command, which is a deliberate departure from one-command-per-group: a shopper reads
/// the WhatsApp number and the three social icons in the same strip of the footer, a staff user fills them
/// in from the same email from the client, and splitting them would make the screen send two requests whose
/// partial success nobody could describe. The <i>permission</i> is still per row, and the group token covers
/// both groups' rows, so the concurrency check cannot be satisfied by half of them.
/// </para>
/// </summary>
public sealed class UpdateContactAndSocialCommandHandler(
    ISettingsStore store,
    IAuditLogWriter auditLog,
    ISiteSettingsCache cache,
    IClock clock)
    : ICommandHandler<UpdateContactAndSocialCommand, SettingsWriteResult>
{
    /// <summary>
    /// Every key this command may touch, paired with the request field it comes from. One list, so the
    /// validation, the write, the audit rows and the "which rows does the token cover" question cannot
    /// disagree about the set.
    /// </summary>
    private static readonly (string Key, string Field)[] Targets =
    [
        (SettingKeys.Contact.WhatsApp, "whatsApp"),
        (SettingKeys.Social.FacebookUrl, "facebookUrl"),
        (SettingKeys.Social.InstagramUrl, "instagramUrl"),
        (SettingKeys.Social.TikTokUrl, "tikTokUrl"),
    ];

    public async Task<SettingsWriteResult> HandleAsync(
        UpdateContactAndSocialCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();

        SettingFieldRules.CheckWhatsAppNumber(errors, "whatsApp", command.WhatsApp);
        SettingFieldRules.CheckProfileUrl(errors, "facebookUrl", command.FacebookUrl, "Facebook");
        SettingFieldRules.CheckProfileUrl(errors, "instagramUrl", command.InstagramUrl, "Instagram");
        SettingFieldRules.CheckProfileUrl(errors, "tikTokUrl", command.TikTokUrl, "TikTok");

        if (errors.Count > 0)
        {
            return new SettingsWriteResult.Invalid(errors);
        }

        // The save unit's keys, from the shared declaration, rather than this file's own list: the token the
        // read model published covers exactly these rows, and the two sides disagreeing about the set is what
        // made every valid save a 409. `Targets` still owns the key-to-field mapping, and
        // AdminSettingsShapeTests derives the expected field names from the catalogue so the two cannot drift.
        var rows = await store.ListAsync(
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.Contact.Group), cancellationToken);
        var byKey = rows.ToDictionary(row => row.Key, StringComparer.Ordinal);

        var missing = Targets.Where(target => !byKey.ContainsKey(target.Key)).Select(target => target.Key).ToList();
        if (missing.Count > 0)
        {
            return new SettingsWriteResult.NotSeeded(
                $"These settings do not exist in this database: {string.Join(", ", missing)}. "
                + "Run the settings seeder before using this screen.");
        }

        if (!SettingsGroupVersion.Matches(rows, command.RowVersion))
        {
            return new SettingsWriteResult.Conflict(
                SettingsConflictCodes.StaleRowVersion,
                SettingFieldRules.ReadRowVersion(command.RowVersion) is null
                    ? "This request did not carry the settings' current version, so it cannot be applied "
                      + "safely. Reload the page and try again."
                    : "Somebody else changed these settings while you were editing them. Reload the page to "
                      + "see their change, then re-apply yours.");
        }

        var wanted = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [SettingKeys.Contact.WhatsApp] = SettingFieldRules.Clean(command.WhatsApp),
            [SettingKeys.Social.FacebookUrl] = SettingFieldRules.Clean(command.FacebookUrl),
            [SettingKeys.Social.InstagramUrl] = SettingFieldRules.Clean(command.InstagramUrl),
            [SettingKeys.Social.TikTokUrl] = SettingFieldRules.Clean(command.TikTokUrl),
        };

        var changed = new List<string>();
        var audits = new List<AuditLog>();
        var now = clock.UtcNow.UtcDateTime;

        foreach (var (key, _) in Targets)
        {
            var row = byKey[key];
            var after = wanted[key];

            // The diff is the audit trail's own, so "changed" and "has an audit row" are the same question
            // asked once. A row that did not move keeps its UpdatedAtUtc, which is what makes that column
            // mean "when this value last changed" rather than "when somebody last pressed Save".
            if (SettingsAuditTrail.ForValueChange(row, after, command.Actor, now) is not { } audit)
            {
                continue;
            }

            row.Value = after;
            row.UpdatedAtUtc = now;
            row.UpdatedByUserId = command.Actor.UserId;

            changed.Add(key);
            audits.Add(audit);
        }

        if (changed.Count == 0)
        {
            return new SettingsWriteResult.Unchanged();
        }

        foreach (var audit in audits)
        {
            auditLog.Enlist(audit);
        }

        var outcome = await store.SaveAsync(command.Actor.UserId, cancellationToken);

        if (outcome == SettingsSaveOutcome.ConcurrencyConflict)
        {
            return new SettingsWriteResult.Conflict(
                SettingsConflictCodes.StaleRowVersion,
                "Somebody else changed these settings while your change was being saved, so nothing was "
                + "written. Reload the page and try again.");
        }

        cache.Evict();

        return new SettingsWriteResult.Succeeded(changed);
    }
}
