using System.Globalization;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Sales.Payments.PayPal.Admin;

/// <summary>
/// PayPal's credentials, rate and timings (PAY-02; <c>Settings.Payments</c>).
/// </summary>
/// <param name="Fields">
/// The request's fields, keyed by the name they carry in the body, holding <b>only the fields the request
/// actually sent</b> — absent means "I am not writing this key", null or blank means "clear it". The reason is
/// the one <see cref="Sales.Payments.Admin.UpdateBankDetailsCommand"/> records at length, and it bites harder
/// here: a screen whose caller cannot read this group never receives these fields and never sends them, and a
/// binding that turned that silence into null would clear the shop's PayPal credentials.
/// <para>
/// <b><c>clientSecret</c> is the exception to "blank clears".</b> A secret is write-only: the screen is never
/// told it, so it cannot send it back, and an empty box therefore has to mean "keep what is saved".
/// </para>
/// </param>
/// <param name="ConfirmRate">
/// True to re-stamp <c>RateUpdatedAtUtc</c> without changing the rate: "yes, today's rate is still this one".
/// Without it the stamp moves only when the rate does, so saving some other field never makes a stale rate
/// look fresh.
/// </param>
/// <param name="RowVersion">The group's token, as the screen read it.</param>
/// <param name="Actor">Who is asking, for the audit rows and the per-row permission check.</param>
public sealed record UpdatePayPalSettingsCommand(
    IReadOnlyDictionary<string, string?> Fields,
    bool ConfirmRate,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// Validates and saves the <c>Payments.PayPal.*</c> keys and <c>Orders.PayPalExpiryMinutes</c>, writing
/// <b>one audit row per changed key</b> (doc 08 §3.8) and the <c>SensitiveSettingsChanged</c> alert (DB2) in
/// the same transaction.
/// <para>
/// The shape and the order of the checks are <c>UpdateBankDetailsCommandHandler</c>'s, because a tester
/// established that order there and each step is load-bearing: <b>validate, then concurrency, then
/// permission over the keys the caller asked to write, then diff, then save</b>. See that class for why
/// concurrency precedes permission and why permission is checked over what was <i>asked</i> and not over
/// what turned out to differ (the second is a one-bit oracle on a value the caller may not read).
/// </para>
/// <para>
/// <b>The client secret.</b> It is stored as Data Protection ciphertext (<see cref="ISettingSecretProtector"/>),
/// logged in the audit trail as <c>[secret changed]</c> by the trail's own rule, and never returned by any
/// read. A blank or absent <c>clientSecret</c> keeps the stored one; a value equal to the stored secret is not
/// a change (ciphertext differs on every <c>Protect</c>, so equality is decided on the plaintext).
/// </para>
/// <para>
/// <b>The rate's stamp.</b> <c>Payments.PayPal.RateUpdatedAtUtc</c> is written here and nowhere else: when the
/// rate changes, or when the request says <c>confirmRate</c>. No request can set it directly, so a stale rate
/// cannot be made to look fresh by typing a date.
/// </para>
/// </summary>
public sealed class UpdatePayPalSettingsCommandHandler(
    ISettingsStore store,
    ISettingSecretProtector secrets,
    IAuditLogWriter auditLog,
    IAdminAlertWriter alerts,
    IEffectivePermissionsProvider permissionsProvider,
    IClock clock)
    : ICommandHandler<UpdatePayPalSettingsCommand, SettingsWriteResult>
{
    /// <summary>The request fields, as the body names them, and the keys they write.</summary>
    private static readonly (string Key, string Field)[] Targets =
    [
        (SettingKeys.PayPal.Mode, "mode"),
        (SettingKeys.PayPal.ClientId, "clientId"),
        (SettingKeys.PayPal.ClientSecret, "clientSecret"),
        (SettingKeys.PayPal.WebhookId, "webhookId"),
        (SettingKeys.PayPal.LkrPerUsd, "lkrPerUsd"),
        (SettingKeys.PayPal.FxBufferPercent, "fxBufferPercent"),
        (SettingKeys.PayPal.RateMaxAgeDays, "rateMaxAgeDays"),
        (SettingKeys.PayPal.CaptureLockHours, "captureLockHours"),
        (SettingKeys.PayPal.CaptureRecoveryMaxHours, "captureRecoveryMaxHours"),
        (SettingKeys.PayPal.PendingCaptureMaxDays, "pendingCaptureMaxDays"),
        (SettingKeys.PayPal.ExpiryMinutes, "expiryMinutes"),
    ];

    /// <summary>The request field names, for the endpoint's reader and the shape test.</summary>
    public static IReadOnlyList<string> FieldNames { get; } = Targets.Select(target => target.Field).ToList();

    /// <summary>Every key this command may write — its targets, plus the stamp it alone sets.</summary>
    private static readonly string[] AllKeys =
        [.. Targets.Select(target => target.Key), SettingKeys.PayPal.RateUpdatedAtUtc];

    public async Task<SettingsWriteResult> HandleAsync(
        UpdatePayPalSettingsCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();
        var wanted = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? newSecret = null;

        foreach (var (key, field) in Targets)
        {
            // Only the fields the request carried: an absent one is not validated, not permission checked and
            // not written.
            if (!command.Fields.TryGetValue(field, out var raw))
            {
                continue;
            }

            if (key == SettingKeys.PayPal.ClientSecret)
            {
                // Blank keeps the stored secret — see the command's remarks.
                newSecret = PayPalSettingRules.CleanSecret(errors, field, raw);
                continue;
            }

            wanted[key] = PayPalSettingRules.Clean(errors, key, field, raw);
        }

        if (errors.Count > 0)
        {
            return new SettingsWriteResult.Invalid(errors);
        }

        var rows = await store.ListAsync(AllKeys, cancellationToken);
        var byKey = rows.ToDictionary(row => row.Key, StringComparer.OrdinalIgnoreCase);

        var missing = AllKeys.Where(key => !byKey.ContainsKey(key)).ToList();

        if (missing.Count > 0)
        {
            return new SettingsWriteResult.NotSeeded(
                $"These settings do not exist in this database: {string.Join(", ", missing)}. "
                + "Run the settings seeder before using this screen.");
        }

        // The token covers the PayPal group's rows, which is what the read model published for them.
        var groupRows = SettingCatalog.KeysInSaveUnitFor(SettingKeys.PayPal.Group)
            .Select(key => byKey[key])
            .ToList();

        if (!SettingsGroupVersion.Matches(groupRows, command.RowVersion))
        {
            return new SettingsWriteResult.Conflict(
                SettingsConflictCodes.StaleRowVersion,
                SettingFieldRules.ReadRowVersion(command.RowVersion) is null
                    ? "This request did not carry the settings' current version, so it cannot be applied "
                      + "safely. Reload the page and try again."
                    : "Somebody else changed the PayPal settings while you were editing them. Reload the page "
                      + "to see their change, then re-apply yours.");
        }

        var asked = wanted.Keys.ToList();

        if (newSecret is not null)
        {
            asked.Add(SettingKeys.PayPal.ClientSecret);
        }

        if (command.ConfirmRate)
        {
            asked.Add(SettingKeys.PayPal.RateUpdatedAtUtc);
        }

        if (await CheckPermissionAsync(asked.Select(key => byKey[key]), command.Actor, cancellationToken)
            is { } refused)
        {
            return refused;
        }

        var now = clock.UtcNow.UtcDateTime;
        var changed = new List<string>();
        var audits = new List<AuditLog>();
        var finalValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, _) in Targets)
        {
            if (key == SettingKeys.PayPal.ClientSecret)
            {
                if (newSecret is not null && !SameSecret(byKey[key], newSecret))
                {
                    finalValues[key] = secrets.Protect(newSecret);
                }

                continue;
            }

            if (wanted.TryGetValue(key, out var after))
            {
                finalValues[key] = after;
            }
        }

        // The rate's stamp: when the rate moves, or when the caller confirms it. Never from the body.
        var rateKey = SettingKeys.PayPal.LkrPerUsd;
        var rateRequested = wanted.TryGetValue(rateKey, out var rateRequest);
        var rateAfter = rateRequested ? rateRequest : byKey[rateKey].Value;
        var rateChanged = rateRequested
            && !string.Equals(rateRequest, byKey[rateKey].Value, StringComparison.Ordinal);

        if (command.ConfirmRate && string.IsNullOrWhiteSpace(rateAfter))
        {
            return new SettingsWriteResult.Invalid(
                "lkrPerUsd",
                "There is no rate to confirm. Enter today's rate first.");
        }

        if ((rateChanged || command.ConfirmRate) && !string.IsNullOrWhiteSpace(rateAfter))
        {
            finalValues[SettingKeys.PayPal.RateUpdatedAtUtc] = now.ToString("O", CultureInfo.InvariantCulture);
        }

        foreach (var (key, after) in finalValues)
        {
            var row = byKey[key];

            // The diff is the audit trail's own, so "changed" and "has an audit row" are the same question
            // asked once. An untouched row keeps its UpdatedAtUtc.
            if (SettingsAuditTrail.ForValueChange(row, after, command.Actor, now) is not { } audit)
            {
                continue;
            }

            changed.Add(row.Key);
            audits.Add(audit);
        }

        if (changed.Count == 0)
        {
            return new SettingsWriteResult.Unchanged();
        }

        foreach (var key in changed)
        {
            var row = byKey[key];
            row.Value = finalValues[key];
            row.UpdatedAtUtc = now;
            row.UpdatedByUserId = command.Actor.UserId;
        }

        foreach (var audit in audits)
        {
            auditLog.Enlist(audit);
        }

        // Doc 08 §3.8 DB2: a payment setting changed. Key names only, in the same transaction.
        alerts.Enlist(SensitiveSettingsChangedAlert.For(
            "PayPal payment settings",
            changed,
            command.Actor.UserId,
            now));

        var outcome = await store.SaveAsync(command.Actor.UserId, cancellationToken);

        if (outcome == SettingsSaveOutcome.ConcurrencyConflict)
        {
            return new SettingsWriteResult.Conflict(
                SettingsConflictCodes.StaleRowVersion,
                "Somebody else changed the PayPal settings while your change was being saved, so nothing was "
                + "written. Reload the page and try again.");
        }

        // **No cache to evict.** The PayPal settings reader reads the rows on every use on purpose: a Super
        // Admin who switches to live or corrects a credential must not have the old one used for a minute.
        return new SettingsWriteResult.Succeeded(changed);
    }

    /// <summary>
    /// Whether <paramref name="plaintext"/> is the secret already stored. Decided on the plaintext, because
    /// Data Protection's ciphertext differs on every call.
    /// </summary>
    private bool SameSecret(Setting row, string plaintext) =>
        secrets.TryUnprotect(row.Value, out var existing)
        && string.Equals(existing, plaintext, StringComparison.Ordinal);

    /// <summary>Doc 08 §3.8 DB2 on the write path: each row carries the permission needed to change it.</summary>
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
