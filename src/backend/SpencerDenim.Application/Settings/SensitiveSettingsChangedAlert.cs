using System.Text;
using System.Text.Json;
using SpencerDenim.Domain.Ops;

namespace SpencerDenim.Application.Settings;

/// <summary>
/// The <c>SensitiveSettingsChanged</c> alert doc 08 §3.8 DB2 asks for whenever a payment, security or
/// retention setting is written: a persistent banner for every Super Admin until one acknowledges it, so a
/// changed credential or rate is never silent (doc 11 P8: "every change alerts all Super Admins").
/// <para>
/// <b>Key names, the staff user's id and a count — never a value.</b> Doc 08 §3.8 limits an alert's details to
/// "keys, entity IDs and amounts; never secret values", and a changed payment credential is exactly the case
/// where leaking the value into a table nobody purges would turn the warning into the problem. The staff
/// user is an id, not an email address: the banner has no business holding personal data (the PDPA test
/// searches this table), and the audit row beside it already names the actor.
/// </para>
/// <para>
/// <b>Pure</b>, like <c>PaymentExceptionAlert</c>, so a unit test asserts the whole row including what must
/// not be in it. Written in the same transaction as the change (<c>IAdminAlertWriter.Enlist</c>), so a
/// change that committed always has its alert and one that rolled back never does.
/// </para>
/// </summary>
public static class SensitiveSettingsChangedAlert
{
    /// <summary>Doc 11 §4.3: Critical, GrantsAll only (R10).</summary>
    public const AdminAlertSeverity Severity = AdminAlertSeverity.Critical;

    /// <summary>Builds the row.</summary>
    /// <param name="what">A short name for the area changed, e.g. <c>PayPal payment settings</c>.</param>
    /// <param name="changedKeys">The keys whose values changed. Names only.</param>
    /// <param name="actingUserId">The staff user, or null for a job.</param>
    /// <param name="nowUtc">The caller's clock.</param>
    public static AdminAlert For(string what, IReadOnlyList<string> changedKeys, int? actingUserId, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        ArgumentNullException.ThrowIfNull(changedKeys);

        var title = $"{what} were changed ({changedKeys.Count} setting{(changedKeys.Count == 1 ? string.Empty : "s")})";

        return new AdminAlert
        {
            AlertType = AdminAlertTypes.SensitiveSettingsChanged,
            Severity = Severity,
            Title = title.Length <= AdminAlert.TitleMaxLength ? title : title[..AdminAlert.TitleMaxLength],
            DetailsJson = Details(changedKeys, actingUserId),
            RelatedEntityType = null,
            RelatedEntityId = null,
            AuditLogId = null,
            CreatedAtUtc = nowUtc,
        };
    }

    /// <summary>Fixed key order, so a test can assert the whole document.</summary>
    private static string Details(IReadOnlyList<string> changedKeys, int? actingUserId)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            writer.WriteStartArray("keys");

            foreach (var key in changedKeys)
            {
                writer.WriteStringValue(key);
            }

            writer.WriteEndArray();

            if (actingUserId is { } userId)
            {
                writer.WriteNumber("changedByUserId", userId);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
