using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Sales.Payments.Admin;

/// <summary>
/// The bank account customers transfer to (PAY-01, doc 11 P7's preconditions; <c>Settings.Payments</c>).
/// </summary>
/// <param name="Fields">
/// The request's fields, keyed by the name they carry in the body, holding <b>only the fields the request
/// actually sent</b>.
/// <para>
/// <b>A dictionary rather than five <c>string?</c> properties, for the reason ADM-22's round 1 found and
/// this group makes live.</b> PUT replaces, so reading a missing field as <see langword="null"/> clears the
/// key — W1-12's Blocker shape, where a save that never mentioned three image columns wiped them on every
/// save while 1,571 tests round-tripped null to null. ADM-22 fixed it for contact and social and wrote down
/// that the fix "bites the moment #53 adds a role that cannot read every group". <b>This is that
/// moment</b>: these five keys require <c>Settings.Payments</c>, which no default role holds, so a Store
/// Manager's settings screen genuinely does not receive them — and if absent meant null, their first save
/// of anything would wipe the shop's bank account number and every order placed afterwards would show a
/// customer no account to pay into.
/// </para>
/// <para>
/// So absent and null are different: absent means "I am not writing this key", null means "clear it".
/// </para>
/// </param>
/// <param name="RowVersion">The group's token, as the screen read it.</param>
/// <param name="Actor">Who is asking, for the audit rows and the per-row permission check.</param>
public sealed record UpdateBankDetailsCommand(
    IReadOnlyDictionary<string, string?> Fields,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// Validates and saves the <c>Bank.*</c> keys, writing <b>one audit row per changed key</b> (doc 08 §3.8).
/// <para>
/// Built on <c>UpdateContactAndSocialCommandHandler</c>'s shape deliberately, down to the order of the
/// checks, because that order was established by a tester and each step of it is load-bearing:
/// <b>validate, then concurrency, then permission over the keys the caller asked to write, then diff, then
/// save, then evict</b>. Two of those are not obvious and are worth repeating here rather than being
/// rediscovered:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Concurrency before permission.</b> Changing a row's <c>RequiredPermission</c> is itself an UPDATE
/// that moves the rowversion, so a stale form must be told to reload before it is told about permissions —
/// otherwise a 403 arrives for a request that would have been allowed after a refresh.
/// </description></item>
/// <item><description>
/// <b>Permission over the keys the caller <i>asked</i> to write, not the ones that turned out to
/// differ.</b> Checking the diff makes the endpoint a one-bit oracle on a value the caller may not read:
/// submit a guess equal to the stored value and the key never enters the diff, so the answer is 204;
/// submit one that differs and it is 403. One guess, one bit, repeatable — and on this group the secret
/// being probed is the shop's bank account number.
/// </description></item>
/// </list>
/// <para>
/// <b>What this does not do, and it is specified.</b> Doc 08 §3.8 DB2 requires any change to a
/// <c>Bank.*</c> key to insert an <c>ops.AdminAlerts</c> <c>SensitiveSettingsChanged</c> row and email
/// every active GrantsAll holder, in the same transaction. Neither <c>ops.AdminAlerts</c> nor
/// <c>ops.EmailOutbox</c> exists in any migration, so neither can be written and this task does not create
/// another milestone's tables. The <b>audit rows are written</b>, so the change is not invisible — it is
/// in the audit log with the actor, the time, the IP and the before and after values — but a Super Admin is
/// not told, and doc 11 P7's last exception ("Bank details changed by a Super Admin → all Super Admins
/// receive a security email and a dashboard banner") is therefore not honoured yet. It is in the task
/// report as owed.
/// </para>
/// </summary>
public sealed class UpdateBankDetailsCommandHandler(
    ISettingsStore store,
    IAuditLogWriter auditLog,
    IEffectivePermissionsProvider permissionsProvider,
    IClock clock)
    : ICommandHandler<UpdateBankDetailsCommand, SettingsWriteResult>
{
    /// <summary>
    /// Every key this command may touch, paired with the request field it comes from. One list, so the
    /// validation, the write, the audit rows and the permission check cannot disagree about the set.
    /// </summary>
    private static readonly (string Key, string Field)[] Targets =
    [
        (SettingKeys.Bank.BankName, "bankName"),
        (SettingKeys.Bank.Branch, "branch"),
        (SettingKeys.Bank.AccountName, "accountName"),
        (SettingKeys.Bank.AccountNumber, "accountNumber"),
        (SettingKeys.Bank.Instructions, "instructions"),
    ];

    public async Task<SettingsWriteResult> HandleAsync(
        UpdateBankDetailsCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();
        var wanted = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, field) in Targets)
        {
            // Only the fields the request carried. An absent field is not validated, not permission
            // checked and not written — it is simply not part of this save.
            if (!command.Fields.TryGetValue(field, out var raw))
            {
                continue;
            }

            wanted[key] = key == SettingKeys.Bank.AccountNumber
                ? BankDetailRules.CleanAccountNumber(errors, field, raw)
                : BankDetailRules.CleanText(errors, field, raw, FieldLabel(field), LengthOf(key));
        }

        if (errors.Count > 0)
        {
            return new SettingsWriteResult.Invalid(errors);
        }

        var rows = await store.ListAsync(
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.Bank.Group),
            cancellationToken);

        // Case-insensitively, to match the database collation. SettingCatalog.ByKey records what an
        // Ordinal dictionary here costs: a row scripted in with different casing disappears from the
        // screen while every save answers NotSeeded, which is a 500, for ever.
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
                    : "Somebody else changed the bank details while you were editing them. Reload the page "
                      + "to see their change, then re-apply yours.");
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

            var row = byKey[key];

            // The diff is the audit trail's own, so "changed" and "has an audit row" are the same question
            // asked once. An untouched row keeps its UpdatedAtUtc, which is what makes that column mean
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
                "Somebody else changed the bank details while your change was being saved, so nothing was "
                + "written. Reload the page and try again.");
        }

        // **No cache to evict.** BankTransferSettingsReader reads the rows on every order page on purpose
        // (see its remarks): a client who corrects a mistyped account number must not have the old one
        // served to shoppers for another minute.
        return new SettingsWriteResult.Succeeded(changed);
    }

    /// <summary>
    /// Doc 08 §3.8 DB2, on the write path: each row carries the permission needed to change it, and this is
    /// where that column is read.
    /// </summary>
    /// <remarks>
    /// Identical in shape to <c>UpdateContactAndSocialCommandHandler.CheckPermissionAsync</c>, and the
    /// duplication is deliberate rather than overlooked: the two handlers are the only callers, the check
    /// is eight lines, and a shared helper would put the one control that decides who may change a bank
    /// account number behind an indirection where a future caller could forget to use it. Permissions come
    /// from the user's roles through <see cref="IEffectivePermissionsProvider"/>, never from claims on the
    /// principal (doc 08 §3.1 R4): a cookie carries what was true when it was issued, and a staff member
    /// demoted five minutes ago must not still be writing bank details out of their own cookie.
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

    private static string FieldLabel(string field) => field switch
    {
        "bankName" => "bank name",
        "branch" => "branch",
        "accountName" => "account name",
        _ => "instructions",
    };

    /// <summary>
    /// How long each value may be.
    /// <para>
    /// <c>ops.Settings.Value</c> is <c>nvarchar(max)</c>, which is not a limit anybody should rely on — the
    /// point <c>SettingFieldRules.UrlMaxLength</c> makes about an unbounded field reachable from a form.
    /// These are the lengths of the things they describe: a bank's name and a branch fit comfortably in a
    /// line, an account name is a legal name, and the instructions are a short paragraph a customer reads
    /// on the order page rather than an article.
    /// </para>
    /// </summary>
    private static int LengthOf(string key) => key == SettingKeys.Bank.Instructions ? 600 : 120;
}
