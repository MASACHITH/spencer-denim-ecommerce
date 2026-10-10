using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpencerDenim.Application.Sales.Payments;

/// <summary>
/// What may be written into <c>sales.PaymentEvents.PayloadJson</c> (doc 08 §3.5, DB13, DB15), decided once
/// and in one place so that two gateways cannot decide it differently.
/// <para>
/// <b>Three rules, in this order.</b>
/// </para>
/// <list type="number">
/// <item><description>
/// <b>It must be a JSON object or array, or it is refused.</b> The column carries
/// <c>CK_PaymentEvents_PayloadJson (ISJSON = 1)</c>, and which SQL Server versions count a bare scalar as
/// JSON differs, so the rule here is the strict one: whatever this accepts, every version stores. A
/// gateway that notifies in some other encoding (a form post, for instance) turns it into a JSON object
/// <i>before</i> it is stored — the verbatim bytes were never going to be a better record than the fields.
/// A bad payload is a bug in the caller, so it throws here with a message and not at the database with a
/// constraint number.
/// </description></item>
/// <item><description>
/// <b>Secrets are overwritten, not stored.</b> Any property whose name says it is a secret, a password, a
/// token or a credential has its value replaced with <see cref="RedactedValue"/>, at any depth. No
/// provider event is expected to contain one — they are notifications <i>about</i> a payment — so this
/// should never change anything; it is the net under the rule "never store secrets" for the day a
/// payload does, through a provider's bug or a caller that passed a request body where it meant a
/// response. When nothing matches the text is stored <b>verbatim</b>, byte for byte, because the entity's
/// contract is the provider's own payload and a re-serialised copy would differ in whitespace and number
/// formatting from what a dispute needs.
/// </description></item>
/// <item><description>
/// <b>It is capped at <see cref="MaxLength"/> characters.</b> A real PayPal or PayHere event is a few
/// kilobytes; a payload many times that is hostile or broken, and an unbounded <c>nvarchar(max)</c> fed
/// by an endpoint the internet can reach is a way to fill the disk. Cutting the text would produce
/// invalid JSON, so an oversize payload is replaced by a small marker object recording that it was
/// dropped and how long it was. The event is still recorded — its idempotency key is what makes a
/// redelivery harmless — but it cannot be reprocessed from the log, which the receipt says.
/// </description></item>
/// </list>
/// </summary>
public static class PaymentEventPayload
{
    /// <summary>The longest payload stored, in characters. 64 KiB of text.</summary>
    public const int MaxLength = 64 * 1024;

    /// <summary>What a secret-looking property's value is replaced with.</summary>
    public const string RedactedValue = "[redacted]";

    /// <summary>
    /// The deepest nesting accepted. A real event nests a handful of levels; a payload built to exhaust a
    /// recursive parser is refused instead of parsed.
    /// </summary>
    private const int MaxDepth = 32;

    /// <summary>
    /// Fragments of a property name (lower-case, with <c>_</c> and <c>-</c> removed) that mark its value as
    /// not to be stored. Fragments rather than exact names, because providers spell the same thing
    /// <c>client_secret</c>, <c>clientSecret</c> and <c>merchant-secret</c>.
    /// <para>
    /// <b>"token" alone is not on the list</b>: it would eat harmless identifiers (a payment token that is
    /// a reference, not a credential), and redacting one makes the log less useful for the dispute it
    /// exists for. The credential forms are named.
    /// </para>
    /// </summary>
    private static readonly string[] SecretFragments =
    [
        "secret",
        "password",
        "passwd",
        "authorization",
        "apikey",
        "accesstoken",
        "refreshtoken",
        "idtoken",
        "privatekey",
        "bearer",
        "cvv",
        "cvc",
        "securitycode",
    ];

    /// <summary>
    /// Validates <paramref name="payloadJson"/>, redacts anything secret-named and applies the cap.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The text is empty, is not valid JSON, is not an object or an array, or nests deeper than
    /// <see cref="MaxDepth"/>.
    /// </exception>
    public static PreparedPayload Prepare(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new ArgumentException("A payment event needs a payload.", nameof(payloadJson));
        }

        JsonNode? root;

        try
        {
            root = JsonNode.Parse(payloadJson, nodeOptions: null, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            // ArgumentException as well as JsonException: a document that repeats a property name is
            // refused by the node parser with the former, and is no more storable than a malformed one.
            throw new ArgumentException(
                "A payment event's payload must be valid JSON. A provider that does not notify in JSON "
                + "converts the notification to a JSON object before storing it.",
                nameof(payloadJson),
                exception);
        }

        if (root is not (JsonObject or JsonArray))
        {
            throw new ArgumentException(
                "A payment event's payload must be a JSON object or array.",
                nameof(payloadJson));
        }

        var redacted = Redact(root);
        var json = redacted ? root.ToJsonString() : payloadJson;

        if (json.Length <= MaxLength)
        {
            return new PreparedPayload(json, Truncated: false, json.Length, redacted);
        }

        // Replaced, not cut: a prefix of a JSON document is not a JSON document.
        var originalLength = json.Length;

        var marker = string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"truncated":true,"originalLength":{{originalLength}}}""");

        return new PreparedPayload(marker, Truncated: true, originalLength, redacted);
    }

    /// <summary>Overwrites every secret-named property under <paramref name="node"/>. True if any was.</summary>
    private static bool Redact(JsonNode? node)
    {
        var changed = false;

        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToList())
                {
                    if (LooksSecret(property.Key))
                    {
                        obj[property.Key] = RedactedValue;
                        changed = true;
                    }
                    else
                    {
                        changed |= Redact(property.Value);
                    }
                }

                break;

            case JsonArray array:
                foreach (var element in array)
                {
                    changed |= Redact(element);
                }

                break;
        }

        return changed;
    }

    private static bool LooksSecret(string propertyName)
    {
        var normalised = propertyName
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return SecretFragments.Any(fragment => normalised.Contains(fragment, StringComparison.Ordinal));
    }
}

/// <summary>A payload as it will be stored, and what was done to it on the way.</summary>
/// <param name="Json">The text to store: the original when nothing was changed.</param>
/// <param name="Truncated">True when the payload exceeded the cap and <paramref name="Json"/> is a marker.</param>
/// <param name="OriginalLength">The length of the payload before it was capped.</param>
/// <param name="Redacted">True when at least one secret-named property's value was overwritten.</param>
public sealed record PreparedPayload(string Json, bool Truncated, int OriginalLength, bool Redacted);

/// <summary>
/// The <c>sales.PaymentEvents.IdempotencyKey</c> shapes doc 08 §3.5 names, so two providers cannot spell
/// them differently.
/// </summary>
public static class PaymentEventKey
{
    /// <summary><c>varchar(160)</c>, doc 08 §3.5 — longer than the parts it is built from, on purpose.</summary>
    public const int MaxLength = 160;

    /// <summary><c>{Provider}:{EventId}</c>, for a provider that gives every event an id.</summary>
    public static string ForEvent(string providerCode, string eventId) =>
        Compose(providerCode, eventId);

    /// <summary>
    /// <c>{Provider}:{Type}:{ResourceId}</c>, for a provider that gives no event id — a notification is then
    /// identified by what it says happened to which resource.
    /// </summary>
    public static string ForResource(string providerCode, string eventType, string resourceId) =>
        Compose(providerCode, eventType, resourceId);

    private static string Compose(params string[] parts)
    {
        foreach (var part in parts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(part);
        }

        var key = string.Join(':', parts.Select(part => part.Trim()));

        return key.Length <= MaxLength
            ? key
            : throw new ArgumentException(
                $"A payment event idempotency key is at most {MaxLength} characters; this one is {key.Length}.");
    }
}
