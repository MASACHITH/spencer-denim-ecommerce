using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Cart;
using SpencerDenim.Application.Sales.Cart.Abstractions;
using SpencerDenim.Application.Sales.Cart.Contracts;
using SpencerDenim.Application.Sales.Checkout;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Application.Sales.Payments.Abstractions;
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

    /// <summary>
    /// <b>Checkout describes the payment before there is a payment row, and says so.</b> The provider is
    /// asked for its instructions ahead of the placement transaction (doc 08 §5.4 keeps reads out of it), so
    /// the request cannot name an attempt: <c>PaymentId</c> and <c>ProviderReference</c> are null. A provider
    /// that needs them answers null here and describes itself on the order page, where
    /// <c>GetOrderQueryHandler</c> fills them in. Pinned because the day somebody "helpfully" invents an id
    /// here, a provider would build a hosted-checkout reference around a payment that does not exist.
    /// </summary>
    [Fact]
    public async Task Checkout_asks_the_provider_to_describe_the_payment_before_any_payment_row_exists()
    {
        var provider = new StubPaymentProvider();
        var reader = new RacingCheckoutReader { Lines = [Line(CartLineStatus.Buyable)], WinnerExists = false };
        var placements = new StubPlacementStore(OrderPlacementResult.Placed(Guid.NewGuid()));

        var result = await HandleAsync(reader, placements, provider);

        Assert.Equal(PlaceOrderStatus.Placed, result.Status);

        var request = Assert.Single(provider.DescribeRequests);
        Assert.Equal("CW-2026-000999", request.OrderNumber);
        Assert.Equal(1350m, request.AmountLkr);
        Assert.Null(request.PaymentId);
        Assert.Null(request.ProviderReference);
    }

    // ------------------------------------------------------------------------------------------------
    // Issue #107: the placement throttle. Same stubs as the race tests above, because the question is the
    // same one asked from the other side: which requests count as "a new order about to hold stock"?
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A new placement takes exactly one permit, for the visitor's address and nobody else's.
    /// </summary>
    [Fact]
    public async Task A_new_placement_takes_one_permit_for_the_visitors_address()
    {
        var throttle = new RecordingThrottle(allow: true);
        var reader = new RacingCheckoutReader { Lines = [Line(CartLineStatus.Buyable)], WinnerExists = false };
        var placements = new StubPlacementStore(OrderPlacementResult.Placed(Guid.NewGuid()));

        var result = await HandleAsync(reader, placements, throttle: throttle, ipAddress: "203.0.113.7");

        Assert.Equal(PlaceOrderStatus.Placed, result.Status);
        Assert.Equal(["203.0.113.7"], throttle.Partitions);
        Assert.Equal(1, placements.Calls);
    }

    /// <summary>
    /// <b>The whole point.</b> With the permit refused, the handler stops before it allocates an order
    /// number, before it asks the payment provider anything and before the placement transaction opens —
    /// so nothing is reserved and nothing is written. The refusal is a status, not an exception.
    /// </summary>
    [Fact]
    public async Task A_refused_permit_places_nothing_and_spends_no_order_number()
    {
        var throttle = new RecordingThrottle(allow: false);
        var provider = new StubPaymentProvider();
        var numbers = new CountingNumberSeriesAllocator();
        var reader = new RacingCheckoutReader { Lines = [Line(CartLineStatus.Buyable)], WinnerExists = false };
        var placements = new StubPlacementStore(OrderPlacementResult.Placed(Guid.NewGuid()));

        var result = await HandleAsync(reader, placements, provider, throttle, numbers, ipAddress: "203.0.113.7");

        Assert.Equal(PlaceOrderStatus.RateLimited, result.Status);
        Assert.Null(result.Confirmation);
        Assert.Equal(1, throttle.Calls);
        Assert.Equal(0, placements.Calls);
        Assert.Equal(0, numbers.Allocations);
        Assert.Equal(0, provider.Initiations);
    }

    /// <summary>
    /// <b>Replay exemption, sequential half.</b> A key that has already placed an order is answered with
    /// that order before the limit is consulted — so a shopper retrying on a bad connection is neither
    /// counted nor refused, however spent the address is. The throttle is never even asked.
    /// </summary>
    [Fact]
    public async Task A_replay_of_a_placed_key_is_answered_with_its_order_without_touching_the_limit()
    {
        var throttle = new RecordingThrottle(allow: false);
        var reader = new RacingCheckoutReader { Lines = [], WinnerFoundOnFirstLook = true };

        var result = await HandleAsync(reader, throttle: throttle, ipAddress: "203.0.113.7");

        Assert.Equal(PlaceOrderStatus.AlreadyPlaced, result.Status);
        Assert.True(result.Confirmation!.IsDuplicateSubmission);
        Assert.Equal(0, throttle.Calls);
    }

    /// <summary>
    /// <b>Replay exemption, racing half.</b> The first submission commits after the pre-check and before
    /// this request's permit. The permit is refused, but the order exists, so the honest answer is the
    /// order — the same re-check every other refusal in this handler makes.
    /// </summary>
    [Fact]
    public async Task A_refused_permit_still_replays_an_order_that_committed_in_the_meantime()
    {
        var throttle = new RecordingThrottle(allow: false);
        var reader = new RacingCheckoutReader { Lines = [Line(CartLineStatus.Buyable)] };
        var placements = new StubPlacementStore(OrderPlacementResult.Placed(Guid.NewGuid()));

        var result = await HandleAsync(reader, placements, throttle: throttle, ipAddress: "203.0.113.7");

        Assert.Equal(PlaceOrderStatus.AlreadyPlaced, result.Status);
        Assert.Equal(RacingCheckoutReader.WinningOrderNumber, result.Confirmation!.OrderNumber);
        Assert.Equal(0, placements.Calls);
    }

    /// <summary>
    /// A request that is refused on its own merits — a missing field, an empty bag, a line that cannot be
    /// bought, a total that moved — has reserved nothing and so costs no permit. This is the newsletter's
    /// "a mistyped address costs nothing" for a form with many more ways to be wrong, and it is what keeps
    /// a shopper who fixes three typos from being locked out by their own corrections.
    /// </summary>
    [Fact]
    public async Task A_request_refused_on_its_own_merits_costs_no_permit()
    {
        var throttle = new RecordingThrottle(allow: true);
        var buyable = new RacingCheckoutReader { Lines = [Line(CartLineStatus.Buyable)], WinnerExists = false };

        var invalid = await HandleAsync(buyable, throttle: throttle, firstName: null);
        Assert.Equal(PlaceOrderStatus.Invalid, invalid.Status);

        var emptyBag = await HandleAsync(
            new RacingCheckoutReader { Lines = [], WinnerExists = false },
            throttle: throttle);
        Assert.Equal(PlaceOrderStatus.Blocked, emptyBag.Status);

        var unbuyable = await HandleAsync(
            new RacingCheckoutReader { Lines = [Line(CartLineStatus.OutOfStock)], WinnerExists = false },
            throttle: throttle);
        Assert.Equal(PlaceOrderStatus.Blocked, unbuyable.Status);

        var moved = await HandleAsync(buyable, throttle: throttle, expectedGrandTotal: 1m);
        Assert.Equal(PlaceOrderStatus.TotalChanged, moved.Status);

        Assert.Equal(0, throttle.Calls);
    }

    /// <summary>
    /// The accepted imprecision, pinned so it is a decision and not an accident: once the permit is taken
    /// the request is a real placement attempt, and losing the stock race inside the transaction does not
    /// give it back. A fixed window cannot return a permit, and an address that keeps losing races is
    /// exactly the one that should be slowed.
    /// </summary>
    [Fact]
    public async Task A_placement_that_loses_the_stock_race_has_still_spent_its_permit()
    {
        var throttle = new RecordingThrottle(allow: true);
        var reader = new RacingCheckoutReader { Lines = [Line(CartLineStatus.Buyable)], WinnerExists = false };
        var placements = new StubPlacementStore(
            OrderPlacementResult.OutOfStock(new Dictionary<int, int> { [VariantId] = 0 }));

        var result = await HandleAsync(reader, placements, throttle: throttle);

        Assert.Equal(PlaceOrderStatus.OutOfStock, result.Status);
        Assert.Equal(1, throttle.Calls);
    }

    private static Task<PlaceOrderResult> HandleAsync(
        RacingCheckoutReader reader,
        IOrderPlacementStore? placements = null,
        IPaymentProvider? provider = null,
        IOrderPlacementThrottle? throttle = null,
        INumberSeriesAllocator? numbers = null,
        string? firstName = "Nimali",
        decimal? expectedGrandTotal = 1350m,
        string? ipAddress = null)
    {
        var handler = new PlaceOrderCommandHandler(
            new StubCartStore(),
            reader,
            numbers ?? new ThrowingNumberSeriesAllocator(),
            placements ?? new StubPlacementStore(OrderPlacementResult.DuplicateSubmission),
            new FixedClock(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero)),

            // PAY-03: the handler takes a registry, not a bank-transfer setting. The race tests never
            // reach the provider — they all refuse before the initiation call — but the registry has to be
            // real enough to resolve the provider code the command carries.
            new StubPaymentProviderRegistry(provider),

            // Issue #107. Open by default, so the race tests above stay about the race: a test about the
            // limit passes a throttle of its own.
            throttle ?? new RecordingThrottle(allow: true));

        return handler.HandleAsync(
            new PlaceOrderCommand(
                CartLookup.ForGuest(CartToken),
                CheckoutKey,
                firstName,
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
                expectedGrandTotal,
                null,
                ipAddress),
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

            // No campaign priced this line: original, list and sale are all Rs. 1,000.00 (ADM-14).
            null,
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

        // Owner decision 2026-10-21: every request in these tests is a Sri Lankan one.
        public Task<IReadOnlyList<CheckoutCountry>> CountriesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CheckoutCountry>>([SriLanka]);

        public Task<CheckoutCountry?> FindCountryAsync(string countryCode, CancellationToken cancellationToken) =>
            Task.FromResult<CheckoutCountry?>(countryCode == "LK" ? SriLanka : null);

        public Task<CheckoutDeliveryMethod?> InternationalMethodAsync(CancellationToken cancellationToken) =>
            Task.FromResult<CheckoutDeliveryMethod?>(null);

        private static CheckoutCountry SriLanka { get; } =
            new("LK", "Sri Lanka", true, false, false,
                StandardQuote: new OverseasQuote(OverseasRefusal.None, 0m, 0, 0),
                ExpressQuote: new OverseasQuote(OverseasRefusal.ExpressNotPriced, 0m, 0, 0));

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
        /// <summary>How many times the placement transaction was asked for. Zero proves nothing was written.</summary>
        public int Calls { get; private set; }

        public Task<OrderPlacementResult> PlaceAsync(
            OrderPlacement placement,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    /// <summary>A throttle that answers what a test told it to and records every address it was asked about.</summary>
    private sealed class RecordingThrottle(bool allow) : IOrderPlacementThrottle
    {
        public List<string?> Partitions { get; } = [];

        public int Calls => Partitions.Count;

        public TimeSpan Window => TimeSpan.FromMinutes(10);

        public bool TryAcquire(string? partitionKey)
        {
            Partitions.Add(partitionKey);
            return allow;
        }
    }

    /// <summary>Counts the order numbers handed out, so a test can show a refusal burned none.</summary>
    private sealed class CountingNumberSeriesAllocator : INumberSeriesAllocator
    {
        public int Allocations { get; private set; }

        public Task<string> AllocateOrderNumberAsync(
            DateTimeOffset placedAt,
            CancellationToken cancellationToken)
        {
            Allocations++;
            return Task.FromResult("CW-2026-000999");
        }

        public Task<int> AllocateAsync(
            string seriesKey,
            string prefix,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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
