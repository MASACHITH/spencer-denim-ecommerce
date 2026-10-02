using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Settings.Admin;

/// <summary>
/// The contact number and the social profile addresses (ADM-22, GLB-07, GLB-08).
/// </summary>
/// <param name="Fields">
/// The request's fields, keyed by the name they carry in the body, holding <b>only the fields the request
/// actually sent</b>.
/// <para>
/// <b>A dictionary rather than four <c>string?</c> properties, and the difference is a latent data loss.</b>
/// PUT replaces, so this command's first version read a missing field as <see langword="null"/> and cleared
/// the key — which is W1-12's Blocker shape exactly. It could not bite while every caller could read every
/// group, and it bites the moment #53 adds a role that cannot: that caller's screen would omit the three
/// social addresses it never received, and their first WhatsApp save would wipe the client's social links.
/// </para>
/// <para>
/// So <b>absent and null are now different</b>: absent means "I am not writing this key", null means "clear
/// it". The endpoint reads presence off the JSON, and the screen omits what the server did not send it. The
/// W1-12 guarantee is unchanged for the ordinary caller — the screen sends every field it was given, and
/// <c>AdminSettingsShapeTests</c> pins the pairing.
/// </para>
/// </param>
/// <param name="RowVersion">The group's token, as the screen read it.</param>
/// <param name="Actor">Who is asking, for the audit rows and for the permission check.</param>
public sealed record UpdateContactAndSocialCommand(
    IReadOnlyDictionary<string, string?> Fields,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// Validates and saves the contact and social keys, writing <b>one audit row per changed key</b> (doc 08
/// §3.8).
/// <para>
/// Two groups in one command, which is a deliberate departure from one-command-per-group: a shopper reads the
/// WhatsApp number and the three social icons in the same strip of the footer, a staff user fills them in from
/// the same email from the client, and splitting them would make the screen send two requests whose partial
/// success nobody could describe. The <i>permission</i> is still per row — and since round 1 that is true
/// rather than merely claimed, see <see cref="CheckPermissionAsync"/>. The group token covers both groups'
/// rows (<see cref="SettingCatalog.SaveUnits"/>), so the concurrency check cannot be satisfied by half of them.
/// </para>
/// </summary>
public sealed class UpdateContactAndSocialCommandHandler(
    ISettingsStore store,
    IAuditLogWriter auditLog,
    ISiteSettingsCache cache,
    IEffectivePermissionsProvider permissionsProvider,
    IClock clock)
    : ICommandHandler<UpdateContactAndSocialCommand, SettingsWriteResult>
{
    /// <summary>
    /// Every key this command may touch, paired with the request field it comes from. One list, so the
    /// validation, the write, the audit rows and the permission check cannot disagree about the set.
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
        var wanted = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Only the fields the request carried. A field that is absent is not validated, not permission
        // checked and not written — it is simply not part of this save.
        foreach (var (key, field) in Targets)
        {
            if (!command.Fields.TryGetValue(field, out var raw))
            {
                continue;
            }

            if (field == "whatsApp")
            {
                SettingFieldRules.CheckWhatsAppNumber(errors, field, raw);
                wanted[key] = SettingFieldRules.Clean(raw);
                continue;
            }

            var label = field switch
            {
                "facebookUrl" => "Facebook",
                "instagramUrl" => "Instagram",
                _ => "TikTok",
            };

            SettingFieldRules.CheckProfileUrl(errors, field, raw, label);

            // The *normalised* form is what gets stored, so the text a client reads back tells them where the
            // link actually goes. See SettingFieldRules.TryNormaliseProfileUrl for the homograph this closes.
            wanted[key] = SettingFieldRules.TryNormaliseProfileUrl(raw, out var normalised)
                ? normalised
                : SettingFieldRules.Clean(raw);
        }

        if (errors.Count > 0)
        {
            return new SettingsWriteResult.Invalid(errors);
        }

        var rows = await store.ListAsync(
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.Contact.Group), cancellationToken);

        // Case-insensitively, to match the database collation. See SettingCatalog.ByKey for what an Ordinal
        // dictionary here costs.
        var byKey = rows.ToDictionary(row => row.Key, StringComparer.OrdinalIgnoreCase);

        var missing = Targets
            .Where(target => !byKey.ContainsKey(target.Key))
            .Select(target => target.Key)
            .ToList();

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

        var changed = new List<string>();
        var audits = new List<AuditLog>();
        var now = clock.UtcNow.UtcDateTime;

        foreach (var (key, _) in Targets)
        {
            if (!wanted.TryGetValue(key, out var after))
            {
                continue;
            }

            var row = byKey[key];

            // The diff is the audit trail's own, so "changed" and "has an audit row" are the same question
            // asked once. A row that did not move keeps its UpdatedAtUtc, which is what makes that column mean
            // "when this value last changed" rather than "when somebody last pressed Save".
            if (SettingsAuditTrail.ForValueChange(row, after, command.Actor, now) is not { } audit)
            {
                continue;
            }

            changed.Add(key);
            audits.Add(audit);
        }

        if (changed.Count == 0)
        {
            return new SettingsWriteResult.Unchanged();
        }

        // After the diff, so a key the caller may not write is only a refusal when they are actually changing
        // it — and before any mutation, so a refusal leaves nothing half applied.
        if (await CheckPermissionAsync(changed.Select(key => byKey[key]), command.Actor, cancellationToken)
            is { } forbidden)
        {
            return forbidden;
        }

        foreach (var key in changed)
        {
            var row = byKey[key];
            row.Value = wanted[key];
            row.UpdatedAtUtc = now;
            row.UpdatedByUserId = command.Actor.UserId;
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

    /// <summary>
    /// Doc 08 §3.8 DB2, on the write path: each row carries the permission needed to change it, and this is
    /// where that column is read.
    /// <para>
    /// <b>Added in round 1, because the comment above used to claim it and the code did not do it.</b> The
    /// read model filtered per row; <c>grep RequiredPermission</c> found it nowhere on the write side. Nothing
    /// could exploit it while every settings key required <c>Settings.Edit</c> — and #53 adds the keys that
    /// do not, so this is the control those keys will land on rather than something to add with them.
    /// </para>
    /// <para>
    /// Permissions come from the user's roles through <see cref="IEffectivePermissionsProvider"/>, never from
    /// claims on the principal (doc 08 §3.1 R4): a cookie carries what was true when it was issued, and a
    /// staff user demoted five minutes ago must not still be writing bank details out of their own cookie.
    /// </para>
    /// </summary>
    private async Task<SettingsWriteResult.Forbidden?> CheckPermissionAsync(
        IEnumerable<Setting> rows,
        AdminActor actor,
        CancellationToken cancellationToken)
    {
        var changing = rows.ToList();

        if (actor.UserId is not { } userId)
        {
            // No signed-in user, so no permissions. Unreachable from the endpoint, which requires a session;
            // refusing rather than allowing is the only safe answer for a caller nothing is known about.
            return new SettingsWriteResult.Forbidden(changing.Select(row => row.Key).ToList());
        }

        var permissions = await permissionsProvider.GetEffectivePermissionsAsync(userId, cancellationToken);

        var refused = changing
            .Where(row => !permissions.Has(row.RequiredPermission))
            .Select(row => row.Key)
            .ToList();

        return refused.Count > 0 ? new SettingsWriteResult.Forbidden(refused) : null;
    }
}
