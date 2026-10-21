using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.Admin;
using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Application.Sales;

/// <summary>
/// The Payment methods switch's own rules (owner decision 2026-10-21), without the route in front of it: the
/// handler refuses anyone without <c>Settings.Payments</c> even if a future caller forgets the route guard, and a
/// permitted change writes one audit row and one alert.
/// </summary>
public sealed class PaymentProviderSwitchHandlerTests
{
    private static readonly AdminActor Actor = new(7, "owner@example.com", null, null, null);

    [Theory]
    [InlineData(Permissions.Settings.View)]
    [InlineData(Permissions.Settings.Edit)]
    public async Task Without_Settings_Payments_the_switch_is_refused_and_nothing_changes(string held)
    {
        var (handler, store, audit, alerts) = Build(new EffectivePermissions(false, [held]));

        var result = await handler.HandleAsync(
            new SetPaymentProviderEnabledCommand(PaymentProviderCodes.BankTransfer, false, "AAAAAAAAAAE=", Actor),
            TestContext.Current.CancellationToken);

        Assert.IsType<PaymentProviderWriteResult.Forbidden>(result);
        Assert.True(store.Row.IsEnabled);
        Assert.False(store.Saved);
        Assert.Empty(audit.Entries);
        Assert.Empty(alerts.Entries);
    }

    [Fact]
    public async Task An_actor_with_no_user_id_is_refused()
    {
        var (handler, store, _, _) = Build(new EffectivePermissions(true, []));

        var result = await handler.HandleAsync(
            new SetPaymentProviderEnabledCommand(PaymentProviderCodes.BankTransfer, false, "AAAAAAAAAAE=", AdminActor.Unknown),
            TestContext.Current.CancellationToken);

        Assert.IsType<PaymentProviderWriteResult.Forbidden>(result);
        Assert.True(store.Row.IsEnabled);
    }

    [Fact]
    public async Task A_Super_Admin_switches_it_off_with_one_audit_row_and_one_alert()
    {
        var (handler, store, audit, alerts) = Build(new EffectivePermissions(true, []));

        var result = await handler.HandleAsync(
            new SetPaymentProviderEnabledCommand(PaymentProviderCodes.BankTransfer, false, "AAAAAAAAAAE=", Actor),
            TestContext.Current.CancellationToken);

        Assert.IsType<PaymentProviderWriteResult.Saved>(result);
        Assert.False(store.Row.IsEnabled);
        Assert.True(store.Saved);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(("PaymentProvider", PaymentProviderCodes.BankTransfer), (entry.EntityType, entry.EntityId));
        Assert.Equal(AdminAlertTypes.SensitiveSettingsChanged, Assert.Single(alerts.Entries).AlertType);
    }

    [Fact]
    public async Task Asking_for_the_state_it_is_already_in_writes_no_audit_and_no_alert()
    {
        var (handler, _, audit, alerts) = Build(new EffectivePermissions(true, []));

        var result = await handler.HandleAsync(
            new SetPaymentProviderEnabledCommand(PaymentProviderCodes.BankTransfer, true, "AAAAAAAAAAE=", Actor),
            TestContext.Current.CancellationToken);

        Assert.IsType<PaymentProviderWriteResult.Saved>(result);
        Assert.Empty(audit.Entries);
        Assert.Empty(alerts.Entries);
    }

    private static (PaymentProviderSwitchHandler, FakeStore, FakeAudit, FakeAlerts) Build(EffectivePermissions permissions)
    {
        var store = new FakeStore();
        var audit = new FakeAudit();
        var alerts = new FakeAlerts();

        return (
            new PaymentProviderSwitchHandler(store, new FakeRegistry(), audit, alerts, new FakePermissions(permissions), new FixedClock()),
            store,
            audit,
            alerts);
    }

    private sealed class FakeStore : IPaymentProviderAdminStore
    {
        public PaymentProvider Row { get; } = new()
        {
            Code = PaymentProviderCodes.BankTransfer,
            DisplayName = "Bank transfer",
            IsEnabled = true,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 1],
        };

        public bool Saved { get; private set; }

        public Task<IReadOnlyList<PaymentProviderRow>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PaymentProviderRow>>(
                [new PaymentProviderRow(Row.Code, Row.DisplayName, Row.IsEnabled, Row.RowVersion!)]);

        public Task<PaymentProvider?> FindForUpdateAsync(string code, byte[] expectedRowVersion, CancellationToken cancellationToken) =>
            Task.FromResult<PaymentProvider?>(code == Row.Code ? Row : null);

        public Task<bool> SaveAsync(CancellationToken cancellationToken)
        {
            Saved = true;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeRegistry : IPaymentProviderRegistry
    {
        public IReadOnlyList<string> ImplementedCodes => [PaymentProviderCodes.BankTransfer];

        public IPaymentProvider? Find(string? code) => null;
    }

    private sealed class FakeAudit : IAuditLogWriter
    {
        public List<AuditLog> Entries { get; } = [];

        public Task WriteAsync(AuditLog entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public void Enlist(AuditLog entry) => Entries.Add(entry);
    }

    private sealed class FakeAlerts : IAdminAlertWriter
    {
        public List<AdminAlert> Entries { get; } = [];

        public Task RaiseAsync(AdminAlert alert, CancellationToken cancellationToken)
        {
            Entries.Add(alert);
            return Task.CompletedTask;
        }

        public void Enlist(AdminAlert alert) => Entries.Add(alert);
    }

    private sealed class FakePermissions(EffectivePermissions permissions) : IEffectivePermissionsProvider
    {
        public Task<EffectivePermissions> GetEffectivePermissionsAsync(int userId, CancellationToken cancellationToken) =>
            Task.FromResult(permissions);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    }
}
