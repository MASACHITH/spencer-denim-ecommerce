using System.Globalization;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Checkout;
using SpencerDenim.Application.Settings;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Reference;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Delivery.Admin;

/// <summary>The admin Overseas delivery screen (owner decision 2026-10-21): the switch, the regions, the countries.</summary>
/// <param name="InternationalEnabled">Whether International delivery is switched on at all.</param>
/// <param name="InternationalRowVersion">The International delivery method's token, base64; null when the row is missing.</param>
/// <param name="Regions">The overseas regions, in their sort order.</param>
/// <param name="Countries">Every country except Sri Lanka, by name.</param>
public sealed record OverseasDeliveryAdminDto(
    bool InternationalEnabled,
    string? InternationalRowVersion,
    IReadOnlyList<DeliveryRegionAdminDto> Regions,
    IReadOnlyList<CountryDeliveryAdminDto> Countries);

/// <summary>One region, with how many countries it covers.</summary>
public sealed record DeliveryRegionAdminDto(
    string Code,
    string Name,
    decimal? Fee,
    byte EstimatedDaysMin,
    byte EstimatedDaysMax,
    decimal? ExpressFee,
    byte ExpressDaysMin,
    byte ExpressDaysMax,
    bool IsEnabled,
    int CountryCount,
    string RowVersion);

/// <summary>One country: its own settings, and what a shopper is actually charged once its region is applied.</summary>
/// <param name="Refusal">
/// Why standard delivery is unavailable (<c>MethodDisabled</c>, <c>CountryDisabled</c>, <c>RegionDisabled</c>,
/// <c>NotPriced</c>), or <c>None</c>.
/// </param>
/// <param name="ExpressRefusal">
/// Why express delivery is unavailable, or <c>None</c>. When <c>ExpressNotPriced</c> the tier is simply
/// not configured for this country/region and is not offered.
/// </param>
public sealed record CountryDeliveryAdminDto(
    string Code,
    string Name,
    string RegionCode,
    bool IsShippingEnabled,
    decimal? DeliveryFeeOverride,
    byte? EstimatedDaysMinOverride,
    byte? EstimatedDaysMaxOverride,
    decimal? ExpressFeeOverride,
    byte? ExpressDaysMinOverride,
    byte? ExpressDaysMaxOverride,
    bool PostalCodeRequired,
    bool RegionRequired,
    bool IsAvailable,
    decimal? EffectiveFee,
    byte? EffectiveDaysMin,
    byte? EffectiveDaysMax,
    decimal? EffectiveExpressFee,
    byte? EffectiveExpressDaysMin,
    byte? EffectiveExpressDaysMax,
    string Refusal,
    string ExpressRefusal,
    string RowVersion);

/// <summary>What a write on the Overseas delivery screen came to.</summary>
public abstract record OverseasDeliveryWriteResult
{
    private OverseasDeliveryWriteResult()
    {
    }

    /// <summary>Written (or already as asked). The screen's new state.</summary>
    public sealed record Saved(OverseasDeliveryAdminDto Screen) : OverseasDeliveryWriteResult;

    public sealed record Invalid(IReadOnlyList<ValidationError> Errors) : OverseasDeliveryWriteResult;

    public sealed record NotFound : OverseasDeliveryWriteResult;

    /// <summary>The row changed since it was read, or the request carried no token.</summary>
    public sealed record Conflict(string Message) : OverseasDeliveryWriteResult;
}

/// <summary>Reads and writes the two overseas tables and the International delivery switch.</summary>
public interface IOverseasDeliveryAdminStore
{
    Task<OverseasDeliveryAdminDto> ReadAsync(CancellationToken cancellationToken);

    /// <summary>The region, tracked, with <paramref name="expectedRowVersion"/> as the concurrency token.</summary>
    Task<DeliveryRegion?> FindRegionForUpdateAsync(
        string code,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken);

    /// <summary>The overseas country, tracked, with the token. Null for Sri Lanka or an unknown code.</summary>
    Task<Country?> FindOverseasCountryForUpdateAsync(
        string code,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken);

    /// <summary>The International delivery method, tracked, with the token.</summary>
    Task<DeliveryMethod?> FindInternationalMethodForUpdateAsync(
        byte[] expectedRowVersion,
        CancellationToken cancellationToken);

    /// <summary>Saves the tracked change and the enlisted audit rows; false on a concurrency conflict.</summary>
    Task<bool> SaveAsync(CancellationToken cancellationToken);
}

/// <summary>The screen as it stands.</summary>
public sealed record GetOverseasDeliveryAdminQuery;

public sealed class GetOverseasDeliveryAdminQueryHandler(IOverseasDeliveryAdminStore store)
    : IQueryHandler<GetOverseasDeliveryAdminQuery, OverseasDeliveryAdminDto>
{
    public Task<OverseasDeliveryAdminDto> HandleAsync(
        GetOverseasDeliveryAdminQuery query,
        CancellationToken cancellationToken) =>
        store.ReadAsync(cancellationToken);
}

/// <summary>Replaces one region's fee, estimate and switch. Every field is required; a null fee means "not priced".</summary>
public sealed record UpdateDeliveryRegionCommand(
    string Code,
    decimal? Fee,
    int? EstimatedDaysMin,
    int? EstimatedDaysMax,
    decimal? ExpressFee,
    int? ExpressDaysMin,
    int? ExpressDaysMax,
    bool? IsEnabled,
    string? RowVersion,
    AdminActor Actor);

/// <summary>Replaces one country's switch and overrides. Null overrides mean "use the region's".</summary>
public sealed record UpdateCountryDeliveryCommand(
    string Code,
    bool? IsShippingEnabled,
    decimal? DeliveryFeeOverride,
    int? EstimatedDaysMinOverride,
    int? EstimatedDaysMaxOverride,
    decimal? ExpressFeeOverride,
    int? ExpressDaysMinOverride,
    int? ExpressDaysMaxOverride,
    string? RowVersion,
    AdminActor Actor);

/// <summary>Switches International delivery — every overseas order — on or off.</summary>
public sealed record SetInternationalDeliveryCommand(bool? IsEnabled, string? RowVersion, AdminActor Actor);

/// <summary>
/// The three writes of the Overseas delivery screen (owner decision 2026-10-21; <c>Settings.Edit</c>, the
/// permission a district's zone is edited under). Each is audited with its before and after values in the same
/// transaction, and each is guarded by the row's own <c>rowversion</c>, so two staff editing one region cannot
/// both win.
/// </summary>
public sealed class OverseasDeliveryCommandHandler(
    IOverseasDeliveryAdminStore store,
    IAuditLogWriter auditLog,
    IClock clock)
    : ICommandHandler<UpdateDeliveryRegionCommand, OverseasDeliveryWriteResult>,
      ICommandHandler<UpdateCountryDeliveryCommand, OverseasDeliveryWriteResult>,
      ICommandHandler<SetInternationalDeliveryCommand, OverseasDeliveryWriteResult>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string StaleMessage =
        "Somebody else changed this while you were editing it, so nothing was saved. Reload the page and try again.";

    private const string MissingTokenMessage =
        "This request did not carry the row's current version, so it cannot be applied safely. Reload the page.";

    public async Task<OverseasDeliveryWriteResult> HandleAsync(
        UpdateDeliveryRegionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();
        OverseasDeliveryRules.CheckFee(errors, "fee", command.Fee);
        OverseasDeliveryRules.CheckDays(errors, "estimatedDays", command.EstimatedDaysMin, command.EstimatedDaysMax, required: true);
        OverseasDeliveryRules.CheckFee(errors, "expressFee", command.ExpressFee);
        OverseasDeliveryRules.CheckDays(errors, "expressDays", command.ExpressDaysMin, command.ExpressDaysMax, required: command.ExpressFee is not null);

        if (command.IsEnabled is null)
        {
            errors.Add(new ValidationError("isEnabled", "Say whether this region is switched on."));
        }

        if (errors.Count > 0)
        {
            return new OverseasDeliveryWriteResult.Invalid(errors);
        }

        if (SettingFieldRules.ReadRowVersion(command.RowVersion) is not { } token)
        {
            return new OverseasDeliveryWriteResult.Conflict(MissingTokenMessage);
        }

        var region = await store.FindRegionForUpdateAsync(command.Code.Trim(), token, cancellationToken);
        if (region is null)
        {
            return new OverseasDeliveryWriteResult.NotFound();
        }

        var before = Snapshot(region);

        region.Fee = command.Fee;
        region.EstimatedDaysMin = (byte)command.EstimatedDaysMin!.Value;
        region.EstimatedDaysMax = (byte)command.EstimatedDaysMax!.Value;
        region.ExpressFee = command.ExpressFee;
        region.ExpressDaysMin = command.ExpressDaysMin.HasValue ? (byte)command.ExpressDaysMin.Value : region.ExpressDaysMin;
        region.ExpressDaysMax = command.ExpressDaysMax.HasValue ? (byte)command.ExpressDaysMax.Value : region.ExpressDaysMax;
        region.IsEnabled = command.IsEnabled!.Value;

        return await SaveAsync("DeliveryRegion", region.Code, before, Snapshot(region), command.Actor, cancellationToken);
    }

    public async Task<OverseasDeliveryWriteResult> HandleAsync(
        UpdateCountryDeliveryCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();

        if (command.DeliveryFeeOverride is not null)
        {
            OverseasDeliveryRules.CheckFee(errors, "deliveryFeeOverride", command.DeliveryFeeOverride);
        }

        OverseasDeliveryRules.CheckDays(
            errors,
            "estimatedDaysOverride",
            command.EstimatedDaysMinOverride,
            command.EstimatedDaysMaxOverride,
            required: false);

        if (command.ExpressFeeOverride is not null)
        {
            OverseasDeliveryRules.CheckFee(errors, "expressFeeOverride", command.ExpressFeeOverride);
        }

        OverseasDeliveryRules.CheckDays(
            errors,
            "expressDaysOverride",
            command.ExpressDaysMinOverride,
            command.ExpressDaysMaxOverride,
            required: false);

        if (command.IsShippingEnabled is null)
        {
            errors.Add(new ValidationError("isShippingEnabled", "Say whether this country is delivered to."));
        }

        if (errors.Count > 0)
        {
            return new OverseasDeliveryWriteResult.Invalid(errors);
        }

        if (SettingFieldRules.ReadRowVersion(command.RowVersion) is not { } token)
        {
            return new OverseasDeliveryWriteResult.Conflict(MissingTokenMessage);
        }

        var code = DestinationRules.NormaliseCountryCode(command.Code);
        var country = code is null
            ? null
            : await store.FindOverseasCountryForUpdateAsync(code, token, cancellationToken);
        if (country is null)
        {
            return new OverseasDeliveryWriteResult.NotFound();
        }

        var before = Snapshot(country);

        country.IsShippingEnabled = command.IsShippingEnabled!.Value;
        country.DeliveryFeeOverride = command.DeliveryFeeOverride;
        country.EstimatedDaysMinOverride = (byte?)command.EstimatedDaysMinOverride;
        country.EstimatedDaysMaxOverride = (byte?)command.EstimatedDaysMaxOverride;
        country.ExpressFeeOverride = command.ExpressFeeOverride;
        country.ExpressDaysMinOverride = (byte?)command.ExpressDaysMinOverride;
        country.ExpressDaysMaxOverride = (byte?)command.ExpressDaysMaxOverride;

        return await SaveAsync("Country", country.Code, before, Snapshot(country), command.Actor, cancellationToken);
    }

    public async Task<OverseasDeliveryWriteResult> HandleAsync(
        SetInternationalDeliveryCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.IsEnabled is null)
        {
            return new OverseasDeliveryWriteResult.Invalid(
                [new ValidationError("isEnabled", "Say whether International delivery is switched on.")]);
        }

        if (SettingFieldRules.ReadRowVersion(command.RowVersion) is not { } token)
        {
            return new OverseasDeliveryWriteResult.Conflict(MissingTokenMessage);
        }

        var method = await store.FindInternationalMethodForUpdateAsync(token, cancellationToken);
        if (method is null)
        {
            return new OverseasDeliveryWriteResult.NotFound();
        }

        var before = new Dictionary<string, object?>(StringComparer.Ordinal) { ["isEnabled"] = method.IsEnabled };
        method.IsEnabled = command.IsEnabled.Value;
        var after = new Dictionary<string, object?>(StringComparer.Ordinal) { ["isEnabled"] = method.IsEnabled };

        return await SaveAsync("DeliveryMethod", method.Code, before, after, command.Actor, cancellationToken);
    }

    private async Task<OverseasDeliveryWriteResult> SaveAsync(
        string entityType,
        string entityId,
        Dictionary<string, object?> before,
        Dictionary<string, object?> after,
        AdminActor actor,
        CancellationToken cancellationToken)
    {
        var beforeJson = JsonSerializer.Serialize(before, JsonOptions);
        var afterJson = JsonSerializer.Serialize(after, JsonOptions);

        // Nothing changed: no audit row and nothing written. A change is written with the caller's token as the
        // row's original version, so a stale screen is refused rather than overwriting a newer edit.
        if (!string.Equals(beforeJson, afterJson, StringComparison.Ordinal))
        {
            auditLog.Enlist(new AuditLog
            {
                OccurredAtUtc = clock.UtcNow.UtcDateTime,
                UserId = actor.UserId,
                UserEmail = actor.Email,
                Action = AuditActions.SettingChange,
                EntityType = entityType,
                EntityId = entityId,
                BeforeJson = beforeJson,
                AfterJson = afterJson,
                IpAddress = actor.IpAddress,
                UserAgent = actor.UserAgent,
                CorrelationId = actor.CorrelationId,
            });
        }

        if (!await store.SaveAsync(cancellationToken))
        {
            return new OverseasDeliveryWriteResult.Conflict(StaleMessage);
        }

        return new OverseasDeliveryWriteResult.Saved(await store.ReadAsync(cancellationToken));
    }

    private static Dictionary<string, object?> Snapshot(DeliveryRegion region) =>
        new(StringComparer.Ordinal)
        {
            ["fee"] = region.Fee?.ToString("0.00", CultureInfo.InvariantCulture),
            ["estimatedDaysMin"] = region.EstimatedDaysMin,
            ["estimatedDaysMax"] = region.EstimatedDaysMax,
            ["expressFee"] = region.ExpressFee?.ToString("0.00", CultureInfo.InvariantCulture),
            ["expressDaysMin"] = region.ExpressDaysMin,
            ["expressDaysMax"] = region.ExpressDaysMax,
            ["isEnabled"] = region.IsEnabled,
        };

    private static Dictionary<string, object?> Snapshot(Country country) =>
        new(StringComparer.Ordinal)
        {
            ["isShippingEnabled"] = country.IsShippingEnabled,
            ["deliveryFeeOverride"] = country.DeliveryFeeOverride?.ToString("0.00", CultureInfo.InvariantCulture),
            ["estimatedDaysMinOverride"] = country.EstimatedDaysMinOverride,
            ["estimatedDaysMaxOverride"] = country.EstimatedDaysMaxOverride,
            ["expressFeeOverride"] = country.ExpressFeeOverride?.ToString("0.00", CultureInfo.InvariantCulture),
            ["expressDaysMinOverride"] = country.ExpressDaysMinOverride,
            ["expressDaysMaxOverride"] = country.ExpressDaysMaxOverride,
        };
}

/// <summary>The bounds an overseas fee and estimate are held to. Public so tests and the stub can share them.</summary>
public static class OverseasDeliveryRules
{
    /// <summary>Rs. 1,000,000: past this a fee is a typo, not a parcel.</summary>
    public const decimal MaxFee = 1_000_000m;

    public const int MinDays = 1;

    /// <summary>Ninety days: past this an estimate is not one.</summary>
    public const int MaxDays = 90;

    /// <summary>A fee: null (not priced, regions only) or 0 to <see cref="MaxFee"/>, to the cent.</summary>
    public static void CheckFee(List<ValidationError> errors, string field, decimal? fee)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (fee is not { } value)
        {
            return;
        }

        if (value < 0m || value > MaxFee)
        {
            errors.Add(new ValidationError(field, "Enter a fee from Rs. 0 to Rs. 1,000,000."));
        }
        else if (decimal.Round(value, 2) != value)
        {
            errors.Add(new ValidationError(field, "A fee can have at most two decimal places."));
        }
    }

    /// <summary>
    /// An estimate: both days or (when not <paramref name="required"/>) neither, each 1 to 90, the first no
    /// more than the second.
    /// </summary>
    public static void CheckDays(List<ValidationError> errors, string field, int? min, int? max, bool required)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (min is null && max is null)
        {
            if (required)
            {
                errors.Add(new ValidationError(field, "Enter the estimated delivery days."));
            }

            return;
        }

        if (min is not { } low || max is not { } high)
        {
            errors.Add(new ValidationError(field, "Enter both the fewest and the most days, or neither."));
            return;
        }

        if (low < MinDays || high > MaxDays || low > high)
        {
            errors.Add(new ValidationError(
                field,
                $"Enter days from {MinDays} to {MaxDays}, with the fewest no more than the most."));
        }
    }
}
