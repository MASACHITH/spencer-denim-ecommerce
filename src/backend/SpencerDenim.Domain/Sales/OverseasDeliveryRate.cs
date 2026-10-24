namespace SpencerDenim.Domain.Sales;

/// <summary>Why an overseas destination cannot be ordered to, or <see cref="None"/> when it can.</summary>
public enum OverseasRefusal : byte
{
    None = 0,

    /// <summary>The International delivery method is switched off: no overseas orders at all.</summary>
    MethodDisabled = 1,

    /// <summary>The country is switched off.</summary>
    CountryDisabled = 2,

    /// <summary>The country's region is switched off.</summary>
    RegionDisabled = 3,

    /// <summary>Neither the country nor its region carries a fee.</summary>
    NotPriced = 4,

    /// <summary>Neither the country nor its region carries an express fee.</summary>
    ExpressNotPriced = 5,
}

/// <summary>The facts one overseas standard quote is made from, exactly as the two tables hold them.</summary>
/// <param name="MethodEnabled"><c>DeliveryMethods.IsEnabled</c> for <see cref="DeliveryMethod.InternationalCode"/>.</param>
/// <param name="CountryEnabled"><c>Countries.IsShippingEnabled</c>.</param>
/// <param name="CountryFee"><c>Countries.DeliveryFeeOverride</c>.</param>
/// <param name="CountryDaysMin"><c>Countries.EstimatedDaysMinOverride</c>.</param>
/// <param name="CountryDaysMax"><c>Countries.EstimatedDaysMaxOverride</c>.</param>
/// <param name="RegionEnabled"><c>DeliveryRegions.IsEnabled</c>.</param>
/// <param name="RegionFee"><c>DeliveryRegions.Fee</c>.</param>
/// <param name="RegionDaysMin"><c>DeliveryRegions.EstimatedDaysMin</c>.</param>
/// <param name="RegionDaysMax"><c>DeliveryRegions.EstimatedDaysMax</c>.</param>
public readonly record struct OverseasRateInputs(
    bool MethodEnabled,
    bool CountryEnabled,
    decimal? CountryFee,
    byte? CountryDaysMin,
    byte? CountryDaysMax,
    bool RegionEnabled,
    decimal? RegionFee,
    byte RegionDaysMin,
    byte RegionDaysMax);

/// <summary>The facts one overseas express quote is made from, mirroring <see cref="OverseasRateInputs"/> for express fields.</summary>
/// <param name="MethodEnabled"><c>DeliveryMethods.IsEnabled</c> for <see cref="DeliveryMethod.InternationalCode"/>.</param>
/// <param name="CountryEnabled"><c>Countries.IsShippingEnabled</c>.</param>
/// <param name="CountryExpressFee"><c>Countries.ExpressFeeOverride</c>.</param>
/// <param name="CountryExpressDaysMin"><c>Countries.ExpressDaysMinOverride</c>.</param>
/// <param name="CountryExpressDaysMax"><c>Countries.ExpressDaysMaxOverride</c>.</param>
/// <param name="RegionEnabled"><c>DeliveryRegions.IsEnabled</c>.</param>
/// <param name="RegionExpressFee"><c>DeliveryRegions.ExpressFee</c>.</param>
/// <param name="RegionExpressDaysMin"><c>DeliveryRegions.ExpressDaysMin</c>.</param>
/// <param name="RegionExpressDaysMax"><c>DeliveryRegions.ExpressDaysMax</c>.</param>
public readonly record struct OverseasExpressInputs(
    bool MethodEnabled,
    bool CountryEnabled,
    decimal? CountryExpressFee,
    byte? CountryExpressDaysMin,
    byte? CountryExpressDaysMax,
    bool RegionEnabled,
    decimal? RegionExpressFee,
    byte RegionExpressDaysMin,
    byte RegionExpressDaysMax);

/// <summary>An overseas destination's price and estimate, or the reason there is none.</summary>
public readonly record struct OverseasQuote(
    OverseasRefusal Refusal,
    decimal Fee,
    byte EstimatedDaysMin,
    byte EstimatedDaysMax)
{
    public bool IsAvailable => Refusal == OverseasRefusal.None;
}

/// <summary>
/// The one place the overseas delivery fee is decided (owner decision 2026-10-21): the country's own
/// override when it has one, otherwise its region's — and only when the method, the country and the region
/// are all switched on and a fee exists.
/// <para>
/// <b>Pure, so it can be tested without a database and so checkout, the bag estimate and the order agree.</b>
/// The estimate follows the same rule as a pair: a country that overrides its days overrides both, which
/// <c>CK_Countries_DaysOverride</c> enforces, so a country can never mix its own minimum with the region's
/// maximum. There is no free-above threshold overseas.
/// </para>
/// <para>
/// Owner decision 2026-10-23: Standard and Express tiers. <see cref="Resolve"/> handles the standard tier;
/// <see cref="ResolveExpress"/> handles express. If a country has no rate for a tier that tier is not
/// offered; if a country has neither tier configured checkout is blocked.
/// </para>
/// </summary>
public static class OverseasDeliveryRate
{
    public static OverseasQuote Resolve(OverseasRateInputs inputs)
    {
        if (!inputs.MethodEnabled)
        {
            return Refused(OverseasRefusal.MethodDisabled);
        }

        if (!inputs.CountryEnabled)
        {
            return Refused(OverseasRefusal.CountryDisabled);
        }

        if (!inputs.RegionEnabled)
        {
            return Refused(OverseasRefusal.RegionDisabled);
        }

        var fee = inputs.CountryFee ?? inputs.RegionFee;
        if (fee is not { } priced || priced < 0m)
        {
            return Refused(OverseasRefusal.NotPriced);
        }

        var (min, max) = inputs is { CountryDaysMin: { } ownMin, CountryDaysMax: { } ownMax }
            ? (ownMin, ownMax)
            : (inputs.RegionDaysMin, inputs.RegionDaysMax);

        return new OverseasQuote(OverseasRefusal.None, priced, min, max);
    }

    /// <summary>
    /// Express-tier counterpart to <see cref="Resolve"/>. Uses the same enabled-checks as the standard tier
    /// (method/country/region gates), then falls back to <see cref="OverseasRefusal.ExpressNotPriced"/>
    /// rather than <see cref="OverseasRefusal.NotPriced"/> when no express fee is configured.
    /// </summary>
    public static OverseasQuote ResolveExpress(OverseasExpressInputs inputs)
    {
        if (!inputs.MethodEnabled)
        {
            return Refused(OverseasRefusal.MethodDisabled);
        }

        if (!inputs.CountryEnabled)
        {
            return Refused(OverseasRefusal.CountryDisabled);
        }

        if (!inputs.RegionEnabled)
        {
            return Refused(OverseasRefusal.RegionDisabled);
        }

        var fee = inputs.CountryExpressFee ?? inputs.RegionExpressFee;
        if (fee is not { } priced || priced < 0m)
        {
            return Refused(OverseasRefusal.ExpressNotPriced);
        }

        var (min, max) = inputs is { CountryExpressDaysMin: { } ownMin, CountryExpressDaysMax: { } ownMax }
            ? (ownMin, ownMax)
            : (inputs.RegionExpressDaysMin, inputs.RegionExpressDaysMax);

        return new OverseasQuote(OverseasRefusal.None, priced, min, max);
    }

    private static OverseasQuote Refused(OverseasRefusal refusal) => new(refusal, 0m, 0, 0);
}
