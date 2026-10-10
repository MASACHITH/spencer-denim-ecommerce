using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Ops.Email;

namespace SpencerDenim.Application.Sales.Payments.PayHere;

/// <summary>
/// What PayHere needs before checkout may offer it, as sentences a Super Admin can act on (doc 11 P8b
/// preconditions). One definition, used by the provider to decide whether to be offered and by the settings
/// screen to say why it is not — so the screen can never claim PayHere is ready while checkout hides it.
/// </summary>
public static class PayHereReadiness
{
    /// <summary>
    /// The reasons PayHere cannot take an order right now, ignoring whether the provider row is switched on.
    /// Empty when it can.
    /// </summary>
    public static IReadOnlyList<string> Problems(PayHereSettings settings, string? publicOrigin)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var problems = new List<string>(4);

        if (settings.CheckoutUrl is null)
        {
            problems.Add("The mode is neither sandbox nor live.");
        }

        if (string.IsNullOrWhiteSpace(settings.MerchantId))
        {
            problems.Add("The merchant ID is not set.");
        }

        if (string.IsNullOrEmpty(settings.MerchantSecret))
        {
            problems.Add("The merchant secret is not set.");
        }

        if (PayHereRoutes.Origin(publicOrigin) is null)
        {
            problems.Add(
                "The shop's public web address is not configured on the server (Email__StorefrontBaseUrl), "
                + "so PayHere cannot be given a return address or a notify address.");
        }

        return problems;
    }
}

/// <summary>Reads whether the <c>PayHere</c> row of <c>sales.PaymentProviders</c> is switched on.</summary>
public interface IPayHereProviderSwitch
{
    /// <summary><see langword="true"/> when the row exists and <c>IsEnabled</c> is set.</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken);
}

/// <summary>Asks for the Super Admin's view of PayHere's readiness.</summary>
public sealed record GetPayHereStatusQuery;

/// <summary>
/// PayHere's readiness, for the settings screen.
/// </summary>
/// <param name="NotifyUrl">
/// The address to register as the notify URL in the PayHere portal, or <see langword="null"/> when the server
/// has no public address configured.
/// </param>
/// <param name="ProviderEnabled">Whether the provider row is switched on.</param>
/// <param name="OfferedAtCheckout">Whether checkout offers PayHere right now: switched on and nothing missing.</param>
/// <param name="Mode"><c>sandbox</c>, <c>live</c> or null.</param>
/// <param name="Problems">What is missing, in plain sentences.</param>
public sealed record PayHereStatusDto(
    string? NotifyUrl,
    bool ProviderEnabled,
    bool OfferedAtCheckout,
    string? Mode,
    IReadOnlyList<string> Problems);

/// <summary>Builds <see cref="PayHereStatusDto"/>. Never reads or returns the secret.</summary>
public sealed class GetPayHereStatusQueryHandler(
    IPayHereSettingsReader settings,
    IPayHereProviderSwitch providerSwitch,
    EmailOptions options)
    : IQueryHandler<GetPayHereStatusQuery, PayHereStatusDto>
{
    public async Task<PayHereStatusDto> HandleAsync(
        GetPayHereStatusQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var current = await settings.ReadAsync(cancellationToken);
        var enabled = await providerSwitch.IsEnabledAsync(cancellationToken);
        var origin = options.NormalisedStorefrontBaseUrl;
        var problems = PayHereReadiness.Problems(current, origin);

        return new PayHereStatusDto(
            PayHereRoutes.NotifyUrl(origin),
            enabled,
            enabled && problems.Count == 0,
            current.Mode,
            problems);
    }
}
