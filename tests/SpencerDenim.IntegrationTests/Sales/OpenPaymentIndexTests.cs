using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// <c>UX_Payments_OneOpen</c> — the filtered unique index over <c>Status IN (0,1)</c> — against the
/// resubmission path doc 08 §3.5 N7 defines (PAY-01).
/// <para>
/// <b>#103's review flagged §3.5's claim that this index "can never block capture recovery or a customer
/// retry" as not quite true, and asked whoever wrote step 3 to settle it.</b> This file is the bank-transfer
/// half of that answer; the PayPal half cannot be tested here because no code captures anything, and it is
/// written up in <c>PaymentConfiguration</c>'s remarks for W4-01 instead.
/// </para>
/// <para>
/// <b>What the index does to the bank flow.</b> A rejection moves the row to <c>Failed (4)</c>, which is
/// outside the filter, so a resubmission inserting a new <c>Pending (1)</c> row never collides with it —
/// the claim holds for the sequential case. What the claim does not say is that the index constrains two
/// resubmissions <i>at once</i>: both find no open row and both insert, and the second is refused. That is
/// the index enforcing "at most one open attempt per order", which is correct — but it reaches the
/// application as error 2627, and the only acceptable answer to a customer who tapped Upload twice is the
/// winner's row rather than a 500.
/// </para>
/// </summary>
public sealed class OpenPaymentIndexTests(SqlServerDatabaseFixture sqlServer) : BankTransferTestBase(sqlServer)
{
    [Fact]
    public async Task A_resubmission_after_a_rejection_opens_a_second_pending_row()
    {
        var (order, _) = await PlaceOrderAsync();
        var orderId = await OrderIdAsync(order.PublicId);

        Assert.IsType<BankTransferDecisionResult.Applied>(await RejectAsync(orderId));

        var reopened = await OpenPaymentAsync(order.PublicId);

        var row = await ReadOrderAsync(order.PublicId);

        // Doc 08 §3.5 N7: the rejected row is closed and a *new* Pending row is created. Two rows, one of
        // them open — which is only possible because Failed (4) is outside the index's filter.
        Assert.Equal(2, row.Payments.Count);
        Assert.Single(row.Payments, payment => payment.Status == PaymentStatus.Failed);

        var open = Assert.Single(row.Payments, payment => payment.Status == PaymentStatus.Pending);
        Assert.Equal(reopened, open.Id);
    }

    [Fact]
    public async Task Asking_twice_in_a_row_returns_the_same_open_row()
    {
        var (order, _) = await PlaceOrderAsync();

        // Placement already left one open row, so this must find it rather than create a second — which is
        // the path a customer takes when they add a reference and then upload a slip.
        var first = await OpenPaymentAsync(order.PublicId);
        var second = await OpenPaymentAsync(order.PublicId);

        Assert.Equal(first, second);
        Assert.Single((await ReadOrderAsync(order.PublicId)).Payments);
    }

    /// <summary>
    /// <b>The case the index really does constrain, and the one worth having a test for.</b>
    /// </summary>
    /// <remarks>
    /// Two resubmissions at once after a rejection: both read no open row, both insert, and
    /// <c>UX_Payments_OneOpen</c> refuses one of them with error 2627. The expected outcome is <i>not</i> an
    /// error — it is that both callers end up holding the same row, because the loser catches the violation
    /// and re-reads. That is the same "ask the index one more question before answering" shape
    /// <c>PlaceOrderCommandHandler.RefuseUnlessAlreadyPlacedAsync</c> uses on
    /// <c>UX_Orders_CheckoutIdempotencyKey</c>, applied to a different index.
    /// <para>
    /// Rounds, because a race proved once is a race that happened once. Four contenders rather than two, so
    /// the loser's retry is itself contended.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Two_resubmissions_at_once_open_exactly_one_row_and_neither_caller_fails()
    {
        const int Rounds = 8;
        const int Contenders = 4;

        for (var round = 1; round <= Rounds; round++)
        {
            var (order, _) = await PlaceOrderAsync();
            var orderId = await OrderIdAsync(order.PublicId);

            Assert.IsType<BankTransferDecisionResult.Applied>(await RejectAsync(orderId));

            var opened = await Task.WhenAll(
                Enumerable.Range(0, Contenders)
                    .Select(_ => Task.Run(() => OpenPaymentAsync(order.PublicId), Token)));

            // Every caller got a row, and it is the same row. A 500 to the customer who tapped Upload
            // twice would be the failure this guards.
            Assert.All(opened, id => Assert.NotNull(id));
            Assert.Single(opened.Distinct());

            var row = await ReadOrderAsync(order.PublicId);

            Assert.Single(row.Payments, payment => payment.Status == PaymentStatus.Pending);
            Assert.Equal(
                2,
                row.Payments.Count);

            // And the one open row carries the order's own total, read from the order rather than from any
            // caller (G-10).
            var open = Assert.Single(row.Payments, payment => payment.Status == PaymentStatus.Pending);
            Assert.Equal(row.GrandTotal, open.Amount);
            Assert.False(open.IsOrderSettlement);
        }
    }

    /// <summary>
    /// Issue #163: with <c>READ_COMMITTED_SNAPSHOT ON</c>, opening a payment still waits for an expiry or a
    /// cancellation that has the order row and has not committed, and then answers "not payable".
    /// </summary>
    /// <remarks>
    /// <b>What the option would otherwise change.</b> <c>OpenPaymentAsync</c> decides from a read of the
    /// order's status and then inserts a <c>Pending</c> payment. With locking <c>READ COMMITTED</c> that read
    /// waited for any transaction holding the order row; with the option on, a plain read takes the last
    /// committed version and does not wait, so it would see <c>PendingPayment</c> while an expiry was moving
    /// the order to Expired and open a payment on an order that was closing. The read carries
    /// <c>WITH (READCOMMITTEDLOCK)</c> to keep the old behaviour. Measured without the hint: the call still
    /// takes the full two seconds — the insert's foreign-key check on the order row waits — but it then
    /// returns a new payment id on the expired order, and this test fails on that.
    /// <para>
    /// It does not close the race that existed before: an expiry that <i>starts</i> after this read and
    /// commits before the insert still leaves a stray open row. That needs the order row claimed in the
    /// same transaction as the insert, and is outside #163.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Opening_a_payment_waits_for_an_uncommitted_expiry_and_then_refuses()
    {
        var (order, _) = await PlaceOrderAsync();
        var orderId = await OrderIdAsync(order.PublicId);

        // A rejection closes the placement's open row, so the open below would have to insert one.
        Assert.IsType<BankTransferDecisionResult.Applied>(await RejectAsync(orderId));

        await using var expiry = new SqlConnection(SqlServer.ConnectionString);
        await expiry.OpenAsync(Token);
        await using var transaction = (SqlTransaction)await expiry.BeginTransactionAsync(Token);
        await using (var expire = new SqlCommand(
            "UPDATE sales.Orders SET Status = @expired WHERE Id = @id AND Status = @pending", expiry, transaction))
        {
            expire.Parameters.AddWithValue("@expired", (byte)OrderStatus.Expired);
            expire.Parameters.AddWithValue("@pending", (byte)OrderStatus.PendingPayment);
            expire.Parameters.AddWithValue("@id", orderId);
            Assert.Equal(1, await expire.ExecuteNonQueryAsync(Token));
        }

        var open = OpenPaymentAsync(order.PublicId);
        var answeredWhileTheExpiryWasOpen = await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(2), Token)) == open;

        await transaction.CommitAsync(Token);
        var opened = await open;

        Assert.False(
            answeredWhileTheExpiryWasOpen,
            "OpenPaymentAsync read the order's status without waiting for a transaction that had the order row (#163).");
        Assert.Null(opened);
        Assert.DoesNotContain(
            (await ReadOrderAsync(order.PublicId)).Payments,
            payment => payment.Status == PaymentStatus.Pending);
    }

    /// <summary>
    /// The index itself, asserted from the catalogue — because every conclusion in this file depends on
    /// which statuses its filter covers.
    /// </summary>
    /// <remarks>
    /// <c>SalesSchemaTests</c> already compares all 31 of doc 08 §3.5's indexes by shape. This one is
    /// repeated here because the <i>reasoning</i> above is only valid for the filter <c>(0,1)</c>: widen it
    /// to include <c>Capturing (2)</c> and the resubmission tests would still pass while capture recovery
    /// became unrecoverable, which is exactly the trade doc 08 N1 made deliberately.
    /// </remarks>
    [Fact]
    public async Task The_index_covers_initiated_and_pending_and_not_capturing()
    {
        SqlServer.SkipIfUnavailable();

        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var filter = await dbContext.Database
            .SqlQuery<string>(
                $"""
                SELECT i.filter_definition AS Value
                  FROM sys.indexes AS i
                 WHERE i.object_id = OBJECT_ID('sales.Payments')
                   AND i.name = 'UX_Payments_OneOpen'
                """)
            .SingleAsync(Token);

        Assert.Equal("([Status] IN ((0), (1)))", filter);

        var isUnique = await dbContext.Database
            .SqlQuery<bool>(
                $"""
                SELECT i.is_unique AS Value
                  FROM sys.indexes AS i
                 WHERE i.object_id = OBJECT_ID('sales.Payments')
                   AND i.name = 'UX_Payments_OneOpen'
                """)
            .SingleAsync(Token);

        Assert.True(isUnique);
    }

    private Task<int?> OpenPaymentAsync(Guid orderPublicId) =>
        InScopeAsync(services => services
            .GetRequiredService<IBankTransferProofStore>()
            .OpenPaymentAsync(orderPublicId, DateTime.UtcNow, Token));
}
