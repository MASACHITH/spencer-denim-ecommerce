using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Checkout;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// The order page tells the payment provider <b>which payment attempt</b> it is describing, so a provider
/// that redirects can find the hosted page for the attempt that already exists (PAY-03,
/// <see cref="PaymentInstructionsRequest.PaymentId"/>) — and what it answers reaches the page's payload,
/// including the redirect form.
/// <para>
/// <b>The real reader, the real attempt reader and the real database, with a spy in the one seat that is
/// not built yet.</b> There is no second provider to register, so the handler is assembled by hand around a
/// provider that records what it was asked and answers what a test tells it to.
/// </para>
/// </summary>
public sealed class OrderPagePaymentAttemptTests(SqlServerDatabaseFixture sqlServer) : BankTransferTestBase(sqlServer)
{
    private static readonly PaymentRedirectForm Form = PaymentRedirectForm.Post(
        "https://pay.example.test/checkout",
        [
            new PaymentRedirectFormField("order_id", "SD-2026-000123-1"),
            new PaymentRedirectFormField("hash", "ABC123"),
        ]);

    [Fact]
    public async Task The_provider_is_told_the_payment_and_its_reference_and_its_answer_reaches_the_page()
    {
        var (order, _) = await PlaceOrderAsync();
        var paymentId = (await ReadOrderAsync(order.PublicId)).Payments.Single().Id;
        await SetReferenceAsync(paymentId, "REF-8841");

        var spy = new SpyProvider(RedirectInstructions(order.GrandTotal));

        var dto = await HandleAsync(order.PublicId, spy);

        // What the provider was asked: the live attempt's id and the provider's own reference for it.
        var request = Assert.Single(spy.Requests);
        Assert.Equal(paymentId, request.PaymentId);
        Assert.Equal("REF-8841", request.ProviderReference);
        Assert.Equal(order.OrderNumber, request.OrderNumber);
        Assert.Equal(order.GrandTotal, request.AmountLkr);

        // What it answered: the redirect form arrives in the page's payload, field for field and in order.
        var payment = dto!.Payment!;
        Assert.Null(payment.RedirectUrl);
        Assert.False(payment.AcceptsProof);

        var form = payment.RedirectForm!;
        Assert.Equal("POST", form.Method);
        Assert.Equal("https://pay.example.test/checkout", form.Action);
        Assert.Equal(["order_id", "hash"], form.Fields.Select(field => field.Name).ToArray());
        Assert.Equal(["SD-2026-000123-1", "ABC123"], form.Fields.Select(field => field.Value).ToArray());
    }

    [Fact]
    public async Task An_order_with_no_live_attempt_gives_the_provider_no_payment()
    {
        var (order, _) = await PlaceOrderAsync();

        // A rejected transfer closes its row (Pending to Failed); the order is still unpaid and the
        // customer may transfer again, but there is no attempt in flight to name.
        Assert.IsType<BankTransferDecisionResult.Applied>(
            await RejectAsync(await OrderIdAsync(order.PublicId), "No credit found."));

        var spy = new SpyProvider(instructions: null);

        var dto = await HandleAsync(order.PublicId, spy);

        var request = Assert.Single(spy.Requests);
        Assert.Null(request.PaymentId);
        Assert.Null(request.ProviderReference);
        Assert.Null(dto!.Payment);
    }

    [Fact]
    public async Task A_paid_order_asks_the_provider_nothing()
    {
        var (order, _) = await PlaceOrderAsync();
        Assert.IsType<BankTransferDecisionResult.Applied>(await ApproveAsync(await OrderIdAsync(order.PublicId)));

        var spy = new SpyProvider(instructions: null);

        var dto = await HandleAsync(order.PublicId, spy);

        // The cost of the page is unchanged for every order that is not waiting for money.
        Assert.Empty(spy.Requests);
        Assert.Null(dto!.Payment);
    }

    /// <summary>
    /// The newest attempt wins, and a capture in flight counts as one the customer is waiting on.
    /// </summary>
    [Fact]
    public async Task The_newest_attempt_in_flight_is_the_one_named()
    {
        var (order, _) = await PlaceOrderAsync();
        var orderId = await OrderIdAsync(order.PublicId);
        var first = (await ReadOrderAsync(order.PublicId)).Payments.Single().Id;

        // The first attempt is mid-capture, which is outside UX_Payments_OneOpen on purpose (doc 08 §3.5),
        // so a newer attempt can be opened beside it.
        await SetStatusAsync(first, PaymentStatus.Capturing);
        var second = await InsertPaymentAsync(orderId, PaymentStatus.Initiated, "REF-SECOND");

        var attempt = await FindAsync(order.PublicId);

        Assert.Equal(second, attempt!.PaymentId);
        Assert.Equal(PaymentStatus.Initiated, attempt.Status);
        Assert.Equal("REF-SECOND", attempt.ProviderReference);

        // Close the newer one and the capture in flight is what is left.
        await SetStatusAsync(second, PaymentStatus.Cancelled);

        var remaining = await FindAsync(order.PublicId);

        Assert.Equal(first, remaining!.PaymentId);
        Assert.Equal(PaymentStatus.Capturing, remaining.Status);
    }

    [Theory]
    [InlineData(PaymentStatus.Initiated, true)]
    [InlineData(PaymentStatus.Pending, true)]
    [InlineData(PaymentStatus.Capturing, true)]
    [InlineData(PaymentStatus.Succeeded, false)]
    [InlineData(PaymentStatus.Failed, false)]
    [InlineData(PaymentStatus.Cancelled, false)]
    [InlineData(PaymentStatus.Expired, false)]
    [InlineData(PaymentStatus.CapturedRequiresRefund, false)]
    [InlineData(PaymentStatus.PartiallyRefunded, false)]
    [InlineData(PaymentStatus.Refunded, false)]
    public async Task Only_an_attempt_that_has_not_reached_an_outcome_is_in_flight(PaymentStatus status, bool inFlight)
    {
        var (order, _) = await PlaceOrderAsync();
        var paymentId = (await ReadOrderAsync(order.PublicId)).Payments.Single().Id;
        await SetStatusAsync(paymentId, status);

        var attempt = await FindAsync(order.PublicId);

        Assert.Equal(inFlight, attempt is not null);
    }

    [Fact]
    public async Task An_order_that_does_not_exist_has_no_attempt()
    {
        Assert.Null(await FindAsync(Guid.NewGuid()));
    }

    /// <summary>
    /// The new contract fields are optional on the wire: a bank-transfer order's page carries no redirect, and
    /// the JSON says so in a way the storefront's optional <c>redirectForm</c> reads as "absent".
    /// </summary>
    [Fact]
    public async Task A_bank_transfer_orders_page_carries_no_redirect_form()
    {
        await SetBankDetailsAsync();

        try
        {
            var (order, _) = await PlaceOrderAsync();
            var api = NewCheckoutVisitor();

            var json = await api.Http.GetStringAsync(
                new Uri($"/api/orders/{order.PublicId:D}", UriKind.Relative),
                Token);

            using var document = JsonDocument.Parse(json);
            var payment = document.RootElement.GetProperty("payment");

            // The bank fields are unchanged...
            Assert.Equal("BankTransfer", payment.GetProperty("providerCode").GetString());
            Assert.True(payment.GetProperty("acceptsProof").GetBoolean());
            Assert.NotEqual(0, payment.GetProperty("fields").GetArrayLength());

            // ...and there is no form: absent or null, never an empty one.
            Assert.True(
                !payment.TryGetProperty("redirectForm", out var form) || form.ValueKind == JsonValueKind.Null,
                "A bank transfer must not carry a redirect form.");
        }
        finally
        {
            await ClearBankDetailsAsync();
        }
    }

    // ---------------------------------------------------------------------------------------------------

    private static PaymentInstructions RedirectInstructions(decimal amount) =>
        new(
            PaymentProviderCodes.BankTransfer,
            null,
            amount,
            DateTime.UtcNow.AddHours(1),
            [],
            null,
            RedirectUrl: null,
            AcceptsProof: false,
            RedirectForm: Form);

    private Task<SpencerDenim.Application.Sales.Checkout.Contracts.OrderConfirmationDto?> HandleAsync(
        Guid publicId,
        IPaymentProvider provider) =>
        InScopeAsync(async services =>
        {
            var handler = new GetOrderQueryHandler(
                services.GetRequiredService<ICheckoutReader>(),
                services.GetRequiredService<IOrderPaymentProofReader>(),
                new SingleProviderRegistry(provider),
                services.GetRequiredService<IPaymentAttemptReader>());

            return await handler.HandleAsync(new GetOrderQuery(publicId), Token);
        });

    private Task<PaymentAttempt?> FindAsync(Guid publicId) =>
        InScopeAsync(services => services.GetRequiredService<IPaymentAttemptReader>().FindInFlightAsync(publicId, Token));

    private async Task SetStatusAsync(int paymentId, PaymentStatus status)
    {
        using var scope = CreateScope();

        var moved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Payments
            .Where(payment => payment.Id == paymentId)
            .ExecuteUpdateAsync(payment => payment.SetProperty(row => row.Status, status), Token);

        // The arrangement, asserted: a no-op update would let a test pass against the state it started in.
        Assert.Equal(1, moved);
    }

    private async Task SetReferenceAsync(int paymentId, string reference)
    {
        using var scope = CreateScope();

        var moved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Payments
            .Where(payment => payment.Id == paymentId)
            .ExecuteUpdateAsync(payment => payment.SetProperty(row => row.ProviderReference, reference), Token);

        Assert.Equal(1, moved);
    }

    private async Task<int> InsertPaymentAsync(int orderId, PaymentStatus status, string reference)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var payment = new Payment
        {
            OrderId = orderId,
            ProviderCode = PaymentProviderCodes.BankTransfer,
            Status = status,
            Amount = await db.Orders
                .AsNoTracking()
                .Where(candidate => candidate.Id == orderId)
                .Select(candidate => candidate.GrandTotal)
                .SingleAsync(Token),
            Currency = SalesCurrency.Lkr,
            ProviderReference = reference,
            CreatedAtUtc = DateTime.UtcNow,
        };

        db.Payments.Add(payment);
        await db.SaveChangesAsync(Token);

        return payment.Id;
    }

    private sealed class SingleProviderRegistry(IPaymentProvider provider) : IPaymentProviderRegistry
    {
        public IReadOnlyList<string> ImplementedCodes => [provider.Code];

        public IPaymentProvider? Find(string? code) =>
            string.Equals(code, provider.Code, StringComparison.OrdinalIgnoreCase) ? provider : null;
    }

    private sealed class SpyProvider(PaymentInstructions? instructions) : IPaymentProvider
    {
        public List<PaymentInstructionsRequest> Requests { get; } = [];

        public string Code => PaymentProviderCodes.BankTransfer;

        public ValueTask<PaymentInitiation> InitiateAsync(
            PaymentInitiationRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentInstructions?> DescribeAsync(
            PaymentInstructionsRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(instructions);
        }
    }
}
