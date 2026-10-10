using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SpencerDenim.Application.Sales.Payments.PayHere;

/// <summary>
/// The <c>status_code</c> values PayHere's notification carries (doc 11 P8b step 4).
/// </summary>
public enum PayHereStatus
{
    /// <summary>Charged back by the cardholder's bank.</summary>
    ChargedBack = -3,

    /// <summary>The payment failed.</summary>
    Failed = -2,

    /// <summary>The customer cancelled.</summary>
    Cancelled = -1,

    /// <summary>Pending: PayHere has not finished with it.</summary>
    Pending = 0,

    /// <summary>The money was taken.</summary>
    Success = 2,
}

/// <summary>
/// One PayHere notification, reduced to the fields this shop reads (doc 11 P8b step 4) — and, because this is
/// what is written to <c>sales.PaymentEvents.PayloadJson</c>, <b>an allow-list and not a copy</b>.
/// <para>
/// <b>What is deliberately not here.</b> PayHere also posts the cardholder's name, a masked card number, the
/// card's expiry, the method of the card, custom fields and the signature itself. None of them is read by
/// anything, so none of them is kept: card data has no business in this shop's database whatever its masking,
/// and the signature proves nothing once it has been checked. A field is added to this record only when a
/// decision needs it, which is the opposite way round from a deny-list of the fields somebody remembered to
/// worry about.
/// </para>
/// <para>
/// <b>The first six are signed and the last two are not.</b> <c>md5sig</c> covers the merchant ID, the order
/// ID, the amount, the currency and the status code; <see cref="StatusMessage"/> and <see cref="Method"/> are
/// text from the network. They are stored for a person to read, bounded and stripped of control characters,
/// and no decision is made from them.
/// </para>
/// </summary>
/// <param name="MerchantId"><c>merchant_id</c>.</param>
/// <param name="OrderId"><c>order_id</c> — see <see cref="PayHereOrderReference"/>.</param>
/// <param name="PaymentId"><c>payment_id</c>, PayHere's own id for this payment.</param>
/// <param name="PayhereAmount"><c>payhere_amount</c>, exactly as posted (it is part of the signature).</param>
/// <param name="PayhereCurrency"><c>payhere_currency</c>.</param>
/// <param name="StatusCode"><c>status_code</c>, exactly as posted.</param>
/// <param name="StatusMessage"><c>status_message</c>, cleaned and clipped; not signed.</param>
/// <param name="Method"><c>method</c> (VISA, MASTER, EZCASH, ...), cleaned and clipped; not signed.</param>
public sealed record PayHereNotification(
    string MerchantId,
    string OrderId,
    string PaymentId,
    string PayhereAmount,
    string PayhereCurrency,
    string StatusCode,
    string? StatusMessage,
    string? Method)
{
    /// <summary>The longest <c>status_message</c> kept.</summary>
    public const int StatusMessageMaxLength = 200;

    /// <summary>The longest <c>method</c> kept.</summary>
    public const int MethodMaxLength = 50;

    /// <summary>
    /// The longest a signed value may be. They are all short — a merchant ID, an order ID, a number — so
    /// anything longer is not PayHere's, and is refused before any work is done on it.
    /// </summary>
    private const int SignedFieldMaxLength = 64;

    /// <summary>
    /// <see cref="PayhereAmount"/> as a number, or <see langword="null"/> when it is not a plain decimal
    /// (digits and at most one full stop — no sign, no thousands separator, no exponent).
    /// </summary>
    public decimal? AmountValue =>
        decimal.TryParse(
            PayhereAmount,
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out var amount)
            ? amount
            : null;

    /// <summary><see cref="StatusCode"/> as one of the codes PayHere documents, or <see langword="null"/>.</summary>
    public PayHereStatus? Status =>
        int.TryParse(StatusCode, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code)
        && Enum.IsDefined(typeof(PayHereStatus), code)
            ? (PayHereStatus)code
            : null;

    /// <summary>
    /// The <c>sales.PaymentEvents.EventType</c> for this notification: a stable word for each status code.
    /// </summary>
    public string EventType => Status switch
    {
        PayHereStatus.Success => "payment.success",
        PayHereStatus.Pending => "payment.pending",
        PayHereStatus.Cancelled => "payment.cancelled",
        PayHereStatus.Failed => "payment.failed",
        PayHereStatus.ChargedBack => "payment.chargeback",
        _ => "payment.status_" + StatusCode,
    };

    /// <summary>
    /// The text to keep on a failed payment: PayHere's message if it sent one, else a plain sentence, never
    /// longer than <c>sales.Payments.FailureMessage</c> allows.
    /// </summary>
    public string FailureMessage(string fallback)
    {
        var message = string.IsNullOrWhiteSpace(StatusMessage) ? fallback : StatusMessage;

        return message.Length <= 500 ? message : message[..500];
    }

    /// <summary>
    /// Reads the signed fields out of a posted form. <see langword="false"/> means the request is not shaped
    /// like a PayHere notification at all, and nothing should be done with it — in particular nothing stored.
    /// </summary>
    /// <param name="fields">
    /// The posted fields. A field PayHere is expected to send exactly once must have been reduced to one
    /// value by the caller, or left out; the endpoint refuses a request that repeats one.
    /// </param>
    /// <param name="form">The notification and the signature that came with it.</param>
    public static bool TryRead(IReadOnlyDictionary<string, string> fields, out PayHereNotifyForm? form)
    {
        ArgumentNullException.ThrowIfNull(fields);

        form = null;

        if (!TryGet(fields, "merchant_id", IsToken, out var merchantId)
            || !TryGet(fields, "order_id", IsOrderId, out var orderId)
            || !TryGet(fields, "payment_id", IsToken, out var paymentId)
            || !TryGet(fields, "payhere_amount", IsAmount, out var amount)
            || !TryGet(fields, "payhere_currency", IsCurrency, out var currency)
            || !TryGet(fields, "status_code", IsStatusCode, out var statusCode)
            || !TryGet(fields, "md5sig", IsSignature, out var signature))
        {
            return false;
        }

        form = new PayHereNotifyForm(
            new PayHereNotification(
                merchantId,
                orderId,
                paymentId,
                amount,
                currency,
                statusCode,
                PayHereText.Clip(fields.GetValueOrDefault("status_message"), StatusMessageMaxLength),
                PayHereText.Clip(fields.GetValueOrDefault("method"), MethodMaxLength)),
            signature);

        return true;
    }

    /// <summary>
    /// The JSON written to <c>sales.PaymentEvents.PayloadJson</c>: the allow-listed fields, in a fixed order,
    /// and nothing else.
    /// </summary>
    public string ToStoredJson()
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("merchant_id", MerchantId);
            writer.WriteString("order_id", OrderId);
            writer.WriteString("payment_id", PaymentId);
            writer.WriteString("payhere_amount", PayhereAmount);
            writer.WriteString("payhere_currency", PayhereCurrency);
            writer.WriteString("status_code", StatusCode);

            if (StatusMessage is not null)
            {
                writer.WriteString("status_message", StatusMessage);
            }

            if (Method is not null)
            {
                writer.WriteString("method", Method);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// The notification a stored event holds, for the retry sweep, or <see langword="null"/> when the text is
    /// not what <see cref="ToStoredJson"/> writes (a truncated marker, a hand-edited row).
    /// </summary>
    public static PayHereNotification? FromStoredJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } value)
                {
                    fields[property.Name] = value;
                }
            }

            // The signature is not stored (it proves nothing once checked), so a well-formed one is supplied
            // for the shape check and discarded.
            fields["md5sig"] = new string('0', 32);

            return TryRead(fields, out var form) ? form!.Notification : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGet(
        IReadOnlyDictionary<string, string> fields,
        string name,
        Func<string, bool> isValid,
        out string value)
    {
        value = string.Empty;

        if (!fields.TryGetValue(name, out var raw)
            || string.IsNullOrEmpty(raw)
            || raw.Length > SignedFieldMaxLength
            || !isValid(raw))
        {
            return false;
        }

        value = raw;

        return true;
    }

    /// <summary>Letters, digits, hyphen and underscore: a merchant ID or a PayHere payment ID.</summary>
    private static bool IsToken(string value) =>
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    /// <summary>
    /// An order ID: what <see cref="PayHereOrderReference"/> writes, so letters, digits and hyphens. The exact
    /// shape is checked later, against the payment it names.
    /// </summary>
    private static bool IsOrderId(string value) =>
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    /// <summary>Digits with at most one full stop: <c>1500.50</c>. No sign, no grouping, no exponent.</summary>
    private static bool IsAmount(string value)
    {
        var dots = 0;

        foreach (var character in value)
        {
            if (character == '.')
            {
                dots++;
            }
            else if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return dots <= 1 && value[0] != '.' && value[^1] != '.';
    }

    private static bool IsCurrency(string value) =>
        value.Length == 3 && value.All(char.IsAsciiLetter);

    /// <summary>An optional minus and one to three digits: <c>2</c>, <c>0</c>, <c>-1</c>, <c>-3</c>.</summary>
    private static bool IsStatusCode(string value)
    {
        var digits = value[0] == '-' ? value[1..] : value;

        return digits.Length is >= 1 and <= 3 && digits.All(char.IsAsciiDigit);
    }

    /// <summary>The 32 hex characters of an MD5 digest.</summary>
    private static bool IsSignature(string value) =>
        value.Length == 32 && value.All(char.IsAsciiHexDigit);
}

/// <summary>A posted notification and the <c>md5sig</c> that came with it.</summary>
/// <param name="Notification">The allow-listed fields.</param>
/// <param name="Signature">The <c>md5sig</c> as posted, to be checked and then discarded.</param>
public sealed record PayHereNotifyForm(PayHereNotification Notification, string Signature);
