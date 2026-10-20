using System.Globalization;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Analytics;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Settings.Admin;

/// <summary>
/// How long the nightly purge keeps raw page views (doc 08 §3.8 group <c>Traffic</c>, §8.4; ADM-20, issue #193).
/// </summary>
/// <param name="Fields">
/// The request's fields, keyed by their body name, holding <b>only the fields the request sent</b>. Absent means
/// "not part of this save"; present means "write this" — and, unlike the text groups, present-but-empty is
/// refused rather than read as "clear it", because an unset period silently hands the purge back to
/// configuration, which is not something a staff user can see from the screen.
/// </param>
/// <param name="RowVersion">The <c>Traffic</c> save unit's token, as the screen read it.</param>
/// <param name="Actor">Who is asking, for the audit rows and the per-row permission check.</param>
public sealed record UpdateTrafficSettingsCommand(
    IReadOnlyDictionary<string, string?> Fields,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// Validates and saves <c>Traffic.RawRetentionDays</c> and <c>Traffic.BotRetentionDays</c>, writing <b>one audit
/// row per changed key</b> (doc 08 §3.8 DB2).
/// </summary>
/// <remarks>
/// <para>
/// <b>The order of the checks is <c>UpdateInvoiceAndFulfilmentSettingsCommandHandler</c>'s</b> — validate the
/// fields, then concurrency, then permission over the keys the caller <i>asked</i> to write, then the diff, then
/// save — with one step added after permission: the cross-field rule that bots are never kept longer than
/// people. It needs the stored value of a field the request may have left out, so it can only run once the
/// rows are read and known to be current, and it runs after the permission check so that a caller who may not
/// write these keys learns nothing about their stored values from it.
/// </para>
/// <para>
/// <b><c>Settings.Edit</c>, so a Store Manager may change it, and no sensitive-change alert.</b> Doc 08 §3.8's
/// table gives the group <c>Settings.Edit</c>, and its N2 trigger list (payments, SMTP, security,
/// <c>Privacy.RetainInvoiceBillTo</c>) does not include it; docs/02's "legal retention" is the invoice-retention
/// key, which stays <c>Settings.Security</c>. What keeps this one safe to delegate is the bounds: a staff user
/// can move the purge between 30 and 365 days, never to a figure that loses the traffic history (the daily
/// tables are kept whatever this says) or keeps visit-level rows for years.
/// </para>
/// <para>
/// <b>No cache to evict.</b> The purge reads the rows once a night through <c>TrafficRetentionSettings</c>, which
/// does not cache, so a save is in force from the next run.
/// </para>
/// </remarks>
public sealed class UpdateTrafficSettingsCommandHandler(
    ISettingsStore store,
    IAuditLogWriter auditLog,
    IEffectivePermissionsProvider permissionsProvider,
    IClock clock)
    : ICommandHandler<UpdateTrafficSettingsCommand, SettingsWriteResult>
{
    /// <summary>The body field for <c>Traffic.RawRetentionDays</c>.</summary>
    public const string RawField = "rawRetentionDays";

    /// <summary>The body field for <c>Traffic.BotRetentionDays</c>.</summary>
    public const string BotField = "botRetentionDays";

    /// <summary>
    /// Every key this command may touch, paired with its request field. One list, so the validation, the write,
    /// the audit rows and the permission check cannot disagree about the set.
    /// </summary>
    private static readonly (string Key, string Field)[] Targets =
    [
        (SettingKeys.Traffic.RawRetentionDays, RawField),
        (SettingKeys.Traffic.BotRetentionDays, BotField),
    ];

    /// <summary>The fields a request may carry, in the order the form shows them.</summary>
    public static IReadOnlyList<string> Fields { get; } = Targets.Select(target => target.Field).ToList();

    public async Task<SettingsWriteResult> HandleAsync(
        UpdateTrafficSettingsCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();
        var wanted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, field) in Targets)
        {
            if (!command.Fields.TryGetValue(field, out var raw))
            {
                continue;
            }

            if (CleanDays(errors, field, raw) is { } days)
            {
                wanted[key] = days;
            }
        }

        if (errors.Count > 0)
        {
            return new SettingsWriteResult.Invalid(errors);
        }

        var rows = await store.ListAsync(
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.Traffic.Group),
            cancellationToken);

        // Case-insensitively, to match the database collation; see SettingCatalog.ByKey for what Ordinal costs.
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
                    : "Somebody else changed the traffic settings while you were editing them. Reload the page "
                      + "to see their change, then re-apply yours.");
        }

        var requested = Targets
            .Where(target => wanted.ContainsKey(target.Key))
            .Select(target => byKey[target.Key]);

        if (await CheckPermissionAsync(requested, command.Actor, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (CheckBotsNotLongerThanPeople(wanted, byKey) is { } crossField)
        {
            return new SettingsWriteResult.Invalid([crossField]);
        }

        var changed = new List<string>();
        var audits = new List<AuditLog>();
        var now = clock.UtcNow.UtcDateTime;

        foreach (var (key, _) in Targets)
        {
            if (!wanted.TryGetValue(key, out var days))
            {
                continue;
            }

            var after = days.ToString(CultureInfo.InvariantCulture);

            // The audit trail's own diff, so "changed" and "has an audit row" are one question asked once.
            if (SettingsAuditTrail.ForValueChange(byKey[key], after, command.Actor, now) is not { } audit)
            {
                continue;
            }

            changed.Add(key);
            audits.Add(audit);

            var row = byKey[key];
            row.Value = after;
            row.UpdatedAtUtc = now;
            row.UpdatedByUserId = command.Actor.UserId;
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

        return outcome == SettingsSaveOutcome.ConcurrencyConflict
            ? new SettingsWriteResult.Conflict(
                SettingsConflictCodes.StaleRowVersion,
                "Somebody else changed the traffic settings while your change was being saved, so nothing was "
                + "written. Reload the page and try again.")
            : new SettingsWriteResult.Succeeded(changed);
    }

    /// <summary>
    /// One period: whole invariant digits inside its bounds, or a named error.
    /// </summary>
    /// <remarks>
    /// The bounds are <see cref="SettingKeys.Traffic"/>'s, the same constants the purge's
    /// <see cref="TrafficRetentionPeriods.Resolve"/> holds a stored value to, so the form cannot save a figure the
    /// purge would then ignore.
    /// </remarks>
    private static int? CleanDays(List<ValidationError> errors, string field, string? raw)
    {
        var (min, max, what) = field == RawField
            ? (SettingKeys.Traffic.RawRetentionDaysMin, SettingKeys.Traffic.RawRetentionDaysMax, "page views")
            : (SettingKeys.Traffic.BotRetentionDaysMin, SettingKeys.Traffic.BotRetentionDaysMax, "bot page views");

        var text = raw?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            errors.Add(new ValidationError(
                field,
                $"Enter how many days to keep {what}, between {min} and {max}. This cannot be left empty."));

            return null;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var days))
        {
            errors.Add(new ValidationError(
                field,
                $"Enter a whole number of days, in digits, between {min} and {max}."));

            return null;
        }

        if (days < min || days > max)
        {
            errors.Add(new ValidationError(
                field,
                field == RawField
                    ? $"Use between {min} and {max} days. Fewer than {min} leaves less than a month of visit-level "
                      + $"records to explain a figure from; more than {max} keeps them a year after any report "
                      + "could use them."
                    : $"Use between {min} and {max} days."));

            return null;
        }

        return days;
    }

    /// <summary>
    /// Bots are never kept longer than people, comparing the value each key will hold after this save: the new
    /// one where the request sent it, the stored one where it did not.
    /// </summary>
    /// <remarks>
    /// A stored value that is not a number in bounds is skipped rather than compared — the purge would not use it
    /// either (<see cref="TrafficRetentionPeriods.Resolve"/> falls back to configuration and cuts bots to the raw
    /// figure), and refusing the save for a row the caller is in the middle of fixing would leave no way to fix it.
    /// The error is put on whichever field the request sent, so the screen shows it beside a box the user touched.
    /// </remarks>
    private static ValidationError? CheckBotsNotLongerThanPeople(
        Dictionary<string, int> wanted,
        Dictionary<string, Setting> byKey)
    {
        var raw = wanted.TryGetValue(SettingKeys.Traffic.RawRetentionDays, out var newRaw)
            ? newRaw
            : TrafficRetentionPeriods.TryReadDays(
                byKey[SettingKeys.Traffic.RawRetentionDays].Value,
                SettingKeys.Traffic.RawRetentionDaysMin,
                SettingKeys.Traffic.RawRetentionDaysMax);

        var bot = wanted.TryGetValue(SettingKeys.Traffic.BotRetentionDays, out var newBot)
            ? newBot
            : TrafficRetentionPeriods.TryReadDays(
                byKey[SettingKeys.Traffic.BotRetentionDays].Value,
                SettingKeys.Traffic.BotRetentionDaysMin,
                SettingKeys.Traffic.BotRetentionDaysMax);

        if (raw is not { } rawDays || bot is not { } botDays || botDays <= rawDays)
        {
            return null;
        }

        var field = wanted.ContainsKey(SettingKeys.Traffic.BotRetentionDays) ? BotField : RawField;

        return new ValidationError(
            field,
            $"Bot page views cannot be kept longer than page views ({botDays} days against {rawDays}). "
            + "Shorten the bot period or lengthen the other.");
    }

    /// <summary>
    /// Doc 08 §3.8 DB2 on the write path: each row carries the permission needed to change it, and this reads it.
    /// </summary>
    /// <remarks>
    /// The same eight lines as the other settings handlers, duplicated on purpose for the reason
    /// <c>UpdateBankDetailsCommandHandler</c> records: a shared helper would put the control behind an indirection
    /// a future caller could forget. Permissions come from the user's roles, never from claims (doc 08 §3.1 R4).
    /// </remarks>
    private async Task<SettingsWriteResult.Forbidden?> CheckPermissionAsync(
        IEnumerable<Setting> rows,
        AdminActor actor,
        CancellationToken cancellationToken)
    {
        var changing = rows.ToList();

        if (actor.UserId is not { } userId)
        {
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
