using SpencerDenim.Application.Sales.Checkout;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Domain.Sales;
using SpencerDenim.UnitTests.Support;

namespace SpencerDenim.UnitTests.Application.Sales;

/// <summary>
/// The order page's read: what it asks of a provider, and when it must not (doc 11 P8 step 6, P8b step 4).
/// <para>
/// An order that carries a <see cref="OrderConfirmationDto.PaymentNotice"/> has had money arrive that could
/// not settle it. Asking a provider "how does this order get paid?" would invite a second payment next to the
/// one already received - a Pay now button, a bank account - so the handler must not ask, and the order goes
/// back exactly as the reader produced it. An order with no notice is unchanged: the provider is asked.
/// </para>
/// </summary>
public sealed class GetOrderQueryHandlerTests
{
    private static readonly Guid PublicId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static OrderConfirmationDto Order(string status = "PendingPayment", string? notice = null) =>
        new(
            "SD-2026-000777",
            PublicId,
            status,
            status,
            "buyer@example.com",
            "Buyer",
            null,
            null,
            [],
            new OrderDeliveryDto("STANDARD", "Standard", null, null, null, null, null, null, "Colombo", null, null, 1, 2),
            1000m,
            1000m,
            0m,
            0m,
            0m,
            0m,
            1000m,
            "LKR",
            PaymentProviderCodes.BankTransfer,
            "Bank transfer",
            null,
            new DateTime(2026, 10, 5, 4, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 7, 4, 30, 0, DateTimeKind.Utc),
            PaymentNotice: notice);

    private static PaymentInstructions Instructions() =>
        new(
            PaymentProviderCodes.BankTransfer,
            "SD-2026-000777",
            1000m,
            new DateTime(2026, 10, 7, 4, 30, 0, DateTimeKind.Utc),
            [],
            null,
            null,
            AcceptsProof: true);

    [Fact]
    public async Task An_order_with_a_payment_notice_asks_no_provider_and_no_attempt_and_no_proof()
    {
        var provider = new StubPaymentProvider(instructions: Instructions());
        var attempts = new CountingAttempts();
        var proofs = new CountingProofs();
        var handler = new GetOrderQueryHandler(
            new OneOrderReader(Order(notice: OrderPaymentNotices.Received)),
            proofs,
            new StubPaymentProviderRegistry(provider),
            attempts);

        var page = await handler.HandleAsync(new GetOrderQuery(PublicId), CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal(OrderPaymentNotices.Received, page.PaymentNotice);
        Assert.Equal("PendingPayment", page.Status);

        // Nothing that could take, or ask for, another payment.
        Assert.Null(page.Payment);
        Assert.Null(page.Proof);
        Assert.Empty(provider.DescribeRequests);
        Assert.Equal(0, attempts.Reads);
        Assert.Equal(0, proofs.Reads);
    }

    [Fact]
    public async Task An_order_with_no_notice_is_described_by_its_provider_as_before()
    {
        var provider = new StubPaymentProvider(instructions: Instructions());
        var attempts = new CountingAttempts();
        var proofs = new CountingProofs();
        var handler = new GetOrderQueryHandler(
            new OneOrderReader(Order()),
            proofs,
            new StubPaymentProviderRegistry(provider),
            attempts);

        var page = await handler.HandleAsync(new GetOrderQuery(PublicId), CancellationToken.None);

        Assert.NotNull(page);
        Assert.Null(page.PaymentNotice);
        Assert.NotNull(page.Payment);
        Assert.Equal("SD-2026-000777", page.Payment.Reference);
        Assert.Single(provider.DescribeRequests);
        Assert.Equal(1, attempts.Reads);
        Assert.Equal(1, proofs.Reads);
    }

    [Theory]
    [InlineData("Expired")]
    [InlineData("Cancelled")]
    [InlineData("Paid")]
    public async Task A_closed_or_paid_order_keeps_its_notice_and_still_asks_nobody(string status)
    {
        var provider = new StubPaymentProvider(instructions: Instructions());
        var handler = new GetOrderQueryHandler(
            new OneOrderReader(Order(status, OrderPaymentNotices.Received)),
            new CountingProofs(),
            new StubPaymentProviderRegistry(provider),
            new CountingAttempts());

        var page = await handler.HandleAsync(new GetOrderQuery(PublicId), CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal(status, page.Status);
        Assert.Equal(OrderPaymentNotices.Received, page.PaymentNotice);
        Assert.Null(page.Payment);
        Assert.Empty(provider.DescribeRequests);
    }

    [Fact]
    public void The_notice_code_is_the_one_word_the_storefront_switches_on()
    {
        Assert.Equal("received", OrderPaymentNotices.Received);
    }

    /// <summary>Only <see cref="FindOrderAsync"/> is real; anything else the handler might call fails loudly.</summary>
    private sealed class OneOrderReader(OrderConfirmationDto order) : ICheckoutReader
    {
        public Task<OrderConfirmationDto?> FindOrderAsync(Guid publicId, CancellationToken cancellationToken) =>
            Task.FromResult<OrderConfirmationDto?>(publicId == order.PublicId ? order : null);

        public Task<IReadOnlyList<CheckoutDistrict>> ActiveDistrictsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CheckoutDistrict?> FindActiveDistrictAsync(int districtId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CheckoutDeliveryMethod>> DeliveryMethodsForZoneAsync(
            DeliveryZone zone,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CheckoutDeliveryMethod?> FindEnabledDeliveryMethodAsync(
            int deliveryMethodId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CheckoutZoneRate?> FindZoneRateAsync(
            int deliveryMethodId,
            DeliveryZone zone,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CheckoutPickupLocation>> ActivePickupLocationsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CheckoutPickupLocation?> FindActivePickupLocationAsync(
            int pickupLocationId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CheckoutPaymentMethod>> EnabledPaymentMethodsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CheckoutCountry>> CountriesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CheckoutCountry?> FindCountryAsync(string countryCode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CheckoutDeliveryMethod?> InternationalMethodAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CheckoutCartLine>> ReadCartAsync(int cartId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<int, int>> AvailableStockAsync(
            IReadOnlyCollection<int> variantIds,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OrderConfirmationDto?> FindOrderByCheckoutKeyAsync(
            Guid checkoutIdempotencyKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PromoCodeTerms?> FindPromoCodeAsync(
            string normalisedCode,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CountingAttempts : IPaymentAttemptReader
    {
        public int Reads { get; private set; }

        public Task<PaymentAttempt?> FindInFlightAsync(Guid orderPublicId, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<PaymentAttempt?>(null);
        }
    }

    private sealed class CountingProofs : IOrderPaymentProofReader
    {
        public int Reads { get; private set; }

        public Task<OrderPaymentProof?> FindAsync(Guid orderPublicId, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<OrderPaymentProof?>(null);
        }
    }
}
