using SpencerDenim.Domain.Common;

namespace SpencerDenim.Domain.Sales;

/// <summary>
/// <c>sales.PaymentProviders</c> (doc 08 §3.5, PAY-01/03) [A] [RV].
/// <para>
/// <b>Its primary key is <see cref="Code"/>, not an int</b>, and doc 08 §1.3's "natural keys are
/// unique alternate keys, never PKs" has an explicit exception here: §3.5 says "a natural key is used
/// deliberately: a tiny, code-matched list referenced by FKs from Orders and Payments". Code matches
/// the provider the application dispatches on (<c>IPaymentProvider</c>, step 2), so an int key would
/// add a lookup to every read without making anything safer.
/// </para>
/// <para>
/// <b>Credentials are not here.</b> Doc 08 §3.5: they live in <c>ops.Settings</c>. This row is the
/// on/off switch and the checkout copy.
/// </para>
/// </summary>
public sealed class PaymentProvider : IAuditable
{
    /// <summary>
    /// <c>varchar(30)</c> PK. Doc 08 §1.4 calls a payment provider an "open-ended code", so new
    /// gateways are rows, not enum members. <see cref="PaymentProviderCodes"/> names the two v1 values.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Doc 08 §3.5: <b>either provider can be switched off</b>, and checkout lists enabled providers
    /// only. It defaults to false so a row inserted before its settings are valid cannot start taking
    /// money (§3.5: "enabling a provider requires its settings to be valid").
    /// </summary>
    public bool IsEnabled { get; set; }

    public short DisplayOrder { get; set; }

    public string? CheckoutDescription { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }

    /// <summary>Optimistic concurrency token exchanged with the admin API as an ETag (doc 08 §1.5).</summary>
    public byte[]? RowVersion { get; set; }
}

/// <summary>
/// The two <c>sales.PaymentProviders.Code</c> values v1 ships with (doc 08 §3.5).
/// <para>
/// They are constants rather than an enum because the column is an open-ended code (§1.4): a third
/// gateway is a seeded row and an <c>IPaymentProvider</c> implementation, not a schema change. They are
/// named here because SQL needs them as literals — <c>IX_Payments_BankQueue</c> is filtered on
/// <c>ProviderCode = 'BankTransfer'</c>, and a filtered index is only matched when the predicate is a
/// literal (doc 08 §1.6, DB28), so the string has to be compile-time constant on both sides.
/// </para>
/// </summary>
public static class PaymentProviderCodes
{
    /// <summary>Bank / online transfer, reviewed by staff (doc 11 P7). The v1 default.</summary>
    public const string BankTransfer = "BankTransfer";

    /// <summary>PayPal, built last behind <c>IPaymentProvider</c>.</summary>
    public const string PayPal = "PayPal";

    /// <summary>
    /// PayHere, a Sri Lankan card, wallet and internet-banking gateway that charges in LKR (doc 11 P8b,
    /// issue #141). A change request outside the signed scope (doc 11 §7 G-43): the row ships disabled.
    /// </summary>
    public const string PayHere = "PayHere";

    /// <summary>Doc 08 §3.5's maximum length for the code column, shared by every FK that copies it.</summary>
    public const int MaxCodeLength = 30;
}
