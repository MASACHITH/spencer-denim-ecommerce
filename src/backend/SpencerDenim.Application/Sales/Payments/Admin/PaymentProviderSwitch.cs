using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Settings;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Payments.Admin;

/// <summary>
/// One payment method on the settings screen's Payment methods panel (owner decision 2026-10-21: bank transfer
/// switched off, PayPal the only payment method, "block only for now").
/// </summary>
/// <param name="Code"><c>sales.PaymentProviders.Code</c>.</param>
/// <param name="DisplayName">What checkout calls it.</param>
/// <param name="IsEnabled">The shop's switch.</param>
/// <param name="IsReady">
/// Whether the method could take a payment right now if switched on — PayPal needs its credentials and a
/// fresh rate, bank transfer needs nothing more. A switched-on method that is not ready is not offered.
/// </param>
/// <param name="IsOffered">Switched on <b>and</b> ready: what checkout lists.</param>
/// <param name="RowVersion">The row's token, base64.</param>
public sealed record PaymentProviderAdminDto(
    string Code,
    string DisplayName,
    bool IsEnabled,
    bool IsReady,
    bool IsOffered,
    string RowVersion);

/// <summary>Every payment method, in checkout's order, and whether checkout can take an order at all.</summary>
/// <param name="AnyOffered">False when no method is offered: checkout then refuses every order.</param>
public sealed record PaymentProvidersAdminDto(IReadOnlyList<PaymentProviderAdminDto> Providers, bool AnyOffered);

/// <summary>One provider row as the store reads it.</summary>
public sealed record PaymentProviderRow(string Code, string DisplayName, bool IsEnabled, byte[] RowVersion);

/// <summary>Reads and writes <c>sales.PaymentProviders.IsEnabled</c>.</summary>
public interface IPaymentProviderAdminStore
{
    Task<IReadOnlyList<PaymentProviderRow>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The row, tracked, with <paramref name="expectedRowVersion"/> as its concurrency token.</summary>
    Task<PaymentProvider?> FindForUpdateAsync(
        string code,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken);

    /// <summary>Saves the change, its audit row and its alert together; false on a concurrency conflict.</summary>
    Task<bool> SaveAsync(CancellationToken cancellationToken);
}

/// <summary>The panel.</summary>
public sealed record GetPaymentProvidersAdminQuery;

/// <summary>Switch one payment method on or off.</summary>
public sealed record SetPaymentProviderEnabledCommand(
    string Code,
    bool? IsEnabled,
    string? RowVersion,
    AdminActor Actor);

/// <summary>What the switch came to.</summary>
public abstract record PaymentProviderWriteResult
{
    private PaymentProviderWriteResult()
    {
    }

    public sealed record Saved(PaymentProvidersAdminDto Panel) : PaymentProviderWriteResult;

    public sealed record Invalid(IReadOnlyList<ValidationError> Errors) : PaymentProviderWriteResult;

    public sealed record NotFound : PaymentProviderWriteResult;

    public sealed record Forbidden : PaymentProviderWriteResult;

    public sealed record Conflict(string Message) : PaymentProviderWriteResult;
}

/// <summary>
/// The Payment methods switch (owner decision 2026-10-21). <b>Super Admin only</b> — <c>Settings.Payments</c>,
/// which no other default role holds — checked here as well as on the route, audited with the before and after
/// value, and raising the <c>SensitiveSettingsChanged</c> alert for every Super Admin in the same transaction,
/// like every other payment setting (doc 08 §3.8 DB2).
/// </summary>
/// <remarks>
/// <b>Switching on requires the method to be ready</b> (doc 08 §3.5: "enabling a provider requires its settings
/// to be valid"). Switching off is always allowed — including the last one, which is the owner's intended state
/// until PayPal is configured: checkout then refuses every order with "No payment method is available", and the
/// panel says so. Existing orders are untouched either way: the switch decides what checkout offers, not how a
/// placed order is paid, so bank-transfer orders already placed and the staff approval queue keep working.
/// </remarks>
public sealed class PaymentProviderSwitchHandler(
    IPaymentProviderAdminStore store,
    IPaymentProviderRegistry registry,
    IAuditLogWriter auditLog,
    IAdminAlertWriter alerts,
    IEffectivePermissionsProvider permissionsProvider,
    IClock clock)
    : IQueryHandler<GetPaymentProvidersAdminQuery, PaymentProvidersAdminDto>,
      ICommandHandler<SetPaymentProviderEnabledCommand, PaymentProviderWriteResult>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PaymentProvidersAdminDto> HandleAsync(
        GetPaymentProvidersAdminQuery query,
        CancellationToken cancellationToken)
    {
        var rows = await store.ListAsync(cancellationToken);
        var providers = new List<PaymentProviderAdminDto>(rows.Count);

        foreach (var row in rows)
        {
            var ready = await IsReadyAsync(row.Code, cancellationToken);

            providers.Add(new PaymentProviderAdminDto(
                row.Code,
                row.DisplayName,
                row.IsEnabled,
                ready,
                row.IsEnabled && ready,
                Convert.ToBase64String(row.RowVersion)));
        }

        return new PaymentProvidersAdminDto(providers.AsReadOnly(), providers.Any(provider => provider.IsOffered));
    }

    public async Task<PaymentProviderWriteResult> HandleAsync(
        SetPaymentProviderEnabledCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.IsEnabled is not { } wanted)
        {
            return new PaymentProviderWriteResult.Invalid(
                [new ValidationError("isEnabled", "Say whether this payment method is switched on.")]);
        }

        // The route requires Settings.Payments; asked again here so the rule holds for any other caller.
        if (command.Actor.UserId is not { } userId
            || !(await permissionsProvider.GetEffectivePermissionsAsync(userId, cancellationToken))
                .Has(Permissions.Settings.Payments))
        {
            return new PaymentProviderWriteResult.Forbidden();
        }

        if (SettingFieldRules.ReadRowVersion(command.RowVersion) is not { } token)
        {
            return new PaymentProviderWriteResult.Conflict(
                "This request did not carry the payment method's current version. Reload the page and try again.");
        }

        var provider = await store.FindForUpdateAsync(command.Code.Trim(), token, cancellationToken);
        if (provider is null)
        {
            return new PaymentProviderWriteResult.NotFound();
        }

        if (wanted && !provider.IsEnabled && !await IsReadyAsync(provider.Code, cancellationToken))
        {
            return new PaymentProviderWriteResult.Invalid(
            [
                new ValidationError(
                    "isEnabled",
                    $"{provider.DisplayName} is not ready to take payments yet. Finish its settings first "
                    + "(for PayPal: the client ID, secret, webhook ID and today's exchange rate)."),
            ]);
        }

        if (provider.IsEnabled != wanted)
        {
            var now = clock.UtcNow.UtcDateTime;
            var key = $"PaymentProviders.{provider.Code}.IsEnabled";

            auditLog.Enlist(new AuditLog
            {
                OccurredAtUtc = now,
                UserId = command.Actor.UserId,
                UserEmail = command.Actor.Email,
                Action = AuditActions.SettingChange,
                EntityType = "PaymentProvider",
                EntityId = provider.Code,
                BeforeJson = Json(provider.IsEnabled),
                AfterJson = Json(wanted),
                IpAddress = command.Actor.IpAddress,
                UserAgent = command.Actor.UserAgent,
                CorrelationId = command.Actor.CorrelationId,
            });

            alerts.Enlist(SensitiveSettingsChangedAlert.For("Payment methods", [key], command.Actor.UserId, now));

            provider.IsEnabled = wanted;
        }

        // A change is written with the caller's token as the row's original version, so a stale screen is
        // refused rather than overwriting a newer choice. Asking for the state it is already in writes nothing.
        if (!await store.SaveAsync(cancellationToken))
        {
            return new PaymentProviderWriteResult.Conflict(
                "Somebody else changed this payment method while you were editing it, so nothing was saved. "
                + "Reload the page and try again.");
        }

        return new PaymentProviderWriteResult.Saved(
            await HandleAsync(new GetPaymentProvidersAdminQuery(), cancellationToken));
    }

    private async Task<bool> IsReadyAsync(string code, CancellationToken cancellationToken) =>
        registry.Find(code) is { } implementation && await implementation.IsAvailableAsync(cancellationToken);

    private static string Json(bool isEnabled) =>
        JsonSerializer.Serialize(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["isEnabled"] = isEnabled },
            JsonOptions);
}
