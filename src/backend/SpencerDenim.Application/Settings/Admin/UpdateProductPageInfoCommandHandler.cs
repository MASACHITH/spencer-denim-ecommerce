using System.Globalization;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Settings.Admin;

/// <summary>
/// The product page's delivery and returns texts (doc 08 §3.8 group <c>Content</c>; PDP-05, issue #33).
/// </summary>
/// <param name="Fields">
/// The request's fields, keyed by their body name, holding <b>only the fields the request sent</b>. Absent means
/// "not part of this save"; present and <see langword="null"/> or blank means "show no text" — which is a choice
/// an owner may make, unlike an unset retention period, because the panel's fees and links stand on their own.
/// </param>
/// <param name="RowVersion">The <c>Content</c> save unit's token, as the screen read it.</param>
/// <param name="Actor">Who is asking, for the audit rows and the per-row permission check.</param>
public sealed record UpdateProductPageInfoCommand(
    IReadOnlyDictionary<string, string?> Fields,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// Sanitises and saves <c>Content.PdpDeliveryInfoHtml</c> and <c>Content.PdpReturnsInfoHtml</c>, writing <b>one
/// audit row per changed key</b> (doc 08 §3.8 DB2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Sanitised here, on save</b>, with the content pages' <see cref="IHtmlSanitiser"/> — doc 08 §3.8's rule for
/// a <see cref="SettingValueType.TextHtml"/> value (N10). The stored value is the cleaned one, so the audit row,
/// the settings screen and the storefront all see the same markup; the storefront's read sanitises again, for a
/// row that did not come through here.
/// </para>
/// <para>
/// <b>What was removed is reported, not swallowed.</b> A paste that lost a <c>&lt;script&gt;</c> or an
/// <c>onclick</c> is saved without it, and <see cref="SettingsWriteResult.Succeeded.Removed"/> carries the list
/// so the screen can say so. A paste the sanitiser refuses to parse at all (too long, or nested past its limit) is
/// a 400 on that field instead, because nothing about its contents is known.
/// </para>
/// <para>
/// <b>The order of the checks is <c>UpdateTrafficSettingsCommandHandler</c>'s</b>: validate, then seeded, then
/// concurrency, then permission over the keys the caller asked to write, then the diff, then save.
/// <c>Settings.Edit</c>, and no sensitive-change alert: the group is not on N2's list. Nothing caches the value
/// — the product page reads the rows on every render — so a save shows on the next page view.
/// </para>
/// </remarks>
public sealed class UpdateProductPageInfoCommandHandler(
    ISettingsStore store,
    IHtmlSanitiser sanitiser,
    IAuditLogWriter auditLog,
    IEffectivePermissionsProvider permissionsProvider,
    IClock clock)
    : ICommandHandler<UpdateProductPageInfoCommand, SettingsWriteResult>
{
    /// <summary>The body field for <c>Content.PdpDeliveryInfoHtml</c>.</summary>
    public const string DeliveryField = "deliveryInfoHtml";

    /// <summary>The body field for <c>Content.PdpReturnsInfoHtml</c>.</summary>
    public const string ReturnsField = "returnsInfoHtml";

    /// <summary>
    /// Every key this command may touch, paired with its request field, in the order the form shows them. One
    /// list, so the validation, the write, the audit rows and the permission check cannot disagree about the set.
    /// </summary>
    private static readonly (string Key, string Field)[] Targets =
    [
        (SettingKeys.Content.PdpDeliveryInfoHtml, DeliveryField),
        (SettingKeys.Content.PdpReturnsInfoHtml, ReturnsField),
    ];

    /// <summary>The fields a request may carry, in the order the form shows them.</summary>
    public static IReadOnlyList<string> Fields { get; } = Targets.Select(target => target.Field).ToList();

    public async Task<SettingsWriteResult> HandleAsync(
        UpdateProductPageInfoCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();
        var wanted = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var removed = new List<string>();

        foreach (var (key, field) in Targets)
        {
            if (!command.Fields.TryGetValue(field, out var raw))
            {
                continue;
            }

            var cleaned = sanitiser.SanitiseSnippet(raw);

            if (cleaned.Rejection is { } refusal)
            {
                errors.Add(new ValidationError(field, refusal));
                continue;
            }

            if (cleaned.Html.Length > SettingKeys.Content.PdpInfoMaxLength)
            {
                errors.Add(new ValidationError(
                    field,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"That is {cleaned.Html.Length:N0} characters of markup, and this text holds at most "
                        + $"{SettingKeys.Content.PdpInfoMaxLength:N0}. Keep it to a few lines and put the detail "
                        + $"on the Shipping or Returns page, which the product page links to.")));
                continue;
            }

            removed.AddRange(cleaned.Removed);

            // Empty is stored as null — "no text" — so an emptied box and a never-filled one are the same state,
            // and the storefront has one thing to test for.
            wanted[key] = cleaned.Html.Length == 0 ? null : cleaned.Html;
        }

        if (errors.Count > 0)
        {
            return new SettingsWriteResult.Invalid(errors);
        }

        var rows = await store.ListAsync(
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.Content.Group),
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
                    : "Somebody else changed the product page texts while you were editing them. Reload the "
                      + "page to see their change, then re-apply yours.");
        }

        var requested = Targets
            .Where(target => wanted.ContainsKey(target.Key))
            .Select(target => byKey[target.Key]);

        if (await CheckPermissionAsync(requested, command.Actor, cancellationToken) is { } refused)
        {
            return refused;
        }

        var report = removed.Distinct(StringComparer.Ordinal).ToList();
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

            // The audit trail's own diff, so "changed" and "has an audit row" are one question asked once.
            if (SettingsAuditTrail.ForValueChange(row, after, command.Actor, now) is not { } audit)
            {
                continue;
            }

            changed.Add(key);
            audits.Add(audit);

            row.Value = after;
            row.UpdatedAtUtc = now;
            row.UpdatedByUserId = command.Actor.UserId;
        }

        if (changed.Count == 0)
        {
            return new SettingsWriteResult.Unchanged(report);
        }

        foreach (var audit in audits)
        {
            auditLog.Enlist(audit);
        }

        var outcome = await store.SaveAsync(command.Actor.UserId, cancellationToken);

        return outcome == SettingsSaveOutcome.ConcurrencyConflict
            ? new SettingsWriteResult.Conflict(
                SettingsConflictCodes.StaleRowVersion,
                "Somebody else changed the product page texts while your change was being saved, so nothing "
                + "was written. Reload the page and try again.")
            : new SettingsWriteResult.Succeeded(changed, report);
    }

    /// <summary>
    /// Doc 08 §3.8 DB2 on the write path: each row carries the permission needed to change it, and this reads it.
    /// </summary>
    /// <remarks>
    /// The same lines as the other settings handlers, duplicated on purpose for the reason
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
