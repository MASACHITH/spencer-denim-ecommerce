using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Cart;
using SpencerDenim.Application.Sales.Cart.Abstractions;
using SpencerDenim.Application.Sales.Cart.Contracts;
using SpencerDenim.Application.Sales.Checkout;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Domain.Sales;
using SpencerDenim.UnitTests.Support;

namespace SpencerDenim.UnitTests.Application.Sales;

/// <summary>
/// The race the handler's own pre-check cannot close, pinned down deterministically.
///
/// <para>
/// <b>Why a unit test for a concurrency defect.</b> The real shape is two submissions of one checkout key
/// arriving together: the winner's transaction reserves the stock, writes the order and <i>deletes the
/// bag</i>, and the loser passed the replay pre-check a few milliseconds earlier. The loser then reads a
/// bag with nothing in it and — before this test existed — answered <c>Blocked([EmptyBag])</c>, a 409
/// reading "Your bag is empty" to a shopper whose order had in fact been placed. Over HTTP that window is
/// a few milliseconds wide and shows up in roughly a quarter of rounds at 24 contenders and almost never
/// at four, which is exactly the test that passes on broken code
/// (<c>CheckoutConcurrencyTests</c> raises the contention for the end-to-end proof).
/// </para>
/// <para>
/// Here the window is <b>made</b> rather than waited for: <see cref="RacingCheckoutReader"/> answers the
/// pre-check with "no such order" and every later lookup with the winner's order, which is precisely the
/// state of the database when the winner commits inside the handler's own round trips. Every refusal the
/// handler can reach after that commit must therefore answer with the placed order, and the assertions
/// below are one per refusal path.
/// </para>
/// </summary>
public sealed class PlaceOrderCommandHandlerTests
{
    private static readonly Guid CheckoutKey = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid CartToken = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

    private const int DistrictId = 1;
    private const int DeliveryMethodId = 2;
    private const int VariantId = 470;

    /// <summary>
    /// <b>The CI failure, in one assertion.</b> The winner emptied the bag between the pre-check and the
    /// repricing read, so the loser's bag is empty — and the honest answer to "did my order go through"
    /// is the order, not "your bag is empty".
    /// </summary>
    [Fact]
    public async Task A_bag_emptied_by_the_winner_replays_the_order_rather_than_reporting_an_empty_bag()
    {
        var reader = new RacingCheckoutReader { Lines = [] };

        var result = await HandleAsync(reader);

        Assert.Equal(PlaceOrderStatus.AlreadyPlaced, result.Status);
        Assert.Equal(RacingCheckoutReader.WinningOrderNumber, result.Confirmation!.OrderNumber);
        Assert.True(result.Confirmation.IsDuplicateSubmission);
        Assert.Empty(result.Blockers);
    }

    /// <summary>
    /// The same hole on the G-08 path: the winner's reservation made the loser's line unbuyable. "Adjust
    /// your bag" is wrong advice for a bag whose order exists.
    /// </summary>
    [Fact]
    public async Task A_line_made_unpurchasable_by_the_winner_replays_the_order()
    {
        var reader = new RacingCheckoutReader { Lines = [Line(CartLineStatus.OutOfStock)] };

        var result = await HandleAsync(reader);

        Assert.Equal(PlaceOrderStatus.AlreadyPlaced, result.Status);
        Assert.Equal(RacingCheckoutReader.WinningOrderNumber, result.Confirmation!.OrderNumber);
        Assert.Empty(result.Blockers);
    }

    /// <summary>
    /// And the one the brief did not name: the loser got as far as the reserving UPDATE, which matched no
    /// row because the winner had taken the stock. <c>OrderPlacementStore</c> reserves <b>before</b> it
    /// inserts, so this refusal is reached without the idempotency index ever being consulted — a bag
    /// holding exactly the available stock reaches it rather than the duplicate-key path.
    /// </summary>
    [Fact]
    public async Task A_reservation_the_winner_took_first_replays_the_order()
    {
        var reader = new RacingCheckoutReader { Lines = [Line(CartLineStatus.Buyable)] };
        var placements = new StubPlacementStore(
            OrderPlacementResult.OutOfStock(new Dictionary<int, int> { [VariantId] = 0 }));

        var result = await HandleAsync(reader, placements);

        Assert.Equal(PlaceOrderStatus.AlreadyPlaced, result.Status);
        Assert.Equal(RacingCheckoutReader.WinningOrderNumber, result.Confirmation!.OrderNumber);
        Assert.Empty(result.ShortLines);
    }

    /// <summary>
    /// The counterpart, and the assertion that stops the fix from swallowing real refusals: with no order
    /// behind the key, an empty bag is still an empty bag.
    /// </summary>
    [Fact]
    public async Task An_empty_bag_with_no_order_behind_the_key_is_still_refused()
    {
        var reader = new RacingCheckoutReader { Lines = [], WinnerExists = false };

        var result = await HandleAsync(reader);

        Assert.Equal(PlaceOrderStatus.Blocked, result.Status);
        Assert.Equal([CheckoutBlocker.EmptyBag], result.Blockers);
    }

    /// <summary>And a real stock refusal still names the short lines.</summary>
    [Fact]
    public async Task A_real_stock_refusal_with_no_order_behind_the_key_still_names_the_short_lines()
    {
        var reader = new RacingCheckoutReader { Lines = [Line(CartLineStatus.Buyable)], WinnerExists = false };
        var placements = new StubPlacementStore(
            OrderPlacementResult.OutOfStock(new Dictionary<int, int> { [VariantId] = 0 }));

        var result = await HandleAsync(reader, placements);

        Assert.Equal(PlaceOrderStatus.OutOfStock, result.Status);
        Assert.Equal(0, result.ShortLines[VariantId]);
    }

    /// <summary>
    /// The sequential replay the pre-check already caught, so the fix is proved to be an addition rather
    /// than a replacement.
    /// </summary>
    [Fact]
    public async Task A_key_that_already_placed_an_order_is_answered_before_anything_else_is_read()
    {
        var reader = new RacingCheckoutReader { Lines = [], WinnerFoundOnFirstLook = true };

        var result = await HandleAsync(reader);

        Assert.Equal(PlaceOrderStatus.AlreadyPlaced, result.Status);

        // Nothing past step 1 ran: the pre-check is still the cheap path, and this is what says so.
        Assert.Equal(0, reader.CartReads);
    }

    private static Task<PlaceOrderResult> HandleAsync(
        RacingCheckoutReader reader,
        IOrderPlacementStore? placements = null)
    {
        var handler = new PlaceOrderCommandHandler(
            new StubCartStore(),
            reader,
            new ThrowingNumberSeriesAllocator(),
            placements ?? new StubPlacementStore(OrderPlacementResult.DuplicateSubmission),
            new FixedClock(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero)),

            // PAY-03: the handler takes a registry, not a bank-transfer setting. These race tests never
            // reach the provider — they all refuse before the initiation call — but the registry has to be
            // real enough to resolve the provider code the command carries.
            new StubPaymentProviderRegistry());

        return handler.HandleAsync(
            new PlaceOrderCommand(
                CartLookup.ForGuest(CartToken),
                CheckoutKey,
                "Nimali",
                "Perera",
                "nimali@example.com",
                "0771234567",
                DeliveryMethodId,
                DistrictId,
                null,
                new CheckoutAddressInput(
                    "Nimali Perera",
                    "0771234567",
                    "14 Galle Road",
                    null,
                    "Kollupitiya",
                    "00300",
                    null),
                PaymentProviderCodes.BankTransfer,

                // One buyable unit at Rs. 1,000 plus a Rs. 350 fee, so G-10 agrees when a line is priced.
                1350m,
                null,
                null),
            TestContext.Current.CancellationToken);
    }

    private static CheckoutCartLine Line(CartLineStatus status) =>
        new(
            VariantId,
            1,
            1,
            1,
            "Slim Fit Jean",
            "SDJ-001",
            "slim-fit-jean",
            "SDJ-001-BLU-32",
            "Indigo",
            "32",
            null,
            null,
            null,
            1,
            status == CartLineStatus.Buyable ? 5 : 0,
            1000m,
            1000m,
            1000m,
            status);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>
    /// A reader whose database changes under the handler exactly once: the first lookup of the checkout
    /// key finds nothing and every lookup after it finds the winner's order.
    /// </summary>
    private sealed class RacingCheckoutReader : ICheckoutReader
    {
        public const string WinningOrderNumber = "CW-2026-000123";

        private int _keyLookups;

        /// <summary>What the repricing read answers — empty for the bag the winner deleted.</summary>
        public IReadOnlyList<CheckoutCartLine> Lines { get; init; } = [];

        /// <summary>False to model "nobody placed this order", so a refusal stays a refusal.</summary>
        public bool WinnerExists { get; init; } = true;

        /// <summary>True to model the plain sequential resubmit, which the pre-check already answers.</summary>
        public bool WinnerFoundOnFirstLook { get; init; }

        /// <summary>How many times the bag was repriced, so a short-circuit can be asserted.</summary>
        public int CartReads { get; private set; }

        public Task<OrderConfirmationDto?> FindOrderByCheckoutKeyAsync(
            Guid checkoutIdempotencyKey,
            CancellationToken cancellationToken)
        {
            Assert.Equal(CheckoutKey, checkoutIdempotencyKey);

            var lookup = ++_keyLookups;

            if (!WinnerExists)
            {
                return Task.FromResult<OrderConfirmationDto?>(null);
            }

            // The commit lands between lookup 1 and lookup 2 — which is the race, expressed as a schedule.
            return Task.FromResult<OrderConfirmationDto?>(
                WinnerFoundOnFirstLook || lookup > 1 ? Winner() : null);
        }

        public Task<IReadOnlyList<CheckoutCartLine>> ReadCartAsync(int cartId, CancellationToken cancellationToken)
        {
            CartReads++;
            return Task.FromResult(Lines);
        }

        public Task<CheckoutDeliveryMethod?> FindEnabledDeliveryMethodAsync(
            int deliveryMethodId,
            CancellationToken cancellationToken) =>
            Task.FromResult<CheckoutDeliveryMethod?>(
                new CheckoutDeliveryMethod(
                    DeliveryMethodId,
                    DeliveryMethod.StandardCode,
                    "Standard delivery",
                    null,
                    RequiresAddress: true,
                    Rate));

        public Task<IReadOnlyList<CheckoutPaymentMethod>> EnabledPaymentMethodsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CheckoutPaymentMethod>>(
                [new CheckoutPaymentMethod(PaymentProviderCodes.BankTransfer, "Bank transfer", null)]);

        /// <summary>
        /// No code matches. These tests are about the idempotency race and none of them sends a promo
        /// code, so the resolver short-circuits on a null code and never reaches this — answering "no
        /// such row" rather than throwing keeps that true if one of them ever does.
        /// </summary>
        public Task<PromoCodeTerms?> FindPromoCodeAsync(
            string normalisedCode,
            CancellationToken cancellationToken) =>
            Task.FromResult<PromoCodeTerms?>(null);

        public Task<CheckoutDistrict?> FindActiveDistrictAsync(
            int districtId,
            CancellationToken cancellationToken) =>
            Task.FromResult<CheckoutDistrict?>(
                new CheckoutDistrict(DistrictId, "Colombo", DeliveryZone.Colombo));

        public Task<CheckoutZoneRate?> FindZoneRateAsync(
            int deliveryMethodId,
            DeliveryZone zone,
            CancellationToken cancellationToken) =>
            Task.FromResult<CheckoutZoneRate?>(Rate);

        public Task<IReadOnlyDictionary<int, int>> AvailableStockAsync(
            IReadOnlyCollection<int> variantIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<int, int>>(new Dictionary<int, int>());

        public Task<OrderConfirmationDto?> FindOrderAsync(Guid publicId, CancellationToken cancellationToken) =>
            Task.FromResult<OrderConfirmationDto?>(Winner());

        public Task<IReadOnlyList<CheckoutDistrict>> ActiveDistrictsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CheckoutDeliveryMethod>> DeliveryMethodsForZoneAsync(
            DeliveryZone zone,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CheckoutPickupLocation>> ActivePickupLocationsAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CheckoutPickupLocation?> FindActivePickupLocationAsync(
            int pickupLocationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static CheckoutZoneRate Rate => new(true, 350m, null, 2, 4);

        private static OrderConfirmationDto Winner() =>
            new(
                WinningOrderNumber,
                Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
                nameof(OrderStatus.PendingPayment),

                // The shopper's own wording, which the reader now renders with OrderStatusLabels so a
                // pickup order does not read as "shipped" (doc 11 P9's pickup variant).
                "Awaiting payment",
                "nimali@example.com",
                "Nimali",
                "Perera",
                "0771234567",
                [],
                new OrderDeliveryDto(
                    DeliveryMethod.StandardCode,
                    "Standard delivery",
                    null,
                    "Nimali Perera",
                    "0771234567",
                    "14 Galle Road",
                    null,
                    "Kollupitiya",
                    "Colombo",
                    "00300",
                    null,
                    2,
                    4),
                1000m,
                1000m,
                0m,
                0m,
                350m,
                0m,
                1350m,
                "LKR",
                PaymentProviderCodes.BankTransfer,
                "Bank transfer",
                null,
                new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc));
    }

    private sealed class StubCartStore : ICartStore
    {
        public Task<CartIdentity?> FindAsync(CartLookup lookup, CancellationToken cancellationToken) =>
            Task.FromResult<CartIdentity?>(new CartIdentity(7, CartToken, null));

        public Task<CartIdentity> EnsureAsync(CartLookup lookup, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddItemAsync(
            int cartId,
            int variantId,
            short quantity,
            decimal unitPriceAtAdd,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> SetQuantityAsync(
            int cartId,
            int variantId,
            short quantity,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> RemoveItemAsync(int cartId, int variantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task TouchAsync(int cartId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CartMergeOutcome> MergeGuestIntoCustomerAsync(
            Guid guestToken,
            int customerUserId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubPlacementStore(OrderPlacementResult result) : IOrderPlacementStore
    {
        public Task<OrderPlacementResult> PlaceAsync(
            OrderPlacement placement,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    /// <summary>
    /// A number is only allocated once a placement is actually going to be attempted, so the paths this
    /// class tests must not reach it. Throwing is the assertion.
    /// </summary>
    private sealed class ThrowingNumberSeriesAllocator : INumberSeriesAllocator
    {
        public Task<string> AllocateOrderNumberAsync(
            DateTimeOffset placedAt,
            CancellationToken cancellationToken) =>
            Task.FromResult("CW-2026-000999");

        public Task<int> AllocateAsync(
            string seriesKey,
            string prefix,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
