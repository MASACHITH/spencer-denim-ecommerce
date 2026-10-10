using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Sales.Payments.PayHere;

/// <summary>
/// The PayHere merchant account and the two windows its orders wait for money, as read from
/// <c>ops.Settings</c> at the moment they are used (doc 11 P8b, <c>Payments.PayHere.*</c>).
/// <para>
/// <b>A value, not a service.</b> It is read per call by <see cref="IPayHereSettingsReader"/> and never held,
/// so a Super Admin who switches sandbox to live, or rotates the secret, changes what the very next checkout
/// and the very next notification see, with no restart and no rebuild. It carries the plaintext secret and
/// therefore has a <see cref="ToString"/> that says nothing, and must never be logged or serialised.
/// </para>
/// </summary>
/// <param name="Mode"><c>sandbox</c> or <c>live</c> in lower case, or <see langword="null"/> when the stored value is neither.</param>
/// <param name="MerchantId">The merchant ID, trimmed, or <see langword="null"/> when unset.</param>
/// <param name="MerchantSecret">
/// The decrypted secret, or <see langword="null"/> when unset — or when a stored value cannot be decrypted
/// (a key ring that was lost), which is treated as not configured because there is nothing else to do.
/// </param>
/// <param name="PendingMaxDays">The pending hold in days, always within the key's bounds.</param>
/// <param name="ExpiryMinutes">The payment window in minutes, always within the key's bounds.</param>
public sealed record PayHereSettings(
    string? Mode,
    string? MerchantId,
    string? MerchantSecret,
    int PendingMaxDays,
    int ExpiryMinutes)
{
    /// <summary>PayHere's test checkout.</summary>
    public const string SandboxCheckoutUrl = "https://sandbox.payhere.lk/pay/checkout";

    /// <summary>PayHere's live checkout.</summary>
    public const string LiveCheckoutUrl = "https://www.payhere.lk/pay/checkout";

    /// <summary>
    /// Whether the shop can build a signed form and verify a notification: a valid mode, a merchant ID and
    /// a secret. Nothing else is needed from the settings table.
    /// </summary>
    public bool IsConfigured =>
        CheckoutUrl is not null
        && !string.IsNullOrWhiteSpace(MerchantId)
        && !string.IsNullOrEmpty(MerchantSecret);

    /// <summary>
    /// Whether the shop's public address may be plain <c>http</c>: only in sandbox mode, where no real payment
    /// is taken (a loopback host is always allowed - see <see cref="PayHereRoutes.Origin"/>).
    /// </summary>
    public bool AllowsPlainHttpOrigin => Mode == SettingKeys.PayHere.ModeSandbox;

    /// <summary>The address the checkout form posts to for <see cref="Mode"/>, or <see langword="null"/> for no valid mode.</summary>
    public string? CheckoutUrl => Mode switch
    {
        SettingKeys.PayHere.ModeSandbox => SandboxCheckoutUrl,
        SettingKeys.PayHere.ModeLive => LiveCheckoutUrl,
        _ => null,
    };

    /// <summary>
    /// A stored mode as <c>sandbox</c> or <c>live</c>, or <see langword="null"/> for anything else. A blank
    /// value is the seeded default, <c>sandbox</c>; a value that is neither is not guessed at, because
    /// "live" is the wrong thing to fall back to and "sandbox" would quietly route real customers to a test
    /// page.
    /// </summary>
    public static string? NormaliseMode(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return SettingKeys.PayHere.ModeSandbox;
        }

        var mode = stored.Trim().ToLowerInvariant();

        return mode is SettingKeys.PayHere.ModeSandbox or SettingKeys.PayHere.ModeLive ? mode : null;
    }

    /// <summary>Never prints the secret.</summary>
    public override string ToString() =>
        $"PayHereSettings {{ Mode = {Mode}, MerchantId = {MerchantId}, MerchantSecret = {(MerchantSecret is null ? "unset" : "set")} }}";
}

/// <summary>
/// Reads <see cref="PayHereSettings"/> from <c>ops.Settings</c>, decrypting the merchant secret.
/// <para>
/// <b>Its own port, and not <c>ISettingsStore</c>.</b> That store hands out tracked rows with their
/// permission column, which an admin write needs and a checkout or a notify handler must not have; this one
/// can answer a single question and its answer carries no row. <b>Not cached</b>, for
/// <c>IBankTransferSettingsReader</c>'s reason turned round: a secret that has just been rotated must stop
/// working at once, not a minute from now.
/// </para>
/// </summary>
public interface IPayHereSettingsReader
{
    /// <summary>The settings as they are now. Never throws for a missing or unseeded row — it answers the defaults.</summary>
    Task<PayHereSettings> ReadAsync(CancellationToken cancellationToken);
}
