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
/// <b>Secrets are overwritten, not stored.</b> Any property whose name says it is a secret, a password
/// (or passphrase), a token, a credential or an API key — or is an authorization header — has its value
/// replaced with <see cref="RedactedValue"/>, at any depth; the rule is <see cref="LooksSecret"/>. No
/// provider event is expected to contain one — they are notifications <i>about</i> a payment — so this
/// should never change anything; it is the net under the rule "never store secrets" for the day a
/// payload does, through a provider's bug or a caller that passed a request body where it meant a
/// response. When nothing matches the text is stored <b>verbatim</b>, byte for byte, because the entity's
/// contract is the provider's own payload and a re-serialised copy would differ in whitespace and number
/// formatting from what a dispute needs.
/// <para>
/// <b>A signature is not a secret and is not redacted here</b> (PayHere's <c>md5sig</c>, PayPal's transmission
/// signature header): the provider code has verified it before it calls the store, and this class cannot
/// know a provider's field names. Whether to keep it is the provider's decision, and the recommendation is
/// to drop it before recording — a hash derived from the merchant secret is of no use once the event is
/// accepted, and leaves nothing to brute-force from a leaked table.
/// </para>
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
    /// Fragments of a property name, once normalised (lower-case, with <c>_</c>, <c>-</c>, <c>.</c> and
    /// spaces removed), that mark its value as not to be stored. Fragments, because providers spell the
    /// same thing <c>client_secret</c>, <c>clientSecret</c>, <c>merchant-secret</c> and <c>X-Auth-Token</c>.
    /// <para>
    /// <b>"token" is on the list, whole</b>: any name containing it is redacted, so <c>access_token</c>,
    /// <c>id_token</c>, <c>X-Auth-Token</c> and a vault's <c>payment_token</c> all go. That is a choice
    /// for the log being safe over the log being complete — an earlier version of this list named the
    /// credential forms only, left <c>payment_token</c> readable for the sake of disputes, and so did not do
    /// what this class's own remarks said. Neither PayPal's payment webhooks nor PayHere's notification
    /// carries a field with "token" in its name, so nothing a payment dispute needs is lost.
    /// </para>
    /// </summary>
    private static readonly string[] SecretFragments =
    [
        "secret",
        "password",
        "passwd",
        "pwd",
        "passphrase",
        "token",
        "credential",
        "apikey",
        "authkey",
        "privatekey",
        "bearer",
        "cvv",
        "cvc",
        "securitycode",
    ];

    /// <summary>
    /// Names (normalised) that are redacted only when they are exactly this, because as fragments they would
    /// take legitimate fields with them: PayPal's capture carries <c>authorization_id</c> in
    /// <c>supplementary_data.related_ids</c> and an order's <c>payments.authorizations</c> is an array of
    /// authorisation records, none of which is a credential. A bare <c>authorization</c> or <c>auth</c>
    /// property is an HTTP header or a credential pair, and is.
    /// </summary>
    private static readonly HashSet<string> SecretNames = new(StringComparer.Ordinal)
    {
        "auth",
        "authorization",
        "proxyauthorization",
        "authheader",
    };

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

    /// <summary>
    /// Whether a property of this name must not have its value stored: its normalised name contains one of
    /// <see cref="SecretFragments"/>, or is one of <see cref="SecretNames"/> exactly.
    /// </summary>
    /// <remarks>
    /// Public so the rule is testable as a rule, name by name, not only through a payload. **What it must
    /// leave alone** is as much the contract as what it takes: <c>payment_id</c>, <c>order_id</c>,
    /// <c>merchant_id</c>, <c>payer_id</c>, <c>status_code</c>, <c>authorization_id</c>,
    /// <c>captured_amount</c>, <c>card_holder_name</c> and a signature such as <c>md5sig</c> are the fields a
    /// payment dispute reads, and none contains a fragment above.
    /// </remarks>
    public static bool LooksSecret(string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);

        var normalised = propertyName
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return SecretNames.Contains(normalised)
            || SecretFragments.Any(fragment => normalised.Contains(fragment, StringComparison.Ordinal));
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

    /// <summary>
    /// Whether <paramref name="value"/> is non-empty printable ASCII with no space — the only text that
    /// survives a trip into a <c>varchar</c> column unchanged.
    /// </summary>
    /// <remarks>
    /// <b>This is a correctness rule, not a style one.</b> Every text column of <c>sales.PaymentEvents</c>
    /// that carries a key (<c>ProviderCode</c>, <c>EventType</c>, <c>ProviderEventId</c>,
    /// <c>IdempotencyKey</c>) is <c>varchar</c>, but a parameter arrives as <c>nvarchar</c>, so a character
    /// outside the code page is folded to <c>?</c> on the way in. The unique index on the key then sees two
    /// different keys as one (two Cyrillic ids both become <c>????</c>), and the "is it there already?" check —
    /// which compares the <i>unfolded</i> text — says no, so the insert fails with a unique violation instead
    /// of answering "redelivery". A space is excluded too: SQL Server ignores trailing spaces when comparing,
    /// so <c>"A "</c> and <c>"A"</c> are the same key to the index and different to the caller. Real gateways'
    /// event and payment ids are ASCII tokens, so rejecting the rest costs nothing real.
    /// </remarks>
    public static bool IsStorable(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }

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

        if (!IsStorable(key))
        {
            throw new ArgumentException(
                "A payment event idempotency key must be printable ASCII with no spaces: the column is "
                + "varchar, and text outside it is folded to '?', which makes different keys collide.");
        }

        return key.Length <= MaxLength
            ? key
            : throw new ArgumentException(
                $"A payment event idempotency key is at most {MaxLength} characters; this one is {key.Length}.");
    }
}
