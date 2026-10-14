using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// The arrangement the provider-settlement suites share: a bank-transfer order placed through the real
/// checkout and then <b>re-labelled</b> as a payment provider's (nothing in this build can place one — the
/// second gateway is seeded disabled and has no code), and the reads that check what a settlement did.
/// <para>
/// (Moved out of <c>OrderSettlementStoreTests</c> unchanged when the robustness suite needed the same
/// arrangement. They are protected members now; nothing else about them differs.)
/// </para>
/// </summary>
public abstract class ProviderOrderTestBase(SqlServerDatabaseFixture sqlServer) : BankTransferTestBase(sqlServer)
{
    protected const string Provider = PaymentProviderCodes.PayPal;
    protected const decimal UsdAmount = 31.25m;
    protected const string Usd = "USD";

    // ---------------------------------------------------------------------------------------------------
    // Arrangement
    // ---------------------------------------------------------------------------------------------------

    /// <summary>An order placed through checkout and re-labelled as the gateway's.</summary>
    protected sealed record ProviderOrder(
        Guid PublicId,
        string OrderNumber,
        int OrderId,
        int PaymentId,
        int VariantId,
        decimal GrandTotal);

    /// <summary>
    /// Places a bank-transfer order over HTTP, optionally runs a lifecycle step on it <i>while it is still a
    /// bank order</i> (expiry and cancellation are provider-pinned), then re-labels the order and its one
    /// payment as the gateway's.
    /// </summary>
    protected async Task<ProviderOrder> PlaceProviderOrderAsync(
        int quantity = 1,
        bool converted = true,
        PaymentStatus? status = null,
        string? failureCode = null,
        Func<Guid, int, Task>? beforeRelabel = null)
    {
        var (order, variantId) = await PlaceOrderAsync(quantity, onHand: 6);
        var orderId = await OrderIdAsync(order.PublicId);

        if (beforeRelabel is not null)
        {
            await beforeRelabel(order.PublicId, orderId);
        }

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var relabelledOrder = await db.Orders
            .Where(candidate => candidate.Id == orderId)
            .ExecuteUpdateAsync(
                candidate => candidate.SetProperty(row => row.PaymentProviderCode, Provider),
                Token);

        // The arrangement, asserted: a no-op update would leave a bank order behind and these tests would
        // be exercising the pin rather than the settlement.
        Assert.Equal(1, relabelledOrder);

        var paymentId = await db.Payments
            .AsNoTracking()
            .Where(candidate => candidate.OrderId == orderId)
            .Select(candidate => candidate.Id)
            .SingleAsync(Token);

        var reference = SalesRowWriter.Unique("PP-ORDER-");

        var relabelledPayment = await db.Payments
            .Where(candidate => candidate.Id == paymentId)
            .ExecuteUpdateAsync(
                candidate => candidate
                    .SetProperty(row => row.ProviderCode, Provider)
                    .SetProperty(row => row.ProviderReference, reference)
                    .SetProperty(row => row.ProviderCurrency, converted ? Usd : null)
                    .SetProperty(row => row.ProviderAmount, converted ? UsdAmount : (decimal?)null)
                    .SetProperty(row => row.ExchangeRate, converted ? 300m : (decimal?)null)
                    .SetProperty(row => row.FxBufferPercent, converted ? 3m : (decimal?)null),
                Token);

        Assert.Equal(1, relabelledPayment);

        if (status is { } wanted)
        {
            await db.Payments
                .Where(candidate => candidate.Id == paymentId)
                .ExecuteUpdateAsync(
                    candidate => candidate
                        .SetProperty(row => row.Status, wanted)
                        .SetProperty(row => row.FailureCode, failureCode)
                        .SetProperty(row => row.FailureMessage, failureCode == null ? null : "The card was declined."),
                    Token);
        }

        // Initiated is a gateway's opening status; the bank's Pending is not. Unless the caller set one (or
        // a lifecycle step already moved the payment), re-open it as the gateway would have.
        else if (beforeRelabel is null)
        {
            await db.Payments
                .Where(candidate => candidate.Id == paymentId)
                .ExecuteUpdateAsync(
                    candidate => candidate.SetProperty(row => row.Status, PaymentStatus.Initiated),
                    Token);
        }

        var grandTotal = await db.Orders
            .AsNoTracking()
            .Where(candidate => candidate.Id == orderId)
            .Select(candidate => candidate.GrandTotal)
            .SingleAsync(Token);

        return new ProviderOrder(order.PublicId, order.OrderNumber, orderId, paymentId, variantId, grandTotal);
    }

    /// <summary>A second payment row on the same order, as a retry or a duplicate capture would be.</summary>
    protected async Task<int> InsertPaymentAsync(int orderId, PaymentStatus status)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var payment = new Payment
        {
            OrderId = orderId,
            ProviderCode = Provider,
            Status = status,
            Amount = await db.Orders
                .AsNoTracking()
                .Where(candidate => candidate.Id == orderId)
                .Select(candidate => candidate.GrandTotal)
                .SingleAsync(Token),
            Currency = SalesCurrency.Lkr,
            ProviderCurrency = Usd,
            ProviderAmount = UsdAmount,
            ExchangeRate = 300m,
            FxBufferPercent = 3m,
            ProviderReference = SalesRowWriter.Unique("PP-ORDER-"),
            CreatedAtUtc = DateTime.UtcNow,
        };

        db.Payments.Add(payment);
        await db.SaveChangesAsync(Token);

        return payment.Id;
    }

    protected Task<PaymentSettlementResult> SettleAsync(
        ProviderOrder arranged,
        int? paymentId = null,
        decimal amount = UsdAmount,
        string currency = Usd,
        string? transactionId = null,
        string? payer = null,
        DateTime? now = null) =>
        InScopeAsync(services => services
            .GetRequiredService<IOrderSettlementStore>()
            .SettleAsync(
                new PaymentSettlement(
                    paymentId ?? arranged.PaymentId,
                    Provider,
                    amount,
                    currency,
                    now ?? DateTime.UtcNow,
                    transactionId ?? SalesRowWriter.Unique("CAP-"),
                    payer),
                Token));

    /// <summary>
    /// Waits until SQL Server reports a request blocked by session <paramref name="blockerSpid"/>, so a test
    /// knows the store has reached the statement the lock is holding up — and fails, with a message, if
    /// that never happens.
    /// </summary>
    protected async Task WaitUntilBlockedByAsync(int blockerSpid)
    {
        await using var monitor = new Microsoft.Data.SqlClient.SqlConnection(SqlServer.ConnectionString);
        await monitor.OpenAsync(Token);

        for (var attempt = 0; attempt < 600; attempt++)
        {
            await using var command = monitor.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @spid;";
            command.Parameters.AddWithValue("@spid", blockerSpid);

            if (Convert.ToInt32(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture) > 0)
            {
                return;
            }

            await Task.Delay(100, Token);
        }

        Assert.Fail("The settlement never reached the payment update that the lock was meant to hold up.");
    }

    protected async Task SetCaptureLockAsync(int orderId, DateTime until)
    {
        using var scope = CreateScope();

        var moved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .Where(order => order.Id == orderId)
            .ExecuteUpdateAsync(
                order => order.SetProperty(row => row.CaptureLockedUntilUtc, until),
                Token);

        Assert.Equal(1, moved);
    }

    protected async Task<List<EmailOutboxMessage>> OutboxAsync(Guid publicId, string templateCode)
    {
        using var scope = CreateScope();
        var id = publicId.ToString("D");

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().EmailOutbox
            .AsNoTracking()
            .Where(message => message.RelatedEntityId == id && message.TemplateCode == templateCode)
            .OrderBy(message => message.Id)
            .ToListAsync(Token);
    }

    protected async Task<List<AdminAlert>> AlertsAsync(Guid publicId)
    {
        using var scope = CreateScope();
        var id = publicId.ToString("D");

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminAlerts
            .AsNoTracking()
            .Where(alert => alert.RelatedEntityId == id)
            .OrderBy(alert => alert.Id)
            .ToListAsync(Token);
    }

    /// <summary>
    /// Removes this test's alerts. The tests that count alerts clear the table themselves first, but a left-over
    /// banner row is a neighbour's fixture, and the sort of thing that makes a full run red when a single
    /// class is green.
    /// </summary>
    protected async Task DeleteAlertsAsync(Guid publicId)
    {
        if (!SqlServer.IsAvailable)
        {
            return;
        }

        using var scope = SqlServer.CreateScope();
        var id = publicId.ToString("D");

        await scope.ServiceProvider.GetRequiredService<AppDbContext>().AdminAlerts
            .Where(alert => alert.RelatedEntityId == id)
            .ExecuteDeleteAsync(CancellationToken.None);
    }

    protected async Task<int> AuditRowCountAsync(Guid publicId)
    {
        using var scope = CreateScope();
        var id = publicId.ToString("D");

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .CountAsync(
                log => log.EntityType == nameof(Order)
                    && log.EntityId == id
                    && (log.Action == AuditActions.Approve || log.Action == AuditActions.StatusChange),
                Token);
    }
}
