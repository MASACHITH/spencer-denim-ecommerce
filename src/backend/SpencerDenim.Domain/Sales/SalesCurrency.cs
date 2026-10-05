namespace SpencerDenim.Domain.Sales;

/// <summary>
/// The currency the shop charges in (doc 08 §1.4: money is <c>decimal(18,2)</c> LKR, and
/// <c>Currency char(3)</c> appears only on Orders, Payments and Refunds, each with a <c>CK 'LKR'</c>).
/// <para>
/// <b>Why a CK rather than no column at all.</b> A single-currency shop does not need the column to
/// decide anything — but PayPal is charged in USD (D1), so a row that records money has to be explicit
/// about which of the two amounts it holds. The CK is what keeps the LKR column from quietly becoming
/// the USD one: the USD figure lives in <c>Payments.ProviderAmount</c> / <c>ProviderCurrency</c>, which
/// carries no CK precisely because it is the one that may differ.
/// </para>
/// </summary>
public static class SalesCurrency
{
    /// <summary>Sri Lankan rupee. The only value the <c>Currency</c> columns accept.</summary>
    public const string Lkr = "LKR";

    /// <summary>What PayPal is charged in (D1). Stored only in the <c>Provider*</c> columns.</summary>
    public const string Usd = "USD";

    /// <summary>ISO 4217 is three characters, so the columns are <c>char(3)</c>, not <c>varchar</c>.</summary>
    public const int CodeLength = 3;
}
