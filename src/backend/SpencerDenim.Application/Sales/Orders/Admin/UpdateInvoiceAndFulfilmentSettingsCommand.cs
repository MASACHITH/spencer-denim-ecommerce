using System.Globalization;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Sales.Payments;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Sales.Orders.Admin;

/// <summary>
/// The company's details as they appear on an invoice, and how long a dispatch waits before the system
/// closes it (ADM-09 / ADM-22, doc 08 §3.8 groups <c>Store</c> and <c>Orders</c>; <c>Settings.Edit</c>).
/// </summary>
/// <remarks>
/// <b>Why this lands with ADM-09 rather than waiting for ADM-22.</b> Doc 02 ADM-09's deliverable is "print
/// invoice", and an invoice is a legal document: without these four keys it prints with no registered name,
/// no address, no BR number and no VAT number, and until this handler existed there was <b>no screen at
/// all</b> that could fill them in — not even a read-only one, because the settings screen renders named
/// forms rather than a generic key list. A deliverable that cannot be completed short of SQL is not a
/// deliverable, so the form ships with the thing that needs it.
/// </remarks>
/// <param name="Fields">
/// The request's fields, keyed by the name they carry in the body, holding <b>only the fields the request
/// actually sent</b>.
/// <para>
/// <b>A dictionary rather than five properties, for the reason ADM-22 and W1-12 both paid for.</b> PUT
/// replaces, so reading a missing field as <see langword="null"/> clears the key — the shape that wiped
/// every category image behind 1,571 passing tests. Absent means "I am not writing this key"; null means
/// "clear it". The endpoint reads presence off the JSON and the screen sends every box it was given.
/// </para>
/// </param>
/// <param name="RowVersion">The save unit's token, as the screen read it.</param>
/// <param name="Actor">Who is asking, for the audit rows and the per-row permission check.</param>
public sealed record UpdateInvoiceAndFulfilmentSettingsCommand(
    IReadOnlyDictionary<string, string?> Fields,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// Validates and saves the four <c>Store.*</c> legal keys and <c>Orders.AutoDeliverAfterDays</c>, writing
/// <b>one audit row per changed key</b> (doc 08 §3.8).
/// </summary>
/// <remarks>
/// Built on <c>UpdateBankDetailsCommandHandler</c>'s shape down to the order of the checks, because a
/// tester established that order and each step is load-bearing: <b>validate, then concurrency, then
/// permission over the keys the caller asked to write, then diff, then save</b>.
/// <list type="bullet">
/// <item><description>
/// <b>Concurrency before permission</b>, because changing a row's <c>RequiredPermission</c> is itself an
/// UPDATE that moves the rowversion: a stale form must be told to reload before it is told about
/// permissions.
/// </description></item>
/// <item><description>
/// <b>Permission over the keys the caller <i>asked</i> to write, never the ones that turned out to
/// differ.</b> Checking the diff makes the endpoint a one-bit oracle on a value the caller may not read:
/// a guess equal to the stored value never enters the diff and answers 204, a guess that differs answers
/// 403. One guess, one bit, repeatable.
/// </description></item>
/// </list>
/// <para>
/// <b>These five keys are <c>Settings.Edit</c>, not <c>Settings.Payments</c></b> (doc 08 §3.8 reserves that
/// for <c>Bank.*</c> and <c>Payments.*</c>), so the per-row check will pass for every caller who reached
/// the form. It is here anyway, reading the row's own column, because that column is the control and a
/// handler that trusted the group name would be the place a future permission change failed to take.
/// </para>
/// <para>
/// <b>No cache to evict.</b> <c>OrderSettingsReader</c> reads these rows when a staff member prints an
/// invoice and once a day in the auto-deliver job, and deliberately does not cache: a client who corrects
/// a mistyped registration number must not have the old one printed for another minute.
/// </para>
/// </remarks>
public sealed class UpdateInvoiceAndFulfilmentSettingsCommandHandler(
    ISettingsStore store,
    IAuditLogWriter auditLog,
    IEffectivePermissionsProvider permissionsProvider,
    IClock clock)
    : ICommandHandler<UpdateInvoiceAndFulfilmentSettingsCommand, SettingsWriteResult>
{
    /// <summary>
    /// Every key this command may touch, paired with the request field it comes from. One list, so the
    /// validation, the write, the audit rows and the permission check cannot disagree about the set.
    /// </summary>
    private static readonly (string Key, string Field)[] Targets =
    [
        (SettingKeys.Store.LegalName, "legalName"),
        (SettingKeys.Store.Address, "registeredAddress"),
        (SettingKeys.Store.BRNumber, "brNumber"),
        (SettingKeys.Store.VatNumber, "vatNumber"),
        (SettingKeys.Orders.AutoDeliverAfterDays, "autoDeliverAfterDays"),
    ];

    /// <summary>
    /// A registered company name. Longer than a bank's name, because a registered name carries its legal
    /// form and sometimes a former name in brackets.
    /// </summary>
    public const int LegalNameMaxLength = 200;

    /// <summary>The registered address, as a short multi-line block rather than a paragraph.</summary>
    public const int AddressMaxLength = 400;

    /// <summary>A registration number. Wide enough for any format with separators; narrow enough not to be a sentence.</summary>
    public const int RegistrationNumberMaxLength = 40;

    public async Task<SettingsWriteResult> HandleAsync(
        UpdateInvoiceAndFulfilmentSettingsCommand command,
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

            wanted[key] = key == SettingKeys.Orders.AutoDeliverAfterDays
                ? CleanAutoDeliverDays(errors, field, raw)
                : BankDetailRules.CleanText(errors, field, raw, FieldLabel(field), LengthOf(key));
        }

        if (errors.Count > 0)
        {
            return new SettingsWriteResult.Invalid(errors);
        }

        // The whole save unit, which is `Store` and `Orders` together (SettingCatalog.SaveUnits) — so the
        // token this compares is by construction the token the screen was handed. Publishing a per-group
        // token and comparing a per-form one is the mismatch that made every valid ADM-22 save a 409.
        var rows = await store.ListAsync(
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.Store.Group),
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
                    : "Somebody else changed the shop's details while you were editing them. Reload the "
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

            // The diff is the audit trail's own, so "changed" and "has an audit row" are the same question
            // asked once. An untouched row keeps its UpdatedAtUtc, which is what makes that column mean
            // "when this value last changed" rather than "when somebody last pressed Save".
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

        var outcome = await store.SaveAsync(command.Actor.UserId, cancellationToken);

        return outcome == SettingsSaveOutcome.ConcurrencyConflict
            ? new SettingsWriteResult.Conflict(
                SettingsConflictCodes.StaleRowVersion,
                "Somebody else changed the shop's details while your change was being saved, so nothing "
                + "was written. Reload the page and try again.")
            : new SettingsWriteResult.Succeeded(changed);
    }

    /// <summary>
    /// <c>Orders.AutoDeliverAfterDays</c>: a whole number of days inside doc 08 §3.8's range, as invariant
    /// digits.
    /// </summary>
    /// <remarks>
    /// <b>Cleared is allowed and is not the same as zero.</b> An empty box means the client has deliberately
    /// taken the figure out, and <c>OrderSettingsReader</c> then answers <see langword="null"/> and the job
    /// <i>skips the pass</i> rather than assuming a number — which is the whole reason that reader refuses
    /// to fall back. Zero is refused outright: "mark it delivered the moment it ships" is exactly the false
    /// "Verified buyer" badge doc 08 §4.1 G-18 is about, and a staff member who wants same-day closure has
    /// the Mark as delivered button.
    /// <para>
    /// Stored as invariant digits, because <c>ops.Settings.Value</c> is read with
    /// <see cref="CultureInfo.InvariantCulture"/> (doc 08 §3.8) and a value written in another culture
    /// would parse back as null — which the job would correctly refuse to guess at, and which would look
    /// like the setting had been cleared.
    /// </para>
    /// </remarks>
    private static string? CleanAutoDeliverDays(
        List<ValidationError> errors,
        string field,
        string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var days))
        {
            errors.Add(new ValidationError(
                field,
                "Enter a whole number of days, in digits — for example 7. Leave it empty to stop the "
                + "system closing orders by itself; staff then confirm every delivery by hand."));

            return null;
        }

        if (days < SettingKeys.Orders.AutoDeliverAfterDaysMin
            || days > SettingKeys.Orders.AutoDeliverAfterDaysMax)
        {
            errors.Add(new ValidationError(
                field,
                $"Use between {SettingKeys.Orders.AutoDeliverAfterDaysMin} and "
                + $"{SettingKeys.Orders.AutoDeliverAfterDaysMax} days. Nought would mark an order "
                + "delivered the moment it ships, which would also give the customer a Verified buyer "
                + "badge for goods still in the van."));

            return null;
        }

        return days.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Doc 08 §3.8 DB2, on the write path: each row carries the permission needed to change it, and this is
    /// where that column is read.
    /// </summary>
    /// <remarks>
    /// The same shape as the other two settings handlers', and the duplication is deliberate rather than
    /// overlooked — the argument <c>UpdateBankDetailsCommandHandler</c> records: the check is eight lines,
    /// and a shared helper would put the one control that decides who may change a company's registered
    /// details behind an indirection a future caller could forget. Permissions come from the user's roles
    /// through <see cref="IEffectivePermissionsProvider"/>, never from claims on the principal (doc 08
    /// §3.1 R4).
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
        "legalName" => "registered company name",
        "registeredAddress" => "registered address",
        "brNumber" => "business registration number",
        _ => "VAT registration number",
    };

    private static int LengthOf(string key) => key switch
    {
        _ when key == SettingKeys.Store.LegalName => LegalNameMaxLength,
        _ when key == SettingKeys.Store.Address => AddressMaxLength,
        _ => RegistrationNumberMaxLength,
    };
}
