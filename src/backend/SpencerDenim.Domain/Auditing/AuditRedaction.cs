using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpencerDenim.Domain.Auditing;

/// <summary>
/// Doc 08 §3.8 DB24's redaction list, enforced where every audit row is written (#50).
/// <para>
/// <b>Why at the write and not with <c>[AuditIgnore]</c> / <c>[AuditRedact]</c> attributes.</b> DB24 describes attributes
/// honoured by a generic entity-change interceptor. This codebase has no such interceptor: every <c>ops.AuditLogs</c> row
/// is written by hand, by the use case that made the change (#59), and an attribute nobody reads would protect nothing.
/// What DB24 is for — no audit row ever carries a credential — is enforced instead at the one place every row passes
/// through (<c>IAuditLogWriter</c>), whichever use case built it and however it spelled the field. It covers a future
/// generic interceptor too, because that would write through the same writer.
/// </para>
/// <list type="bullet">
/// <item><description><b>Never logged</b> (the property is removed, at any depth, whatever its casing): the DB24 columns
/// (<c>PasswordHash</c>, <c>SecurityStamp</c>, <c>ConcurrencyStamp</c>, <c>AccessFailedCount</c>,
/// <c>TransportOverrideProtected</c>) and the contents of the tables DB24 never audits that a hand-written row could
/// still copy (<c>UserTokens</c>: the authenticator key and recovery codes), plus any password field.</description></item>
/// <item><description><b>Logged as <c>"[changed]"</c></b>: <c>ProofAssetId</c>.</description></item>
/// </list>
/// <para>
/// <c>Settings.Value</c> where <c>IsSecret</c> is written as <c>"[secret changed]"</c> by <c>SettingsAuditTrail</c>, which
/// knows which settings are secret; a property name cannot say so, so that rule stays there.
/// </para>
/// </summary>
public static class AuditRedaction
{
    /// <summary>What a property on the "changed" list is written as.</summary>
    public const string Changed = "[changed]";

    /// <summary>Property names never written to an audit row, compared without case.</summary>
    public static IReadOnlySet<string> NeverLogged { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PasswordHash",
        "SecurityStamp",
        "ConcurrencyStamp",
        "AccessFailedCount",
        "TransportOverrideProtected",
        "AuthenticatorKey",
        "RecoveryCodes",
        "RecoveryCode",
        "Password",
        "CurrentPassword",
        "NewPassword",
        "TemporaryPassword",
        "InitialPassword",
        "ClientSecret",
    };

    /// <summary>Property names written as <see cref="Changed"/> whatever their value, compared without case.</summary>
    public static IReadOnlySet<string> LoggedAsChanged { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ProofAssetId",
    };

    /// <summary>
    /// <paramref name="entry"/> with both JSON columns redacted. The same instance when nothing needed redacting, so a
    /// caller that kept a reference to the row it enlisted still has the row that was written.
    /// </summary>
    public static AuditLog Apply(AuditLog entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var before = Redact(entry.BeforeJson);
        var after = Redact(entry.AfterJson);

        if (ReferenceEquals(before, entry.BeforeJson) && ReferenceEquals(after, entry.AfterJson))
        {
            return entry;
        }

        return new AuditLog
        {
            OccurredAtUtc = entry.OccurredAtUtc,
            UserId = entry.UserId,
            UserEmail = entry.UserEmail,
            Action = entry.Action,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId,
            BeforeJson = before,
            AfterJson = after,
            IpAddress = entry.IpAddress,
            UserAgent = entry.UserAgent,
            CorrelationId = entry.CorrelationId,
        };
    }

    /// <summary>
    /// What a column is written as when it could not be read and might hold a listed property: valid JSON (the column has
    /// an <c>ISJSON</c> check), saying that something was there without saying what.
    /// </summary>
    public const string Unreadable = """{"redacted":"[unreadable]"}""";

    /// <summary>
    /// Deeper than SQL Server's own <c>ISJSON</c> limit (128), so anything the column would accept is read rather than
    /// skipped. The default (64) let a credential nested deeper than that through unread.
    /// </summary>
    private static readonly JsonDocumentOptions ReadOptions = new() { MaxDepth = 256 };

    /// <summary>
    /// <paramref name="json"/> with the listed properties removed or replaced, at any depth. The same string when there was
    /// nothing to do.
    /// <para>
    /// <b>It never throws</b>, because it runs inside the save of the change being audited and a redaction error must not
    /// roll that change back. <b>And it fails closed</b>: text it cannot read (not JSON, duplicate property names, an
    /// unpaired surrogate) is left as written only when no listed name appears anywhere in it; otherwise the column is
    /// written as <see cref="Unreadable"/>. Text that is not JSON at all is then refused by the column's own <c>ISJSON</c>
    /// check, exactly as it was before this class existed.
    /// </para>
    /// </summary>
    public static string? Redact(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        try
        {
            var root = JsonNode.Parse(json.TrimStart(ByteOrderMark), documentOptions: ReadOptions);

            return root is not null && RedactNode(root) ? root.ToJsonString() : json;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return MentionsAListedName(json) || NamesAListedProperty(json) ? Unreadable : json;
        }
    }

    /// <summary>A leading byte-order mark is not JSON; it is skipped before reading.</summary>
    private const char ByteOrderMark = (char)0xFEFF;

    private static bool IsListed(string? name) =>
        name is not null && (NeverLogged.Contains(name) || LoggedAsChanged.Contains(name));

    private static bool MentionsAListedName(string text) =>
        NeverLogged.Concat(LoggedAsChanged).Any(name => text.Contains(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether text the parser refused still names a listed property once its JSON escapes are undone — a name with one
    /// letter written as a <c>\uXXXX</c> escape does not contain the listed name as written, so the plain text search
    /// above cannot see it. The reader accepts duplicate names (the usual reason the parser refused), and
    /// <c>GetString</c> unescapes.
    /// <para>
    /// Text the reader gives up on partway through an object or array is treated as naming one whenever it contains an
    /// escape: fail closed. Text that does not even open one is not JSON at all and is left for the column's
    /// <c>ISJSON</c> check to refuse, so a caller writing non-JSON still fails loudly rather than storing a placeholder.
    /// </para>
    /// </summary>
    private static bool NamesAListedProperty(string text)
    {
        var options = new JsonReaderOptions
        {
            MaxDepth = ReadOptions.MaxDepth,
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        var openedContainer = false;

        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text.TrimStart(ByteOrderMark)), options);
            while (reader.Read())
            {
                openedContainer |= reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray;

                if (reader.TokenType == JsonTokenType.PropertyName && IsListed(reader.GetString()))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return openedContainer && text.Contains('\\', StringComparison.Ordinal);
        }
    }

    private static bool RedactNode(JsonNode node)
    {
        var changed = false;

        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(property => property.Key).ToList())
                {
                    if (NeverLogged.Contains(name))
                    {
                        obj.Remove(name);
                        changed = true;
                    }
                    else if (LoggedAsChanged.Contains(name))
                    {
                        // Null stays null: "[changed]" would claim a value existed.
                        if (obj[name] is { } current
                            && (current is not JsonValue value || value.ToJsonString() != JsonSerializer.Serialize(Changed)))
                        {
                            obj[name] = Changed;
                            changed = true;
                        }
                    }
                    else if (obj[name] is { } child && RedactNode(child))
                    {
                        changed = true;
                    }
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null && RedactNode(item))
                    {
                        changed = true;
                    }
                }

                break;
        }

        return changed;
    }
}
