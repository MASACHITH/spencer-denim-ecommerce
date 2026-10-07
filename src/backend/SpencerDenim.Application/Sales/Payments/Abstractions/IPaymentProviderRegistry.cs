namespace SpencerDenim.Application.Sales.Payments.Abstractions;

/// <summary>
/// The <see cref="IPaymentProvider"/> implementations this build has, keyed by
/// <c>sales.PaymentProviders.Code</c> (PAY-03).
/// <para>
/// <b>Why a registry and not <c>IEnumerable&lt;IPaymentProvider&gt;</c> at every call site.</b> Two rules
/// have to hold together and neither is obvious from a list: the lookup must be
/// <b>case-insensitive</b>, because the code arrives from a <c>varchar</c> column in a
/// <c>Latin1_General_100_CI_AS_SC</c> database where <c>banktransfer</c> and <c>BankTransfer</c> are the
/// same row and an ordinal match would silently find neither; and two implementations claiming one code
/// must be a startup failure rather than a coin toss about which one takes the money. Both live here,
/// once.
/// </para>
/// <para>
/// <b>It answers "is there code for this provider", not "is it switched on".</b> Whether a provider may be
/// offered is <c>sales.PaymentProviders.IsEnabled</c>, which checkout already reads through
/// <c>ICheckoutReader.EnabledPaymentMethodsAsync</c>. The two questions are genuinely different and the
/// order matters: a row enabled in the database for a provider whose class does not exist must refuse the
/// checkout, and a provider whose class exists but whose row is disabled must not be offered. Doc 08 §3.5
/// seeds the PayPal row <b>disabled</b> precisely so the second case is the normal one until W4-01.
/// </para>
/// </summary>
public interface IPaymentProviderRegistry
{
    /// <summary>
    /// The implementation for <paramref name="code"/>, or <see langword="null"/> when this build has none.
    /// Matched case-insensitively, to agree with the database collation.
    /// </summary>
    IPaymentProvider? Find(string? code);

    /// <summary>
    /// Every code this build can actually take money with, in registration order. Used by the checkout
    /// refusal message and by <c>PaymentProviderContractTests</c>, which runs the PAY-03 contract over all
    /// of them so a new provider is covered the day it is registered rather than the day somebody
    /// remembers to copy a test.
    /// </summary>
    IReadOnlyList<string> ImplementedCodes { get; }
}
