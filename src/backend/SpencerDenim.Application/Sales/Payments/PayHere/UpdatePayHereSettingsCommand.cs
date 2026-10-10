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

namespace SpencerDenim.Application.Sales.Payments.PayHere;

/// <summary>
/// The PayHere merchant account and its two windows (doc 11 P8b; <c>Settings.Payments</c>).
/// </summary>
/// <param name="Fields">
/// The request's fields keyed by the name they carry in the body — <c>mode</c>, <c>merchantId</c>,
/// <c>merchantSecret</c>, <c>pendingMaxDays</c>, <c>expiryMinutes</c> — holding <b>only the fields the request
/// actually sent</b>. Absent means "I am not writing this key" and is not validated, not permission-checked
/// and not written: the lesson <see cref="SpencerDenim.Application.Sales.Payments.Admin.UpdateBankDetailsCommand"/>
/// records, and the one that matters most on this group, where a save that never mentioned the secret would
/// otherwise clear it.
/// </param>
/// <param name="RowVersion">The group's token, as the screen read it.</param>
/// <param name="Actor">Who is asking, for the audit rows, the alert and the per-row permission check.</param>
public sealed record UpdatePayHereSettingsCommand(
    IReadOnlyDictionary<string, string?> Fields,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// Validates and saves the <c>Payments.PayHere.*</c> keys, including the payment window: one audit
/// row per changed key (secret values logged as <c>[secret changed]</c>), one <c>SensitiveSettingsChanged</c>
/// alert per save, all in one transaction (doc 08 §3.8 DB2, N2).
/// <para>
/// Built on <c>UpdateBankDetailsCommandHandler</c>'s shape, down to the order of the checks —
/// <b>validate, concurrency, permission over the keys asked for, diff, save</b> — for the reasons its remarks
/// give. What differs is the secret:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Write-only.</b> A non-blank <c>merchantSecret</c> is encrypted (<see cref="ISettingSecretProtector"/>) and
/// stored; a blank or null one <b>keeps the stored value</b>, because the screen never receives the secret and
/// so cannot send it back. There is no way to clear it from this form: a shop that wants PayHere off switches
/// the provider off.
/// </description></item>
/// <item><description>
/// <b>Every save of a non-blank secret counts as a change</b>, because encrypting the same text twice gives
/// two ciphertexts. That is the right behaviour: someone typed a secret, and the audit trail and the alert
/// should say so.
/// </description></item>
/// </list>
/// <para>
/// <b>The alert is the banner doc 08 §3.8 N2 asks for, and the email is not built.</b> No
/// <c>SensitiveSettingsChanged</c> email template exists in this build; the banner, which an email failure
/// cannot clear, is the whole notification today. The bank form does not raise one at all yet (it predates
/// <c>ops.AdminAlerts</c>); PayHere does, because the table now exists and this group holds a credential.
/// </para>
/// </summary>
public sealed class UpdatePayHereSettingsCommandHandler(
    ISettingsStore store,
    IAuditLogWriter auditLog,
    IAdminAlertWriter alerts,
    ISettingSecretProtector protector,
    IEffectivePermissionsProvider permissionsProvider,
    IClock clock)
    : ICommandHandler<UpdatePayHereSettingsCommand, SettingsWriteResult>
{
    /// <summary>The longest merchant ID accepted. PayHere's are seven digits today.</summary>
    public const int MerchantIdMaxLength = 30;

    /// <summary>The longest merchant secret accepted. PayHere's are about fifty characters.</summary>
    public const int MerchantSecretMaxLength = 200;

    /// <summary>
    /// Every key this command may touch, paired with the request field it comes from. One list, so the
    /// validation, the write, the audit rows and the permission check cannot disagree about the set.
    /// </summary>
    private static readonly (string Key, string Field)[] Targets =
    [
        (SettingKeys.PayHere.Mode, "mode"),
        (SettingKeys.PayHere.MerchantId, "merchantId"),
        (SettingKeys.PayHere.MerchantSecret, "merchantSecret"),
        (SettingKeys.PayHere.PendingMaxDays, "pendingMaxDays"),
        (SettingKeys.PayHere.ExpiryMinutes, "expiryMinutes"),
    ];

    public async Task<SettingsWriteResult> HandleAsync(
        UpdatePayHereSettingsCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();
        var wanted = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, field) in Targets)
        {
            if (!command.Fields.TryGetValue(field, out var raw))
            {
                continue;
            }

            switch (key)
            {
                case SettingKeys.PayHere.Mode:
                    if (CleanMode(errors, field, raw) is { } mode)
                    {
                        wanted[key] = mode;
                    }

                    break;

                case SettingKeys.PayHere.MerchantId:
                    wanted[key] = CleanMerchantId(errors, field, raw);
                    break;

                case SettingKeys.PayHere.MerchantSecret:
                    // Blank keeps the stored value; only a typed secret is a write.
                    if (CleanSecret(errors, field, raw) is { } secret)
                    {
                        wanted[key] = protector.Protect(secret);
                    }

                    break;

                case SettingKeys.PayHere.PendingMaxDays:
                    if (CleanWhole(
                            errors,
                            field,
                            raw,
                            "days",
                            SettingKeys.PayHere.PendingMaxDaysMin,
                            SettingKeys.PayHere.PendingMaxDaysMax) is { } days)
                    {
                        wanted[key] = days;
                    }

                    break;

                default:
                    if (CleanWhole(
                            errors,
                            field,
                            raw,
                            "minutes",
                            SettingKeys.PayHere.ExpiryMinutesMin,
                            SettingKeys.PayHere.ExpiryMinutesMax) is { } minutes)
                    {
                        wanted[key] = minutes;
                    }

                    break;
            }
        }

        if (errors.Count > 0)
        {
            return new SettingsWriteResult.Invalid(errors);
        }

        var rows = await store.ListAsync(
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.PayHere.Group),
            cancellationToken);

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
                    : "Somebody else changed the PayHere settings while you were editing them. Reload the "
                      + "page to see their change, then re-apply yours.");
        }

        var requested = Targets
            .Where(target => wanted.ContainsKey(target.Key))
            .Select(target => byKey[target.Key]);

        if (await CheckPermissionAsync(requested, command.Actor, cancellationToken) is { } refused)
        {
            return refused;
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

            if (SettingsAuditTrail.ForValueChange(byKey[key], after, command.Actor, now) is not { } audit)
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

        // In the same transaction as the change, so a save that committed always has its banner and a banner
        // never describes a change that rolled back.
        alerts.Enlist(PayHereAlerts.SettingsChanged(changed, command.Actor, now));

        var outcome = await store.SaveAsync(command.Actor.UserId, cancellationToken);

        if (outcome == SettingsSaveOutcome.ConcurrencyConflict)
        {
            return new SettingsWriteResult.Conflict(
                SettingsConflictCodes.StaleRowVersion,
                "Somebody else changed the PayHere settings while your change was being saved, so nothing "
                + "was written. Reload the page and try again.");
        }

        // No cache to evict: PayHereSettingsReader reads the rows on every use, so a rotated secret stops
        // working at the next request.
        return new SettingsWriteResult.Succeeded(changed);
    }

    private static string? CleanMode(List<ValidationError> errors, string field, string? raw)
    {
        var mode = raw?.Trim().ToLowerInvariant();

        if (mode is SettingKeys.PayHere.ModeSandbox or SettingKeys.PayHere.ModeLive)
        {
            return mode;
        }

        errors.Add(new ValidationError(field, "Choose sandbox or live."));

        return null;
    }

    private static string? CleanMerchantId(List<ValidationError> errors, string field, string? raw)
    {
        var id = SettingFieldRules.Clean(raw);

        if (id is null)
        {
            return null;
        }

        if (id.Length > MerchantIdMaxLength || !id.All(character => char.IsAsciiLetterOrDigit(character)))
        {
            errors.Add(new ValidationError(
                field,
                $"The merchant ID is letters and digits only, up to {MerchantIdMaxLength} characters — "
                + "copy it from the PayHere portal."));
        }

        return id;
    }

    private static string? CleanSecret(List<ValidationError> errors, string field, string? raw)
    {
        var secret = SettingFieldRules.Clean(raw);

        if (secret is null)
        {
            return null;
        }

        if (secret.Length > MerchantSecretMaxLength || secret.Any(char.IsControl))
        {
            errors.Add(new ValidationError(
                field,
                $"The merchant secret is up to {MerchantSecretMaxLength} characters with no control "
                + "characters — copy it from the PayHere portal."));

            return null;
        }

        return secret;
    }

    private static string? CleanWhole(
        List<ValidationError> errors,
        string field,
        string? raw,
        string unit,
        int min,
        int max)
    {
        if (int.TryParse(raw?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value >= min
            && value <= max)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        errors.Add(new ValidationError(
            field,
            string.Create(CultureInfo.InvariantCulture, $"Enter a whole number of {unit} between {min} and {max}.")));

        return null;
    }

    /// <summary>
    /// Doc 08 §3.8 DB2, on the write path: each row carries the permission needed to change it. The same
    /// shape, and for the same reason, as <c>UpdateBankDetailsCommandHandler.CheckPermissionAsync</c> —
    /// permissions come from the user's roles through <see cref="IEffectivePermissionsProvider"/>, never
    /// from claims on the principal (doc 08 §3.1 R4).
    /// </summary>
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
