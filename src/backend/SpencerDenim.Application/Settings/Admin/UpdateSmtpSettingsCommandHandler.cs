using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Settings.Admin;

/// <summary>
/// The mail account (doc 08 §3.8 <c>Email.Smtp</c>; <c>Settings.Security</c>, Super Admin only).
/// </summary>
/// <param name="Fields">
/// The text fields the request carried, keyed by body name (<see cref="UpdateSmtpSettingsCommandHandler.TextFields"/>):
/// <b>only the fields actually sent</b>. Absent means "not writing this key"; null or blank means "clear it" —
/// except <c>password</c>, which is write-only, so blank keeps the saved one (the PayPal secret's rule).
/// </param>
/// <param name="UseStartTls">The encryption switch, or null when the body left it out.</param>
/// <param name="RowVersion">The group's token, as the screen read it.</param>
/// <param name="Actor">Who is asking, for the audit rows, the permission check and the alert.</param>
public sealed record UpdateSmtpSettingsCommand(
    IReadOnlyDictionary<string, string?> Fields,
    bool? UseStartTls,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// Validates and saves the seven <c>Email.Smtp.*</c> keys, with <b>one audit row per changed key</b> (the password as
/// <c>[secret changed]</c>), the <c>SensitiveSettingsChanged</c> banner, and the warning email to every Super Admin
/// through <b>both</b> the new and the previous account (doc 08 §3.8 N2) — all in one transaction.
/// <para>
/// <b>Order of checks: fields, concurrency, permission over the keys asked to write, the cross-field rules, diff,
/// save</b> — <c>UpdatePayPalSettingsCommandHandler</c>'s order, with the cross-field step after permission (as the
/// Traffic handler does) because it reads stored values a refused caller must learn nothing about.
/// </para>
/// <para>
/// <b>The cross-field rules are what make a saved account usable</b>: a server needs a port and a send-from address,
/// and a user name (so a password) is refused with encryption off, because the password would cross the network in
/// the clear. They are checked on the values the rows will hold <i>after</i> the save. Clearing the server is always
/// allowed: it hands sending back to <c>Email__Smtp__*</c> (<see cref="SmtpSettingsResolver"/>).
/// </para>
/// <para>
/// <b>The previous account is read before anything is changed</b> (<see cref="ISmtpSettingsReader"/>), so the old-channel
/// copy goes through the account the Super Admins were really receiving mail through — the rows if they were in use,
/// otherwise configuration.
/// </para>
/// </summary>
public sealed class UpdateSmtpSettingsCommandHandler(
    ISettingsStore store,
    ISettingSecretProtector secrets,
    ISmtpSettingsReader smtpReader,
    ISensitiveSettingsChangeNotifier notifier,
    IAuditLogWriter auditLog,
    IEffectivePermissionsProvider permissionsProvider,
    IClock clock)
    : ICommandHandler<UpdateSmtpSettingsCommand, SettingsWriteResult>
{
    public const string HostField = "host";
    public const string PortField = "port";
    public const string UsernameField = "username";
    public const string PasswordField = "password";
    public const string FromAddressField = "fromAddress";
    public const string FromNameField = "fromName";
    public const string UseStartTlsField = "useStartTls";

    /// <summary>What the banner and the email call this area.</summary>
    public const string AlertArea = "Email account settings";

    /// <summary>The text fields as the body names them, and the keys they write. The switch is read separately.</summary>
    private static readonly (string Key, string Field)[] TextTargets =
    [
        (SettingKeys.Smtp.Host, HostField),
        (SettingKeys.Smtp.Port, PortField),
        (SettingKeys.Smtp.Username, UsernameField),
        (SettingKeys.Smtp.Password, PasswordField),
        (SettingKeys.Smtp.FromAddress, FromAddressField),
        (SettingKeys.Smtp.FromName, FromNameField),
    ];

    /// <summary>The text field names, for the endpoint's reader and the shape test.</summary>
    public static IReadOnlyList<string> TextFields { get; } = TextTargets.Select(target => target.Field).ToList();

    private static readonly string[] AllKeys = [.. SettingCatalog.KeysIn(SettingKeys.Smtp.Group)];

    public async Task<SettingsWriteResult> HandleAsync(
        UpdateSmtpSettingsCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();
        var wanted = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? newPassword = null;

        foreach (var (key, field) in TextTargets)
        {
            if (!command.Fields.TryGetValue(field, out var raw))
            {
                continue;
            }

            if (key == SettingKeys.Smtp.Password)
            {
                newPassword = CleanPassword(errors, raw);
                continue;
            }

            wanted[key] = CleanField(errors, key, field, raw);
        }

        if (command.UseStartTls is { } useStartTls)
        {
            wanted[SettingKeys.Smtp.UseStartTls] = useStartTls ? "true" : "false";
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

        var groupRows = SettingCatalog.KeysInSaveUnitFor(SettingKeys.Smtp.Group).Select(key => byKey[key]).ToList();

        if (!SettingsGroupVersion.Matches(groupRows, command.RowVersion))
        {
            return new SettingsWriteResult.Conflict(
                SettingsConflictCodes.StaleRowVersion,
                SettingFieldRules.ReadRowVersion(command.RowVersion) is null
                    ? "This request did not carry the settings' current version, so it cannot be applied safely. "
                      + "Reload the page and try again."
                    : "Somebody else changed the email account while you were editing it. Reload the page to see "
                      + "their change, then re-apply yours.");
        }

        var asked = wanted.Keys.ToList();

        if (newPassword is not null)
        {
            asked.Add(SettingKeys.Smtp.Password);
        }

        if (await CheckPermissionAsync(asked.Select(key => byKey[key]), command.Actor, cancellationToken)
            is { } refused)
        {
            return refused;
        }

        // The values the rows will hold after the save: new where sent, stored where not.
        string? After(string key) => wanted.TryGetValue(key, out var value) ? value : byKey[key].Value;

        if (CrossFieldErrors(After) is { Count: > 0 } crossField)
        {
            return new SettingsWriteResult.Invalid(crossField);
        }

        var finalValues = new Dictionary<string, string?>(wanted, StringComparer.OrdinalIgnoreCase);

        if (newPassword is not null && !SamePassword(byKey[SettingKeys.Smtp.Password], newPassword))
        {
            finalValues[SettingKeys.Smtp.Password] = secrets.Protect(newPassword);
        }

        var now = clock.UtcNow.UtcDateTime;
        var changed = new List<string>();
        var audits = new List<AuditLog>();

        // Catalogue order, so the audit rows, the banner and the email list the keys the way the form shows them.
        foreach (var key in AllKeys)
        {
            if (!finalValues.TryGetValue(key, out var after))
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

        // Before any row is touched: the channel the Super Admins were receiving mail through until now.
        var before = await smtpReader.ReadEffectiveAsync(cancellationToken);

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

        await notifier.EnlistAsync(
            new SensitiveSettingsChange(
                AlertArea,
                changed,
                command.Actor,
                now,
                MailAccountChanged: true,
                PreviousMailAccount: before.Settings),
            cancellationToken);

        var outcome = await store.SaveAsync(command.Actor.UserId, cancellationToken);

        if (outcome == SettingsSaveOutcome.ConcurrencyConflict)
        {
            return new SettingsWriteResult.Conflict(
                SettingsConflictCodes.StaleRowVersion,
                "Somebody else changed the email account while your change was being saved, so nothing was "
                + "written. Reload the page and try again.");
        }

        // No cache to evict: SendEmails reads the account each sweep (ISmtpSettingsReader).
        return new SettingsWriteResult.Succeeded(changed);
    }

    /// <summary>
    /// The rules that span fields, on the values after the save. Public so the test-send path applies the same ones.
    /// </summary>
    internal static List<ValidationError> CrossFieldErrors(Func<string, string?> after)
    {
        var errors = new List<ValidationError>();

        var host = SmtpSettingRules.Clean(after(SettingKeys.Smtp.Host));

        if (host is not null)
        {
            if (SmtpSettingRules.ParsePort(after(SettingKeys.Smtp.Port)) is null)
            {
                errors.Add(new ValidationError(PortField, "Enter the mail server's port, usually 587."));
            }

            if (!SmtpSettingRules.IsValidAddress(SmtpSettingRules.Clean(after(SettingKeys.Smtp.FromAddress))))
            {
                errors.Add(new ValidationError(
                    FromAddressField,
                    "Enter the address emails are sent from. A mail server cannot be used without one."));
            }
        }

        var username = SmtpSettingRules.Clean(after(SettingKeys.Smtp.Username));
        var startTls = SmtpSettingRules.ParseBool(after(SettingKeys.Smtp.UseStartTls)) ?? true;

        if (username is not null && !startTls)
        {
            errors.Add(new ValidationError(
                UseStartTlsField,
                "Keep encryption on when a user name is set. Without it the password would be sent unencrypted."));
        }

        return errors;
    }

    /// <summary>One text field's own rules. Returns the value to store (trimmed, null for blank).</summary>
    internal static string? CleanField(List<ValidationError> errors, string key, string field, string? raw)
    {
        var value = SmtpSettingRules.Clean(raw);

        if (value is null)
        {
            return null;
        }

        switch (key)
        {
            case SettingKeys.Smtp.Host when !SmtpSettingRules.IsValidHost(value):
                errors.Add(new ValidationError(
                    field,
                    "Enter the server name only, for example the one your provider gives for SMTP — no https://, "
                    + "no port and no spaces."));
                break;

            case SettingKeys.Smtp.Port when SmtpSettingRules.ParsePort(value) is null:
                errors.Add(new ValidationError(field, "Enter the port as a whole number between 1 and 65535."));
                break;

            case SettingKeys.Smtp.Port:
                // Normalised, so "0587" and "587" are one value and the diff does not see a change.
                return SmtpSettingRules.ParsePort(value)!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

            case SettingKeys.Smtp.Username
                when !SmtpSettingRules.IsPlainLine(value, SettingKeys.Smtp.UsernameMaxLength):
                errors.Add(new ValidationError(
                    field,
                    $"Keep the user name to {SettingKeys.Smtp.UsernameMaxLength} characters, on one line."));
                break;

            case SettingKeys.Smtp.FromAddress when !SmtpSettingRules.IsValidAddress(value):
                errors.Add(new ValidationError(field, "Enter one email address, like name@yourshop.lk, with no name around it."));
                break;

            case SettingKeys.Smtp.FromName
                when !SmtpSettingRules.IsPlainLine(value, SettingKeys.Smtp.FromNameMaxLength):
                errors.Add(new ValidationError(
                    field,
                    $"Keep the name to {SettingKeys.Smtp.FromNameMaxLength} characters, on one line."));
                break;
        }

        return value;
    }

    /// <summary>
    /// The password as typed — <b>not trimmed</b>, because a space can be part of a password — or null for blank,
    /// which keeps the saved one.
    /// </summary>
    internal static string? CleanPassword(List<ValidationError> errors, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!SmtpSettingRules.IsPlainLine(raw, SettingKeys.Smtp.PasswordMaxLength))
        {
            errors.Add(new ValidationError(
                PasswordField,
                $"Keep the password to {SettingKeys.Smtp.PasswordMaxLength} characters, on one line."));
            return null;
        }

        return raw;
    }

    /// <summary>Decided on the plaintext: ciphertext differs on every <c>Protect</c>.</summary>
    private bool SamePassword(Setting row, string plaintext) =>
        secrets.TryUnprotect(row.Value, out var existing)
        && string.Equals(existing, plaintext, StringComparison.Ordinal);

    /// <summary>Each row carries the permission needed to change it (doc 08 §3.8 DB2).</summary>
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
