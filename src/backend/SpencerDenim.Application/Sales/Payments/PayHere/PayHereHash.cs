using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SpencerDenim.Application.Sales.Payments.PayHere;

/// <summary>
/// The two signatures PayHere's protocol is built on, and nothing else (doc 11 P8b steps 1 and 4).
/// <para>
/// <b>Both are keyed by <c>UPPER(MD5(merchant_secret))</c>, and that is PayHere's choice and not ours.</b> The
/// protocol <i>mandates</i> MD5 — the gateway computes the same digest on its side, so a stronger hash here
/// would simply never match. MD5 is broken as a collision-resistant hash, and that is not the property in use:
/// what is relied on is that nobody can produce the digest of a message that includes a secret they do not
/// hold, which is the property PayHere's design needs from it. The risk it carries is therefore the secret's
/// confidentiality, which is why the secret is a Data Protection ciphertext in <c>ops.Settings</c>, is never
/// returned by any endpoint, and appears in no log. The analyser warning (CA5351) is suppressed at the one
/// place a digest is taken, with this reason, and nowhere else.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Checkout hash</b> — what PayHere checks on the form the browser posts, so a customer cannot change the
/// amount or the order: <c>UPPER(MD5(merchant_id + order_id + amount + currency + UPPER(MD5(secret))))</c>,
/// the amount with exactly two decimals.
/// </description></item>
/// <item><description>
/// <b>Notification signature</b> (<c>md5sig</c>) — what we check on the server-to-server notify, so a forged
/// request cannot pay an order:
/// <c>UPPER(MD5(merchant_id + order_id + payhere_amount + payhere_currency + status_code + UPPER(MD5(secret))))</c>,
/// over the strings exactly as PayHere sent them.
/// </description></item>
/// </list>
/// <para>
/// <b>What is not signed.</b> <c>status_message</c>, <c>method</c>, the card fields and any custom field are
/// outside <c>md5sig</c>. They are therefore data from the network, not facts: nothing here or in the
/// notification handler makes a decision from one.
/// </para>
/// </summary>
public static class PayHereHash
{
    /// <summary>The currency PayHere is asked to charge in, and the only one the shop sells in.</summary>
    public const string Currency = "LKR";

    /// <summary>
    /// <c>UPPER(MD5(merchant_secret))</c>, the inner digest both signatures embed.
    /// </summary>
    public static string SecretDigest(string merchantSecret)
    {
        ArgumentException.ThrowIfNullOrEmpty(merchantSecret);

        return Md5Upper(merchantSecret);
    }

    /// <summary>
    /// The amount as PayHere's own sample code formats it: <c>number_format($amount, 2, '.', '')</c> — two
    /// decimals, a full stop, no thousands separator, whatever the server's culture.
    /// </summary>
    public static string FormatAmount(decimal amount) =>
        amount.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>The <c>hash</c> field of the checkout form, for an amount to be formatted here.</summary>
    public static string CheckoutHash(
        string merchantId,
        string orderId,
        decimal amount,
        string currency,
        string merchantSecret) =>
        CheckoutHash(merchantId, orderId, FormatAmount(amount), currency, merchantSecret);

    /// <summary>The <c>hash</c> field of the checkout form, for an amount already formatted.</summary>
    public static string CheckoutHash(
        string merchantId,
        string orderId,
        string formattedAmount,
        string currency,
        string merchantSecret)
    {
        ArgumentException.ThrowIfNullOrEmpty(merchantId);
        ArgumentException.ThrowIfNullOrEmpty(orderId);
        ArgumentException.ThrowIfNullOrEmpty(formattedAmount);
        ArgumentException.ThrowIfNullOrEmpty(currency);

        return Md5Upper(string.Concat(merchantId, orderId, formattedAmount, currency, SecretDigest(merchantSecret)));
    }

    /// <summary>
    /// The signature PayHere should have put in <c>md5sig</c>. Every argument is the string PayHere posted,
    /// untouched: re-formatting <c>payhere_amount</c> (<c>"1500.5"</c> to <c>"1500.50"</c>) would verify a
    /// different message from the one PayHere signed.
    /// </summary>
    public static string NotificationSignature(
        string merchantId,
        string orderId,
        string payhereAmount,
        string payhereCurrency,
        string statusCode,
        string merchantSecret)
    {
        ArgumentException.ThrowIfNullOrEmpty(merchantId);
        ArgumentException.ThrowIfNullOrEmpty(orderId);
        ArgumentException.ThrowIfNullOrEmpty(payhereAmount);
        ArgumentException.ThrowIfNullOrEmpty(payhereCurrency);
        ArgumentException.ThrowIfNullOrEmpty(statusCode);

        return Md5Upper(string.Concat(
            merchantId,
            orderId,
            payhereAmount,
            payhereCurrency,
            statusCode,
            SecretDigest(merchantSecret)));
    }

    /// <summary>
    /// Whether <paramref name="receivedSignature"/> is the signature of this notification, compared in
    /// constant time.
    /// <para>
    /// <b>Constant time, although an MD5 hex digest is not what an attacker is short of.</b> A comparison
    /// that returns at the first differing character tells a patient attacker how many leading characters
    /// of a guess were right; the cost of not allowing that is one call. Case is ignored in the received
    /// value — PayHere sends upper case, and a lower-case digest of the same bytes is the same digest — and
    /// a value of any other length is simply wrong.
    /// </para>
    /// </summary>
    public static bool IsValidNotificationSignature(
        string merchantId,
        string orderId,
        string payhereAmount,
        string payhereCurrency,
        string statusCode,
        string merchantSecret,
        string? receivedSignature)
    {
        if (string.IsNullOrWhiteSpace(receivedSignature))
        {
            return false;
        }

        var expected = NotificationSignature(
            merchantId,
            orderId,
            payhereAmount,
            payhereCurrency,
            statusCode,
            merchantSecret);

        var received = receivedSignature.Trim().ToUpperInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(received));
    }

    /// <summary>
    /// <c>UPPER(MD5(text))</c> over the UTF-8 bytes, which is what PHP's <c>md5()</c> hashes and so what
    /// PayHere's own samples compute. The only place in the code base a digest of this kind is taken.
    /// </summary>
    [SuppressMessage(
        "Security",
        "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "PayHere's checkout and notify protocol mandates MD5 (see the type remarks): the gateway "
            + "computes the same digest, so no other algorithm can interoperate. It is used as a keyed "
            + "message authenticator over a secret that is never exposed, not for collision resistance.")]
    private static string Md5Upper(string text) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text)));
}
