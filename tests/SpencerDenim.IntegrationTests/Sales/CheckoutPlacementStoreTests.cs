using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// <c>IOrderPlacementStore</c> against a real SQL Server: doc 08 §5.4's one transaction, driven at the
/// port rather than over HTTP.
/// <para>
/// <b>Why not over HTTP.</b> The <c>OUT_OF_STOCK</c> branch of §5.4 step 3 is <i>unreachable</i> from a
/// sequential HTTP client by design: the use case re-reads the bag immediately before reserving, so a
/// line that has gone is caught as G-08's "adjust your bag" long before the reserving UPDATE is reached.
/// The branch only fires when the stock goes inside that window — a race, proved as a race in
/// <c>CheckoutConcurrencyTests</c>. What a race cannot prove is the <b>detail</b>: that the rollback
/// releases the reservations already taken in the same attempt, and that the shortfall reported is the
/// stock as it now stands. This file asks the store for an order it cannot fill and checks exactly that.
/// </para>
/// </summary>
public sealed class CheckoutPlacementStoreTests(SqlServerDatabaseFixture sqlServer)
    : CheckoutTestBase(sqlServer)
{
    /// <summary>
    /// Doc 08 §5.4 step 3, in detail: one line reserves, the next cannot, and **the first one's
    /// reservation goes back**. A store that committed the lines it managed would leave stock held for an
    /// order that does not exist — invisible until the nightly reconciliation, or until the item sold out
    /// for everybody else.
    /// </summary>
    [Fact]
    public async Task A_line_that_cannot_be_reserved_rolls_back_the_lines_that_could()
    {
        SqlServer.SkipIfUnavailable();

        var variantIds = await CheckoutVariantIdsAsync(2);

        // Ordered ascending, because the store reserves in VariantId order (doc 08 §5.3) and the point of
        // this test is that the *earlier* one is released when the later one fails.
        var first = Math.Min(variantIds[0], variantIds[1]);
        var second = Math.Max(variantIds[0], variantIds[1]);

        await SetStockAsync(first, 10);
        await SetStockAsync(second, 1);

        var (_, cartId, _) = await NewBagAsync(first);
        var placement = await BuildPlacementAsync(
            cartId,
            [(first, 2), (second, 2)]);

        using var scope = CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOrderPlacementStore>();

        var result = await store.PlaceAsync(placement, Token);

        Assert.Equal(OrderPlacementStatus.OutOfStock, result.Status);
        Assert.Null(result.PublicId);

        // The shortfall is the stock as it now stands, for the line that could not be filled.
        Assert.Equal(1, result.ShortLines[second]);

        // **Nothing is held.** This is the assertion that a partial commit would fail.
        Assert.Equal(0, (await StockAsync(first)).Reserved);
        Assert.Equal(0, (await StockAsync(second)).Reserved);

        // No order, no payment, no history — and the bag is untouched.
        using var read = CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal(
            0,
            await db.Orders.CountAsync(
                order => order.CheckoutIdempotencyKey == placement.CheckoutIdempotencyKey,
                Token));
        Assert.Equal(1, await CartItemCountAsync(cartId));
    }

    /// <summary>
    /// A line whose variant has no stock row at all. "No figure" and "none in stock" must be the same
    /// answer: a reserve that matched nothing is insufficient stock, not a missing-row exception.
    /// </summary>
    [Fact]
    public async Task A_variant_with_nothing_in_stock_is_a_shortfall_of_zero_and_not_an_error()
    {
        SqlServer.SkipIfUnavailable();

        var variantId = await CheckoutVariantIdAsync();

        // The bag first, **then** the stock is emptied. The other order round fails in Add to Bag, which
        // refuses an out-of-stock variant with a 409 — a bag cannot be arranged into this state from the
        // front, only fallen into by the stock going afterwards.
        var (_, cartId, _) = await NewBagAsync(variantId);
        await SetStockAsync(variantId, 0);

        var placement = await BuildPlacementAsync(cartId, [(variantId, 1)]);

        using var scope = CreateScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<IOrderPlacementStore>()
            .PlaceAsync(placement, Token);

        Assert.Equal(OrderPlacementStatus.OutOfStock, result.Status);
        Assert.Equal(0, result.ShortLines[variantId]);
    }

    /// <summary>
    /// The same idempotency key twice, through the store. The second attempt must report a duplicate and
    /// — the part that matters — must **not** leave a second reservation behind.
    /// </summary>
    [Fact]
    public async Task A_repeated_checkout_key_is_reported_as_a_duplicate_and_reserves_nothing_twice()
    {
        SqlServer.SkipIfUnavailable();

        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);

        var (_, cartId, _) = await NewBagAsync(variantId);
        var placement = await BuildPlacementAsync(cartId, [(variantId, 2)]);

        using var scope = CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOrderPlacementStore>();

        var first = await store.PlaceAsync(placement, Token);
        Assert.Equal(OrderPlacementStatus.Placed, first.Status);
        Assert.Equal(2, (await StockAsync(variantId)).Reserved);

        // A different order number, because the use case allocates one per attempt and gaps are allowed
        // (doc 08 §3.5) — so the only thing stopping a second order is the idempotency key's index.
        var replay = placement with { OrderNumber = placement.OrderNumber + "-R" };

        var second = await store.PlaceAsync(replay, Token);

        Assert.Equal(OrderPlacementStatus.DuplicateSubmission, second.Status);
        Assert.Equal(2, (await StockAsync(variantId)).Reserved);

        using var read = CreateScope();
        Assert.Equal(
            1,
            await read.ServiceProvider.GetRequiredService<AppDbContext>().Orders
                .CountAsync(
                    order => order.CheckoutIdempotencyKey == placement.CheckoutIdempotencyKey,
                    Token));
    }

    /// <summary>
    /// Doc 11 P6 step 10, per payment method, **without naming a gateway**: instructions that send the
    /// customer elsewhere (a redirect link or a redirect form) are not emailed as bank-transfer
    /// instructions. The only payment-instructions template there is says "pay by bank or online transfer",
    /// and a redirecting provider's page, not an email, is where the customer pays.
    /// </summary>
    [Theory]
    [InlineData("link")]
    [InlineData("form")]
    public async Task Instructions_that_send_the_customer_elsewhere_queue_no_bank_transfer_email(string kind)
    {
        SqlServer.SkipIfUnavailable();

        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);

        var (_, cartId, _) = await NewBagAsync(variantId);
        var placement = await BuildPlacementAsync(cartId, [(variantId, 1)]);

        var redirecting = kind == "link"
            ? Instructions(placement) with { RedirectUrl = "https://pay.example.test/checkout" }
            : Instructions(placement) with
            {
                RedirectForm = PaymentRedirectForm.Post(
                    "https://pay.example.test/checkout",
                    [new PaymentRedirectFormField("order_id", placement.OrderNumber)]),
            };

        using var scope = CreateScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<IOrderPlacementStore>()
            .PlaceAsync(placement with { Instructions = redirecting }, Token);

        Assert.Equal(OrderPlacementStatus.Placed, result.Status);

        // The order-placed confirmation is queued for every order; the bank-transfer one is not.
        Assert.Equal(
            [EmailTemplateCodes.OrderPlaced],
            (await OutboxTemplatesAsync(result.PublicId!.Value)).ToArray());
    }

    /// <summary>
    /// The control for the test above: the same placement with ordinary bank instructions queues both
    /// emails, so the redirect rule is proved to be the thing that removed the second.
    /// </summary>
    [Fact]
    public async Task Instructions_with_bank_details_queue_the_order_confirmation_and_the_instructions()
    {
        SqlServer.SkipIfUnavailable();

        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);

        var (_, cartId, _) = await NewBagAsync(variantId);
        var placement = await BuildPlacementAsync(cartId, [(variantId, 1)]);

        using var scope = CreateScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<IOrderPlacementStore>()
            .PlaceAsync(placement with { Instructions = Instructions(placement) }, Token);

        Assert.Equal(OrderPlacementStatus.Placed, result.Status);

        Assert.Equal(
            [EmailTemplateCodes.OrderPlaced, EmailTemplateCodes.BankTransferInstructions],
            (await OutboxTemplatesAsync(result.PublicId!.Value)).ToArray());
    }

    /// <summary>Bank-style instructions for a placement: a field to print and a slip to accept, no redirect.</summary>
    private static PaymentInstructions Instructions(OrderPlacement placement) =>
        new(
            PaymentProviderCodes.BankTransfer,
            placement.OrderNumber,
            placement.Pricing.GrandTotal,
            placement.Payment.PaymentDueAtUtc,
            [new PaymentInstructionField("Account number", "0000 1111 2222", Copyable: true)],
            null,
            RedirectUrl: null,
            AcceptsProof: true);

    private async Task<List<string>> OutboxTemplatesAsync(Guid publicId)
    {
        using var scope = CreateScope();
        var id = publicId.ToString("D");

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().EmailOutbox
            .AsNoTracking()
            .Where(message => message.RelatedEntityId == id)
            .OrderBy(message => message.Id)
            .Select(message => message.TemplateCode)
            .ToListAsync(Token);
    }

    /// <summary>
    /// Builds a placement for the given (variant, quantity) pairs, priced from the live catalogue the way
    /// the use case does, so the rows satisfy every CHECK constraint the real path satisfies.
    /// </summary>
    private async Task<OrderPlacement> BuildPlacementAsync(
        int cartId,
        IReadOnlyList<(int VariantId, short Quantity)> lines)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var districtId = await DistrictIdAsync("Colombo");
        var deliveryMethodId = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var pricingLines = new List<OrderPricingLine>();
        var snapshots = new List<(int VariantId, OrderLineSnapshotSeed Seed)>();

        foreach (var (variantId, quantity) in lines)
        {
            var row = await db.ProductVariants
                .AsNoTracking()
                .Where(variant => variant.Id == variantId)
                .Select(variant => new
                {
                    variant.Id,
                    variant.Sku,
                    variant.PriceOverride,
                    variant.CompareAtPriceOverride,
                    ColourName = variant.ProductColour!.Name,
                    SizeLabel = variant.Size!.Label,
                    variant.ProductId,
                    ProductName = variant.Product!.Name,
                    variant.Product.ProductCode,
                    variant.Product.BasePrice,
                    variant.Product.CompareAtPrice,
                    variant.Product.DepartmentId,
                    CategoryId = variant.Product.PrimaryCategoryId,
                })
                .SingleAsync(Token);

            var list = Domain.Catalog.CatalogPricing.ListPrice(row.BasePrice, row.PriceOverride);
            var original = Domain.Catalog.CatalogPricing.OriginalPrice(
                list,
                row.CompareAtPrice,
                row.CompareAtPriceOverride);
            var sale = Domain.Catalog.CatalogPricing.SalePrice(list, campaignPrice: null);

            pricingLines.Add(new OrderPricingLine(variantId, quantity, original, list, sale));
            snapshots.Add((
                variantId,
                new OrderLineSnapshotSeed(
                    row.ProductId,
                    row.DepartmentId,
                    row.CategoryId,
                    row.ProductName,
                    row.ProductCode,
                    row.Sku,
                    row.ColourName,
                    row.SizeLabel)));
        }

        // The same zone rate the use case would read, so the totals satisfy CK_Orders_Totals.
        var rate = await db.DeliveryMethodZoneRates
            .AsNoTracking()
            .Where(candidate =>
                candidate.DeliveryMethodId == deliveryMethodId
                && candidate.DeliveryZone == DeliveryZone.Colombo)
            .SingleAsync(Token);

        var pricing = OrderPricing.Price(
            pricingLines,
            new DeliveryCharge(rate.Fee, rate.FreeAboveAmount));

        var placedAt = DateTime.UtcNow;

        var orderLines = pricing.Lines
            .Select((priced, index) =>
            {
                var seed = snapshots.Single(entry => entry.VariantId == priced.VariantId).Seed;

                return new OrderLineSnapshot(
                    (short)(index + 1),
                    seed.ProductId,
                    priced.VariantId,
                    seed.DepartmentId,
                    seed.CategoryId,
                    seed.ProductName,
                    seed.ProductCode,
                    seed.Sku,
                    seed.ColourName,
                    seed.SizeLabel,
                    null,
                    priced);
            })
            .ToList();

        return new OrderPlacement(
            cartId,
            DocumentNumber.Format(DocumentNumber.OrderNumberPrefix, placedAt.Year, Random.Shared.Next(1, 999_999)),
            Guid.NewGuid(),
            null,
            PaymentProviderCodes.BankTransfer,
            "store-test@example.com",
            "0771234567",
            "Store",
            "Test",
            districtId,
            "Colombo",
            DeliveryZone.Colombo,
            new AddressSnapshot
            {
                RecipientName = "Store Test",
                Phone = "0771234567",
                Line1 = "14 Galle Road",
                City = "Kollupitiya",
            },
            deliveryMethodId,
            null,
            new DeliverySnapshot
            {
                DeliveryMethodCode = DeliveryMethod.StandardCode,
                DeliveryMethodName = "Standard delivery",
                EstimatedDaysMin = rate.EstimatedDaysMin,
                EstimatedDaysMax = rate.EstimatedDaysMax,
            },
            pricing,
            orderLines.AsReadOnly(),
            null,
            null,
            placedAt,

            // PAY-03: the provider's answer, which placement copies verbatim. Spelled out rather than
            // taken from BankTransferProvider, so this fixture states what a bank transfer's opening row
            // is and a change to the provider shows up here as a failing assertion rather than silently
            // changing what the test exercises.
            new PaymentInitiation(PaymentStatus.Pending, placedAt.AddHours(48)),

            // W3-06: the display name the confirmation email prints, and the provider's instructions.
            //
            // **`null` instructions, deliberately, and it is the state this fixture should be in.** It
            // means "the provider could not say how to pay", which for a bank transfer is an unset
            // `Bank.*` settings group — the shop's real state today. So the placement transaction queues
            // `OrderPlaced` and no instructions email, and every assertion in this file about the order
            // row is unaffected. `OrderPlacementEmailTests` is where the two-email case is exercised,
            // through the real checkout endpoint with the bank details set.
            "Bank / online transfer",
            null);
    }

    private sealed record OrderLineSnapshotSeed(
        int ProductId,
        int DepartmentId,
        int CategoryId,
        string ProductName,
        string ProductCode,
        string Sku,
        string ColourName,
        string SizeLabel);
}
