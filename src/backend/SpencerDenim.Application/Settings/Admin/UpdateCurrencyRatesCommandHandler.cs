using System.Globalization;
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
/// The rates the storefront uses to show a price in USD, GBP, EUR, AUD or AED (doc 08 §3.8 group
/// <c>Currency.Display</c>; issue #248).
/// </summary>
/// <param name="Fields">
/// The request's fields, keyed by their body name (<c>usd</c>, <c>gbp</c>, <c>eur</c>, <c>aud</c>, <c>aed</c>),
/// holding <b>only the fields the request sent</b>. Absent means "not part of this save"; present means "write
/// this", and present-but-empty (or null) means <b>clear this rate</b>, so the currency stops being offered.
/// </param>
/// <param name="RowVersion">The <c>Currency.Display</c> save unit's token, as the screen read it.</param>
/// <param name="Actor">Who is asking, for the audit rows and the per-row permission check.</param>
public sealed record UpdateCurrencyRatesCommand(
    IReadOnlyDictionary<string, string?> Fields,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// Validates and saves the five display rates, writing <b>one audit row per changed key</b> (doc 08 §3.8 DB2).
/// </summary>
/// <remarks>
/// <para>
/// <b>The order of the checks is <see cref="UpdateTrafficSettingsCommandHandler"/>'s</b>: validate the fields, then
/// the seeded rows, then concurrency, then permission over the keys the caller <i>asked</i> to write, then the
/// diff, then save. There is no cross-field rule here: the rates are independent of each other.
/// </para>
/// <para>
/// <b>An empty field clears the rate, where Traffic refuses an empty one, and the reason is the opposite one.</b>
/// An unset retention period silently hands the purge back to configuration, which nothing on the screen would
/// show. An unset rate has a visible, intended meaning: the currency is not offered to shoppers. Staff need that
/// to retire a currency (or to stop showing one whose rate they cannot keep current), and a form that could only
/// replace a rate with another would leave them a stale figure as the only way to hide it.
/// </para>
/// <para>
/// <b><c>Settings.Edit</c> and no sensitive-change alert</b>, deliberately: see <see cref="SettingKeys.CurrencyDisplay"/>.
/// Every change is still audited, one row per rate, so "who moved the pound to 420, and when" has an answer.
/// A rate's age is the row's own <c>UpdatedAtUtc</c>, stamped here only for a rate that actually changed; a
/// save that repeats a stored rate (even typed as <c>399.50</c> for a stored <c>399.5</c>) writes nothing.
/// </para>
/// </remarks>
public sealed class UpdateCurrencyRatesCommandHandler(
    ISettingsStore store,
    IAuditLogWriter auditLog,
    IEffectivePermissionsProvider permissionsProvider,
    IClock clock,
    ICurrencyRatesCache cache)
    : ICommandHandler<UpdateCurrencyRatesCommand, SettingsWriteResult>
{
    /// <summary>
    /// Every key this command may touch, paired with its request field and currency. Derived from
    /// <see cref="DisplayCurrencies"/>, so the validation, the write, the audit rows and the permission check
    /// cannot disagree about the set.
    /// </summary>
    private static readonly DisplayCurrency[] Targets = [.. DisplayCurrencies.All];

    /// <summary>The fields a request may carry, in the order the form shows them.</summary>
    public static IReadOnlyList<string> Fields { get; } = Targets.Select(target => target.Field).ToList();

    public async Task<SettingsWriteResult> HandleAsync(
        UpdateCurrencyRatesCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();

        // Key to the canonical text to store, or null to clear the rate.
        var wanted = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in Targets)
        {
            if (!command.Fields.TryGetValue(target.Field, out var raw))
            {
                continue;
            }

            var text = raw?.Trim();

            if (string.IsNullOrEmpty(text))
            {
                wanted[target.SettingKey] = null;
                continue;
            }

            switch (CurrencyRateRules.Check(text, out var rate))
            {
                case CurrencyRateProblem.None:
                    wanted[target.SettingKey] = CurrencyRateRules.Canonical(rate);
                    break;

                case CurrencyRateProblem.TooManyDecimals:
                    errors.Add(new ValidationError(
                        target.Field,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Use at most {SettingKeys.CurrencyDisplay.MaxDecimalPlaces} decimal places.")));
                    break;

                case CurrencyRateProblem.OutOfRange:
                    errors.Add(new ValidationError(
                        target.Field,
                        "Use a rate above 0 and no more than "
                        + SettingKeys.CurrencyDisplay.MaxRate.ToString("N0", CultureInfo.InvariantCulture)
                        + $". It is how many rupees one {target.Name} is worth."));
                    break;

                default:
                    errors.Add(new ValidationError(
                        target.Field,
                        $"Enter the {target.Name} rate as digits with an optional decimal "
                        + "point, such as 399.5: no signs, commas, spaces or letters. Leave it empty to stop "
                        + $"offering {target.Code}."));
                    break;
            }
        }

        if (errors.Count > 0)
        {
            return new SettingsWriteResult.Invalid(errors);
        }

        var rows = await store.ListAsync(
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.CurrencyDisplay.Group),
            cancellationToken);

        // Case-insensitively, to match the database collation; see SettingCatalog.ByKey for what Ordinal costs.
        var byKey = rows.ToDictionary(row => row.Key, StringComparer.OrdinalIgnoreCase);

        var missing = Targets
            .Where(target => !byKey.ContainsKey(target.SettingKey))
            .Select(target => target.SettingKey)
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
                    : "Somebody else changed the currency rates while you were editing them. Reload the page "
                      + "to see their change, then re-apply yours.");
        }

        var requested = Targets
            .Where(target => wanted.ContainsKey(target.SettingKey))
            .Select(target => byKey[target.SettingKey]);

        if (await CheckPermissionAsync(requested, command.Actor, cancellationToken) is { } refused)
        {
            return refused;
        }

        var changed = new List<string>();
        var audits = new List<AuditLog>();
        var now = clock.UtcNow.UtcDateTime;

        foreach (var target in Targets)
        {
            if (!wanted.TryGetValue(target.SettingKey, out var after))
            {
                continue;
            }

            // The audit trail's own diff, so "changed" and "has an audit row" are one question asked once.
            if (SettingsAuditTrail.ForValueChange(byKey[target.SettingKey], after, command.Actor, now) is not { } audit)
            {
                continue;
            }

            changed.Add(target.SettingKey);
            audits.Add(audit);

            var row = byKey[target.SettingKey];
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

        if (outcome == SettingsSaveOutcome.ConcurrencyConflict)
        {
            return new SettingsWriteResult.Conflict(
                SettingsConflictCodes.StaleRowVersion,
                "Somebody else changed the currency rates while your change was being saved, so nothing was "
                + "written. Reload the page and try again.");
        }

        // Only after a write that happened: a refused or conflicting save changed nothing the shop shows. The public
        // feed is cached for a minute, and without this a staff member who has just saved a rate would see the old
        // one on the shop for the rest of it (the site-settings feed is evicted the same way).
        cache.Evict();

        return new SettingsWriteResult.Succeeded(changed);
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
