using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Places real orders through checkout and then exercises the bank-transfer flow on them (PAY-01, doc 11
/// P7).
/// <para>
/// <b>The orders are placed over HTTP and never inserted by hand.</b> <c>SalesRowWriter</c> exists for
/// testing a constraint, where EF's own validation would get in the way — but every invariant this suite
/// is about depends on the order and its reservation having been written by the real placement path. An
/// order whose <c>Reserved</c> was set by a fixture would let an approval "deduct" stock nobody held, and
/// the test would pass while the thing it is about did not happen.
/// </para>
/// </summary>
public abstract class BankTransferTestBase(SqlServerDatabaseFixture sqlServer) : CheckoutTestBase(sqlServer)
{
    /// <summary>The five <c>Bank.*</c> keys, so a test can restore them in a <c>finally</c>.</summary>
    protected static readonly string[] BankKeys =
    [
        SettingKeys.Bank.BankName,
        SettingKeys.Bank.Branch,
        SettingKeys.Bank.AccountName,
        SettingKeys.Bank.AccountNumber,
        SettingKeys.Bank.Instructions,
    ];

    /// <summary>An actor for a store call, with an id so the approver columns and the audit row have one.</summary>
    protected static AdminActor Staff(int userId = 4242) =>
        new(userId, $"staff-{userId}@example.com", "203.0.113.7", "xunit", null);

    /// <summary>How many orders this test has placed, so each one gets a variant of its own.</summary>
    private int _ordersPlaced;

    /// <summary>
    /// Places one order for <paramref name="quantity"/> of a variant whose stock this test owns, and
    /// returns the order and the variant.
    /// </summary>
    /// <remarks>
    /// <b>Every call takes a <i>different</i> variant, and the first version of this helper did not — which
    /// cost a real failure before a line of product code was wrong.</b> <c>SetStockAsync</c> zeroes
    /// <c>Reserved</c> (its own remarks say so: a round of a concurrency test needs a known starting
    /// point), so a second call on the same variant detaches the <i>first</i> order's reservation. The
    /// first order then looks real but holds nothing, and approving it throws doc 08 §5.5's drift
    /// exception. That is the store behaving exactly as specified against a fixture that lied, and it is
    /// precisely the "a test that takes stock from its neighbours" shape <c>CheckoutTestBase</c> was
    /// rewritten to avoid.
    /// <para>
    /// So the index into <see cref="CheckoutTestBase.CheckoutVariantIdsAsync"/> advances per call, and the
    /// list is taken from the high end of the catalogue where no other suite looks.
    /// </para>
    /// </remarks>
    protected async Task<(OrderConfirmationDto Order, int VariantId)> PlaceOrderAsync(
        int quantity = 1,
        int onHand = 10)
    {
        var variantId = (await CheckoutVariantIdsAsync(++_ordersPlaced))[_ordersPlaced - 1];
        await SetStockAsync(variantId, onHand);

        var (_, _, api) = await NewBagAsync(variantId, quantity);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);
        var (response, order) = await api.PlaceAsync(
            ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session)),
            Token);

        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(order);

        return (order, variantId);
    }

    /// <summary>The order's internal id, which the stores take.</summary>
    protected async Task<int> OrderIdAsync(Guid publicId)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .AsNoTracking()
            .Where(order => order.PublicId == publicId)
            .Select(order => order.Id)
            .SingleAsync(Token);
    }

    /// <summary>Runs <paramref name="act"/> against a fresh application scope on the API host.</summary>
    /// <remarks>
    /// A scope per call, never one shared across a test. These stores clear the change tracker and open
    /// their own transactions; reusing a scope across an approval and the read that checks it would let
    /// the read be answered from the tracker, which is <c>OrderPlacementStore.DetachAll</c>'s own warning.
    /// </remarks>
    protected async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> act)
    {
        ArgumentNullException.ThrowIfNull(act);

        using var scope = CreateApplicationScope();
        return await act(scope.ServiceProvider);
    }

    protected Task<BankTransferDecisionResult> ApproveAsync(int orderId, string? note = null) =>
        InScopeAsync(services => services
            .GetRequiredService<IBankTransferReviewStore>()
            .ApproveAsync(new BankTransferDecision(orderId, Staff(), note, DateTime.UtcNow), Token));

    protected Task<BankTransferDecisionResult> RejectAsync(int orderId, string note = "No credit found.") =>
        InScopeAsync(services => services
            .GetRequiredService<IBankTransferReviewStore>()
            .RejectAsync(new BankTransferDecision(orderId, Staff(), note, DateTime.UtcNow), Token));

    protected Task<BankTransferDecisionResult> AcceptLateAsync(int orderId, string? note = null) =>
        InScopeAsync(services => services
            .GetRequiredService<IBankTransferReviewStore>()
            .AcceptLateAsync(new BankTransferDecision(orderId, Staff(), note, DateTime.UtcNow), Token));

    /// <summary>
    /// Expires one order through the real store, with "now" far enough ahead that its deadline has passed.
    /// </summary>
    /// <remarks>
    /// <b>The clock is moved rather than the deadline</b>, because the deadline is the thing under test:
    /// rewriting <c>PaymentDueAtUtc</c> would mean the expiry guard was checked against a value this test
    /// wrote. 49 hours is one past the 48 checkout gives a bank transfer.
    /// </remarks>
    protected Task<bool> ExpireAsync(int orderId, DateTime? nowUtc = null) =>
        InScopeAsync(services => services
            .GetRequiredService<IOrderExpiryStore>()
            .ExpireAsync(orderId, nowUtc ?? DateTime.UtcNow.AddHours(49), Token));

    /// <summary>The order row with its payments and history, read fresh.</summary>
    protected async Task<Order> ReadOrderAsync(Guid publicId)
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .AsNoTracking()
            .Include(order => order.Payments)
            .Include(order => order.StatusHistory)
            .Include(order => order.Lines)
            .SingleAsync(order => order.PublicId == publicId, Token);
    }

    /// <summary>Sets the five <c>Bank.*</c> settings, so the provider has details to describe.</summary>
    protected async Task SetBankDetailsAsync(
        string? bankName = "Commercial Bank of Ceylon PLC",
        string? branch = "Kollupitiya",
        string? accountName = "Spencer Denim Industries (Pvt) Ltd",
        string? accountNumber = "8001 2345 6789",
        string? instructions = "Cash-machine deposits can take a working day to appear.")
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [SettingKeys.Bank.BankName] = bankName,
            [SettingKeys.Bank.Branch] = branch,
            [SettingKeys.Bank.AccountName] = accountName,
            [SettingKeys.Bank.AccountNumber] = accountNumber,
            [SettingKeys.Bank.Instructions] = instructions,
        };

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var (key, value) in values)
        {
            await db.Settings
                .Where(setting => setting.Key == key)
                .ExecuteUpdateAsync(
                    setting => setting.SetProperty(row => row.Value, value),
                    Token);
        }
    }

    /// <summary>
    /// Puts the five <c>Bank.*</c> settings back to null.
    /// </summary>
    /// <remarks>
    /// <b>Every test that sets them must call this in a <c>finally</c>.</b> The assembly shares one
    /// database and classes run sequentially, so a left-behind bank account number is a neighbour's
    /// fixture — the shape that made four <c>CartStaleItemTests</c> red in a full run and green alone.
    /// Null is right rather than "the seeded value", because the seeder writes null and nothing else ever
    /// sets them.
    /// </remarks>
    protected async Task ClearBankDetailsAsync()
    {
        if (!SqlServer.IsAvailable)
        {
            return;
        }

        using var scope = SqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Settings
            .Where(setting => BankKeys.Contains(setting.Key))
            .ExecuteUpdateAsync(
                setting => setting.SetProperty(row => row.Value, (string?)null),
                CancellationToken.None);
    }

    /// <summary>
    /// The <c>INVOICE-&lt;year&gt;</c> series' next value, or <see langword="null"/> when nothing has ever
    /// allocated from it.
    /// </summary>
    protected async Task<int?> InvoiceSeriesNextValueAsync()
    {
        var key = DocumentNumber.InvoiceSeriesKey(
            SpencerDenim.Domain.Common.ColomboTime.YearOf(DateTimeOffset.UtcNow));

        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().NumberSeries
            .AsNoTracking()
            .Where(series => series.SeriesKey == key)
            .Select(series => (int?)series.NextValue)
            .FirstOrDefaultAsync(Token);
    }
}
