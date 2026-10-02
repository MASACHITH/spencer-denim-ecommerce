using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Settings;

/// <summary>
/// Builds the <c>ops.AuditLogs</c> rows for an ADM-22 write (doc 08 §3.8).
/// <para>
/// <b>One row per changed key</b>, which is doc 08 §3.8's own words — not one row per save. A save that
/// moved the WhatsApp number and the Instagram address is two events: the audit viewer filters by
/// <c>EntityId</c>, and one row named <c>Contact</c> would be a row nobody could search for and whose
/// <c>AfterJson</c> nobody could read against a single key.
/// </para>
/// <para>
/// <b>Why a settings change in particular has to be audited.</b> Issue <b>#59</b> is about a trail that
/// showed a client nothing but sign-ins. A logo replacement is the most visible change anybody can make
/// to this shop — every page, instantly — and a social link is a URL the shop publishes under its own
/// name. "Who put that there" has to have an answer, and the row has to commit with the change
/// (<c>IAuditLogWriter.Enlist</c>) rather than before or after it.
/// </para>
/// </summary>
public static class SettingsAuditTrail
{
    /// <summary><c>ops.AuditLogs.EntityType</c> for a settings row.</summary>
    public const string EntityType = "Setting";

    /// <summary>
    /// What a secret's before and after values are logged as, instead of the value. Doc 08 §3.8's
    /// redaction list: "Logged as <c>[secret changed]</c> — <c>Settings.Value</c> where IsSecret".
    /// </summary>
    public const string RedactedSecret = "[secret changed]";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The audit row for one key whose value moved from <paramref name="before"/> to
    /// <paramref name="after"/>, or <see langword="null"/> when it did not.
    /// <para>
    /// <see langword="null"/> rather than a row saying "changed nothing": a trail full of those is how an
    /// audit log stops being read, and the caller uses the null to decide there is nothing to save either.
    /// </para>
    /// <para>
    /// <b>The redaction is driven by <see cref="Setting.IsSecret"/> on the row</b>, not by the caller
    /// remembering. A handler cannot opt out of it, because it never supplies the flag.
    /// </para>
    /// </summary>
    public static AuditLog? ForValueChange(
        Setting row,
        string? after,
        AdminActor actor,
        DateTime occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(actor);

        var before = row.Value;

        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return null;
        }

        return new AuditLog
        {
            OccurredAtUtc = occurredAtUtc,
            UserId = actor.UserId,
            UserEmail = actor.Email,
            Action = AuditActions.SettingChange,
            EntityType = EntityType,
            EntityId = Truncate(row.Key),
            BeforeJson = ToJson(row, before),
            AfterJson = ToJson(row, after),
            IpAddress = actor.IpAddress,
            UserAgent = actor.UserAgent,
            CorrelationId = actor.CorrelationId,
        };
    }

    /// <summary>
    /// <c>ops.AuditLogs.EntityId</c> is <c>varchar(100)</c> and so is <c>ops.Settings.Key</c>, so this can
    /// never actually cut — it is here so that the day a key gets longer, the audit row is still written
    /// rather than the insert failing and taking the change with it.
    /// </summary>
    private static string Truncate(string key) =>
        key.Length > 100 ? key[..100] : key;

    /// <summary>
    /// <c>{"value": …}</c> — one field, named, rather than a bare JSON string, because
    /// <c>CK_AuditLogs_AfterJson</c> requires <c>ISJSON = 1</c> and because the viewer renders a changed-field
    /// map. A secret's value is replaced here, which is the single place it could have leaked into the one
    /// table nobody ever deletes from.
    /// </summary>
    private static string ToJson(Setting row, string? value)
    {
        var shown = row.IsSecret
            ? (value is null ? null : RedactedSecret)
            : value;

        return JsonSerializer.Serialize(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["value"] = shown },
            JsonOptions);
    }
}
