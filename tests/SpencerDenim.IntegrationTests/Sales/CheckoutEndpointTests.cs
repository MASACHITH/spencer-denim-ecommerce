using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Cart.Contracts;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Checkout and the order it writes, against a real SQL Server (CHK-01..04, doc 11 P6).
/// <para>
/// <b>Everything here is asserted against the database, not against the response.</b> The response is what
/// the API said it did; the rows are what it did. The money invariants in particular are read back out of
/// <c>sales.Orders</c> and <c>sales.OrderLines</c> and put through the identities
/// <c>CK_Orders_Totals</c> enforces — plus the one it cannot,
/// <c>ItemsSubtotal = Σ LineSubtotal</c>, which W3-03's review flagged as resting entirely on the code
/// that writes the lines.
/// </para>
/// </summary>
public sealed class CheckoutEndpointTests(SqlServerDatabaseFixture sqlServer) : CheckoutTestBase(sqlServer)
{
    /// <summary><c>SD-2026-000123</c>: the shape doc 08 §3.5 gives, with a four-digit year.</summary>
    private static readonly Regex OrderNumberShape = new(@"^SD-\d{4}-\d{6,}$", RegexOptions.None);

    [Fact]
    public async Task A_checkout_without_a_district_prices_the_items_and_quotes_no_delivery()
    {
        var variantId = await InStockVariantIdAsync();
        var (_, _, api) = await NewBagAsync(variantId, quantity: 2);

        var (response, session) = await api.GetAsync(null, null, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(session);

        // Doc 08 §6 step 10 needs a zone. There is none, so there is no fee and no total — and null is
        // not zero, because zero would be a promise of free delivery.
        Assert.Null(session.Totals.DeliveryFeeAmount);
        Assert.Null(session.Totals.GrandTotal);
        Assert.Null(session.SelectedDistrictId);
        Assert.True(session.Totals.ItemsSubtotal > 0m);

        // The twenty-five districts of doc 08 §9, so the form can be filled in.
        Assert.Equal(25, session.Districts.Count);
        Assert.Contains(session.Districts, district => district.Name == "Colombo");
    }

    /// <summary>
    /// Every checkout answer is personal — it holds the idempotency token and, once submitted, a name and
    /// an address. A shared cache keeping one of these would hand a shopper another shopper's checkout.
    /// </summary>
    [Fact]
    public async Task A_checkout_is_never_cacheable_and_varies_on_the_cookie()
    {
        var variantId = await InStockVariantIdAsync();
        var (_, _, api) = await NewBagAsync(variantId);

        var (response, _) = await api.GetAsync(null, null, Token);

        var cacheControl = Assert.Single(response.Headers.GetValues("Cache-Control"));
        Assert.Contains("no-store", cacheControl, StringComparison.Ordinal);
        Assert.Contains("private", cacheControl, StringComparison.Ordinal);
        Assert.Equal("Cookie", Assert.Single(response.Headers.GetValues("Vary")));
    }

    [Fact]
    public async Task A_visitor_with_no_bag_is_told_the_bag_is_empty_rather_than_given_one()
    {
        var api = NewCheckoutVisitor();
        var cartsBefore = await CartCountAsync();

        var (response, session) = await api.GetAsync(null, null, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(session);
        Assert.Empty(session.Lines);
        Assert.Contains(CheckoutBlocker.EmptyBag, session.Blockers);

        // Doc 08 §3.5: the cart row is created on the first Add to Bag, never on a page view — so a
        // checkout page load must not create one, and must not issue a cookie it has no bag for.
        Assert.DoesNotContain(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            value => value.StartsWith("sd_cart=", StringComparison.Ordinal)
                && !value.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));

        // Counted before and after, not "anything created in the last five seconds": the suite shares one
        // database and runs sequentially, so a window-based count measures the previous test's bag. That
        // is how this assertion first failed — it was wrong, the code was right.
        Assert.Equal(cartsBefore, await CartCountAsync());
    }

    [Fact]
    public async Task Standard_delivery_to_Colombo_is_priced_from_the_seeded_zone_rate()
    {
        var variantId = await InStockVariantIdAsync();
        var (_, _, api) = await NewBagAsync(variantId);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (response, session) = await api.GetAsync(colombo, standard, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(session);

        var option = Assert.Single(session.DeliveryOptions, candidate => candidate.Id == standard);

        // Doc 08 §9's Colombo Standard rate: Rs. 400, free above Rs. 10,000, 1–2 days. Placeholders the
        // client has still to confirm (D8), and the point of the assertion is that the figure on the
        // screen comes from the table rather than from anywhere else.
        Assert.True(option.IsAvailable);
        Assert.Equal(400m, option.Fee);
        Assert.Equal(10_000m, option.FreeAboveAmount);
        Assert.Equal((byte)1, option.EstimatedDaysMin);
        Assert.Equal((byte)2, option.EstimatedDaysMax);

        Assert.Equal(400m, session.Totals.DeliveryFeeAmount);
        Assert.Equal(
            session.Totals.ItemsSubtotal + 400m - session.Totals.DeliveryDiscountAmount,
            session.Totals.GrandTotal);
    }

    /// <summary>
    /// Doc 08 §9 makes Express island-wide <b>unavailable</b>, with a row present saying so rather than no
    /// row at all. The option is listed and unselectable, and carries no fee — "we do not know what this
    /// would cost" and "this costs nothing" are different answers.
    /// </summary>
    [Fact]
    public async Task Express_is_offered_in_Colombo_and_refused_island_wide()
    {
        var variantId = await InStockVariantIdAsync();
        var (_, _, api) = await NewBagAsync(variantId);
        var express = await DeliveryMethodIdAsync(DeliveryMethod.ExpressCode);

        var (_, colomboSession) = await api.GetAsync(await DistrictIdAsync("Colombo"), express, Token);
        var colomboOption = Assert.Single(colomboSession!.DeliveryOptions, option => option.Id == express);

        Assert.True(colomboOption.IsAvailable);
        Assert.Equal(950m, colomboOption.Fee);

        var (_, jaffnaSession) = await api.GetAsync(await DistrictIdAsync("Jaffna"), express, Token);
        var jaffnaOption = Assert.Single(jaffnaSession!.DeliveryOptions, option => option.Id == express);

        Assert.False(jaffnaOption.IsAvailable);
        Assert.Null(jaffnaOption.Fee);
        Assert.Null(jaffnaOption.FeeAfterDiscount);

        // It was asked for and could not be given, so nothing is selected: the page must not show a
        // chosen option that the placement would refuse.
        Assert.Null(jaffnaSession.SelectedDeliveryMethodId);
        Assert.Null(jaffnaSession.Totals.GrandTotal);
    }

    /// <summary>
    /// A delivery method that is switched off is never offered — and <c>PICKUP</c> is the one the shop
    /// ships switched off (doc 08 §9), because §9 also seeds <b>zero</b> pickup locations (client input,
    /// D8) and doc 11 P6 requires at least one. An enabled method with nowhere to collect from is an
    /// option that fails after the shopper has chosen it.
    /// </summary>
    /// <remarks>
    /// <b>It asserts the method is not offered, and deliberately not that the shop has no pickup
    /// locations.</b> The first version did both and failed in the full run, correctly: this assembly
    /// shares one database and <c>SalesSchemaTestBase</c> inserts a <c>sales.PickupLocations</c> row of
    /// its own to hang its constraint tests off, so <c>Assert.Empty(session.PickupLocations)</c> was an
    /// assertion about which other suites had run first. What it now checks is stronger anyway: the
    /// method stays unoffered <i>even when</i> a location exists, which is the enabled flag doing its
    /// job rather than an empty table hiding the question.
    /// </remarks>
    [Fact]
    public async Task A_delivery_method_that_is_switched_off_is_not_offered()
    {
        var variantId = await InStockVariantIdAsync();
        var (_, _, api) = await NewBagAsync(variantId);

        var (_, session) = await api.GetAsync(await DistrictIdAsync("Colombo"), null, Token);

        Assert.DoesNotContain(
            session!.DeliveryOptions,
            option => option.Code == DeliveryMethod.PickupCode);

        // The two that are on are offered, so the list is not empty for the wrong reason.
        Assert.Contains(session.DeliveryOptions, option => option.Code == DeliveryMethod.StandardCode);
        Assert.Contains(session.DeliveryOptions, option => option.Code == DeliveryMethod.ExpressCode);
    }

    /// <summary>Doc 08 §9: bank transfer enabled, PayPal disabled until its credentials and FX rate exist.</summary>
    /// <remarks>
    /// <b>Asserted by code, not by count</b>, for the reason above: <c>SalesSchemaTestBase</c> inserts an
    /// enabled <c>sales.PaymentProviders</c> row of its own, so <c>Assert.Single</c> was an assertion
    /// about test ordering. What matters is which of the two <i>seeded</i> providers a shopper is offered.
    /// </remarks>
    [Fact]
    public async Task PayPal_is_not_offered_and_bank_transfer_is()
    {
        var variantId = await InStockVariantIdAsync();
        var (_, _, api) = await NewBagAsync(variantId);

        var (_, session) = await api.GetAsync(null, null, Token);

        Assert.Contains(
            session!.PaymentMethods,
            method => method.Code == PaymentProviderCodes.BankTransfer);
        Assert.DoesNotContain(
            session.PaymentMethods,
            method => method.Code == PaymentProviderCodes.PayPal);
    }

    [Fact]
    public async Task Placing_an_order_answers_201_with_the_order_s_own_address()
    {
        var variantId = await InStockVariantIdAsync();
        var (_, cartId, api) = await NewBagAsync(variantId, quantity: 2);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);
        var body = ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session));

        var (response, order) = await api.PlaceAsync(body, Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(order);
        Assert.Equal($"/api/orders/{order.PublicId:D}", response.Headers.Location?.ToString());
        Assert.Matches(OrderNumberShape, order.OrderNumber);
        Assert.Equal(nameof(OrderStatus.PendingPayment), order.Status);

        // Doc 08 §5.4 step 5 and doc 11 P6 step 9.3, read back off the row rather than off the response.
        var row = await OrderByNumberAsync(order.OrderNumber);

        Assert.Equal(OrderStatus.PendingPayment, row.Status);
        Assert.Equal(OrderStockState.Reserved, row.StockState);
        Assert.Equal(PaymentProviderCodes.BankTransfer, row.PaymentProviderCode);
        Assert.Equal("nimali@example.com", row.Email);
        Assert.Equal("NIMALI@EXAMPLE.COM", row.EmailNormalized);
        Assert.Equal(colombo, row.DeliveryDistrictId);
        Assert.Equal("Colombo", row.DeliveryDistrictName);
        Assert.Equal(DeliveryZone.Colombo, row.DeliveryZone);
        Assert.Equal(DeliveryMethod.StandardCode, row.Delivery.DeliveryMethodCode);
        Assert.Null(row.PickupLocationId);
        Assert.Equal("14 Galle Road", row.ShipAddress.Line1);
        Assert.Equal("Kollupitiya", row.ShipAddress.City);
        Assert.Equal(SalesCurrency.Lkr, row.Currency);

        // CHK-03: every checkout is a guest checkout today, because there is no customer sign-in.
        Assert.Null(row.CustomerUserId);

        // Doc 11 P6: "History row, source Customer", and the first row comes from nowhere.
        var history = Assert.Single(row.StatusHistory);
        Assert.Null(history.FromStatus);
        Assert.Equal(OrderStatus.PendingPayment, history.ToStatus);
        Assert.Equal(OrderStatusChangeSource.Customer, history.Source);

        // Doc 11 P6: 48 hours for a bank transfer.
        Assert.Equal(48, Math.Round((row.PaymentDueAtUtc - row.PlacedAtUtc).TotalHours));

        // Doc 11 P6 and doc 08 §8.4 want the customer's IP on the order for fraud checks, and **this is
        // not the place that can prove it**: `TestServer` reports no `Connection.RemoteIpAddress` at all,
        // so asserting a value here would be asserting a property of the test host. The column is
        // therefore expected to be null under this host, and `CheckoutPlacedIpTests` proves the wiring by
        // giving the host an address to report. Pinning the null is still worth doing: it says the
        // placement does not invent one.
        Assert.Null(row.PlacedIpAddress);

        // **The bag is emptied in the same transaction.** Doc 08 and doc 11 are silent on this; leaving
        // the lines would mean a shopper returning to their bag sees the items they have just bought,
        // with the badge still counting them, and the obvious next action places a second order.
        Assert.Equal(0, await CartItemCountAsync(cartId));
    }

    /// <summary>
    /// The money, read back out of the database and put through every identity — including
    /// <c>ItemsSubtotal = Σ LineSubtotal</c>, which no CHECK constraint can express because it spans rows.
    /// </summary>
    [Fact]
    public async Task The_order_s_totals_add_up_in_the_database()
    {
        var variantIds = await CheckoutVariantIdsAsync(3);
        var (_, _, api) = await NewBagAsync(variantIds[0], quantity: 2);

        // Three lines, so the sum is a sum of more than one thing.
        var cart = new CartApiClient(api.Http);
        await cart.AddAsync(variantIds[1], 1, Token);
        await cart.AddAsync(variantIds[2], 3, Token);

        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);
        var (response, order) = await api.PlaceAsync(
            ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session)),
            Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var row = await OrderByNumberAsync(order!.OrderNumber);

        Assert.Equal(3, row.Lines.Count);

        // The cross-row invariant. This is the one the review called out as resting entirely on the code
        // that writes the lines.
        Assert.Equal(row.Lines.Sum(line => line.LineSubtotal), row.ItemsSubtotal);
        Assert.Equal(row.Lines.Sum(line => line.UnitOriginalPrice * line.Quantity), row.ItemsOriginalTotal);
        Assert.Equal(row.Lines.Sum(line => line.UnitListPrice * line.Quantity), row.ItemsListTotal);

        // CK_Orders_Totals' four equalities.
        Assert.Equal(row.MarkdownDiscountTotal, row.ItemsOriginalTotal - row.ItemsListTotal);
        Assert.Equal(row.CampaignDiscountTotal, row.ItemsListTotal - row.ItemsSubtotal);
        Assert.Equal(
            row.GrandTotal,
            row.ItemsSubtotal - row.PromoDiscountAmount + row.DeliveryFeeAmount - row.DeliveryDiscountAmount);
        Assert.True(row.GrandTotal >= 0m);

        // CK_OrderLines_LineSubtotal and _LineTotal, per line.
        Assert.All(row.Lines, line =>
        {
            Assert.Equal(line.UnitPrice * line.Quantity, line.LineSubtotal);
            Assert.Equal(line.LineSubtotal - line.PromoDiscountAllocated, line.LineTotal);
            Assert.True(line.LineTotal >= 0m);
            Assert.True(line.UnitOriginalPrice >= line.UnitListPrice);
            Assert.True(line.UnitListPrice >= line.UnitPrice);
            Assert.True(line.UnitPrice > 0m);
        });

        // And the figure the shopper was shown is the figure in the row — the whole of G-10's purpose.
        Assert.Equal(session.Totals.GrandTotal, row.GrandTotal);

        // Line numbers are 1..n in the order the bag listed them.
        Assert.Equal([1, 2, 3], row.Lines.OrderBy(line => line.LineNumber).Select(line => (int)line.LineNumber));
    }

    /// <summary>
    /// **The price is frozen at placement, and this is where that is proved.** The bag reprices on every
    /// read; an order line must not. Changing the product's price *after* the order is placed must leave
    /// every figure on the order untouched.
    /// </summary>
    [Fact]
    public async Task A_price_change_after_placement_does_not_move_the_order()
    {
        var variantId = await InStockVariantIdAsync();
        var (_, _, api) = await NewBagAsync(variantId, quantity: 2);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);
        var (_, order) = await api.PlaceAsync(
            ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session)),
            Token);

        var before = await OrderByNumberAsync(order!.OrderNumber);

        // The shop raises the price.
        int productId;
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            productId = await db.ProductVariants
                .AsNoTracking()
                .Where(variant => variant.Id == variantId)
                .Select(variant => variant.ProductId)
                .SingleAsync(Token);

            await db.Products
                .Where(product => product.Id == productId)
                .ExecuteUpdateAsync(
                    product => product.SetProperty(row => row.BasePrice, row => row.BasePrice + 1_000m),
                    Token);
        }

        try
        {
            var after = await OrderByNumberAsync(order.OrderNumber);

            Assert.Equal(before.ItemsSubtotal, after.ItemsSubtotal);
            Assert.Equal(before.GrandTotal, after.GrandTotal);
            Assert.Equal(
                before.Lines.Single().UnitPrice,
                after.Lines.Single().UnitPrice);

            // And the confirmation a shopper reloads says the same.
            var (reloaded, confirmation) = await api.GetOrderAsync(order.PublicId.ToString("D"), Token);
            Assert.Equal(HttpStatusCode.OK, reloaded.StatusCode);
            Assert.Equal(before.GrandTotal, confirmation!.GrandTotal);
        }
        finally
        {
            // **Put the price back.** This is the one test here that edits the shared seeded catalogue, and
            // leaving the edit behind changed what every later test in the run priced — which is how it was
            // found: the next test failed, in the suite and not on its own. The suite shares one database
            // by design (doc 06), so a test that mutates reference data owns restoring it.
            using var scope = CreateScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Products
                .Where(product => product.Id == productId)
                .ExecuteUpdateAsync(
                    product => product.SetProperty(row => row.BasePrice, row => row.BasePrice - 1_000m),
                    Token);
        }
    }

    /// <summary>
    /// Doc 08 §4.1's "— → PendingPayment" side-effect list includes the payment row, and doc 11 P7's
    /// preconditions require an open bank payment on a PendingPayment order — so placement writes it, and
    /// W3-03's step 3 picks it up. Nothing further about the money is this task's.
    /// </summary>
    [Fact]
    public async Task Placement_opens_one_pending_bank_payment_and_settles_nothing()
    {
        var variantId = await InStockVariantIdAsync();
        var (_, _, api) = await NewBagAsync(variantId);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);
        var (_, order) = await api.PlaceAsync(
            ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session)),
            Token);

        var row = await OrderByNumberAsync(order!.OrderNumber);
        var payment = Assert.Single(row.Payments);

        Assert.Equal(PaymentProviderCodes.BankTransfer, payment.ProviderCode);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(row.GrandTotal, payment.Amount);
        Assert.Equal(SalesCurrency.Lkr, payment.Currency);

        // Nothing has settled: CK_Payments_IsOrderSettlement allows the flag only for statuses 3, 8 and 9.
        Assert.False(payment.IsOrderSettlement);
        Assert.Null(payment.CompletedAtUtc);
        Assert.Null(payment.ApprovedByUserId);

        // And no invoice: doc 08 §3.5 issues a gapless invoice number inside the Paid transaction, which
        // is W3-03's step 3.
        Assert.Null(row.InvoiceNumber);
        Assert.Null(row.PaidAtUtc);
    }

    [Fact]
    public async Task Stock_is_reserved_for_every_line_and_nothing_is_deducted()
    {
        var variantId = await InStockVariantIdAsync();
        await SetStockAsync(variantId, 10);

        var (_, _, api) = await NewBagAsync(variantId, quantity: 3);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);
        var (response, _) = await api.PlaceAsync(
            ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session)),
            Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Doc 08 §5.1: reserve at placement, deduct on Paid. OnHand must not move.
        var stock = await StockAsync(variantId);
        Assert.Equal(10, stock.OnHand);
        Assert.Equal(3, stock.Reserved);
        Assert.Equal(7, stock.Available);
    }

    /// <summary>
    /// Stock that disappears between the quote and the submission.
    /// </summary>
    /// <remarks>
    /// <b>This test's first premise was wrong, and the correction is worth recording.</b> It was written
    /// expecting the <c>OUT_OF_STOCK</c> answer of doc 08 §5.4 step 3, with a per-line shortfall. What
    /// actually happens — and what should happen — is G-08's answer: the use case re-reads the bag before
    /// it reserves anything, sees a line it cannot place, and refuses the whole checkout with
    /// <see cref="CheckoutBlocker.LineNotPurchasable"/>. The shopper is sent back to the bag to adjust it
    /// rather than being told an order failed.
    /// <para>
    /// Which means the <c>OUT_OF_STOCK</c> branch is <b>not reachable from a single sequential client at
    /// all</b>, by design: it only fires when the stock goes between the use case's own read and the
    /// reserving UPDATE, a window a few statements wide. That is a race, so it is proved by a race —
    /// <c>CheckoutConcurrencyTests.Four_simultaneous_checkouts_of_the_last_unit_place_exactly_one_order</c>
    /// over HTTP, and <c>CheckoutPlacementStoreTests</c> against the port directly for the rollback and
    /// the shortfall figures. A test here that *claimed* to cover it would have been a test of nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Stock_that_goes_between_the_quote_and_the_submission_refuses_the_whole_order()
    {
        var variantIds = await CheckoutVariantIdsAsync(2);
        await SetStockAsync(variantIds[0], 10);
        await SetStockAsync(variantIds[1], 10);

        var (_, cartId, api) = await NewBagAsync(variantIds[0], quantity: 2);
        var cart = new CartApiClient(api.Http);
        await cart.AddAsync(variantIds[1], 2, Token);

        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        var (_, session) = await api.GetAsync(colombo, standard, Token);

        // The second variant all but sells out between the quote and the submission.
        await SetStockAsync(variantIds[1], 1);

        var (response, _) = await api.PlaceAsync(
            ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session)),
            Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var problem = await CheckoutApiClient.ProblemAsync(response, Token);
        Assert.Contains(
            nameof(CheckoutBlocker.LineNotPurchasable),
            problem.RootElement.GetProperty("blockers").EnumerateArray().Select(value => value.GetString()));

        // Nothing was written, nothing was reserved — including for the line that *could* have been.
        using var scope = CreateScope();
        Assert.Equal(
            0,
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
                .CountAsync(order => order.CheckoutIdempotencyKey == session.CheckoutKey, Token));

        Assert.Equal(0, (await StockAsync(variantIds[0])).Reserved);
        Assert.Equal(0, (await StockAsync(variantIds[1])).Reserved);

        // The bag is untouched, so the shopper can adjust it.
        Assert.Equal(2, await CartItemCountAsync(cartId));
    }

    /// <summary>
    /// G-08: warn and block. A bag holding an item that cannot be bought is refused with the reason,
    /// never silently trimmed to the part that can be.
    /// </summary>
    [Fact]
    public async Task An_order_is_refused_rather_than_trimmed_when_a_line_cannot_be_bought()
    {
        var variantIds = await CheckoutVariantIdsAsync(2);
        await SetStockAsync(variantIds[0], 10);
        await SetStockAsync(variantIds[1], 10);

        var (_, cartId, api) = await NewBagAsync(variantIds[0], quantity: 1);
        var cart = new CartApiClient(api.Http);
        await cart.AddAsync(variantIds[1], 1, Token);

        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        var (_, session) = await api.GetAsync(colombo, standard, Token);

        // The second line goes out of stock entirely, which the quote would exclude from the subtotal.
        await SetStockAsync(variantIds[1], 0);

        var (_, requoted) = await api.GetAsync(colombo, standard, Token);
        Assert.Contains(CheckoutBlocker.LineNotPurchasable, requoted!.Blockers);
        Assert.Contains(requoted.Lines, line => line.Status == CartLineStatus.OutOfStock);

        var (response, _) = await api.PlaceAsync(
            ValidBody(session!.CheckoutKey, standard, colombo, requoted.Totals.GrandTotal!.Value),
            Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var problem = await CheckoutApiClient.ProblemAsync(response, Token);
        Assert.Contains(
            nameof(CheckoutBlocker.LineNotPurchasable),
            problem.RootElement.GetProperty("blockers").EnumerateArray().Select(value => value.GetString()));

        // Both lines are still in the bag: nothing was dropped on the shopper's behalf.
        Assert.Equal(2, await CartItemCountAsync(cartId));
    }

    /// <summary>
    /// G-10, verbatim: "if the placed total differs, reject with prices changed, please review". An order
    /// is never placed at a figure the customer has not seen.
    /// </summary>
    [Fact]
    public async Task An_order_whose_total_has_moved_is_refused_with_the_new_total()
    {
        var variantId = await InStockVariantIdAsync();
        var (_, cartId, api) = await NewBagAsync(variantId);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);
        var body = ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session));

        // A total one rupee away from the quote, which is what a campaign ending mid-checkout looks like.
        body["expectedGrandTotal"] = QuotedTotal(session) + 1m;

        var (response, _) = await api.PlaceAsync(body, Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var problem = await CheckoutApiClient.ProblemAsync(response, Token);
        var totals = problem.RootElement.GetProperty("totals");

        Assert.Equal(
            QuotedTotal(session),
            totals.GetProperty("grandTotal").GetDecimal());

        // Nothing placed, nothing reserved, bag intact.
        using var scope = CreateScope();
        Assert.Equal(
            0,
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
                .CountAsync(order => order.CheckoutIdempotencyKey == session.CheckoutKey, Token));
        Assert.Equal(1, await CartItemCountAsync(cartId));
    }

    [Fact]
    public async Task A_placed_order_is_readable_by_its_public_token_and_by_nothing_else()
    {
        var variantId = await InStockVariantIdAsync();
        var (_, _, api) = await NewBagAsync(variantId);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);
        var (_, order) = await api.PlaceAsync(
            ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session)),
            Token);

        var (found, confirmation) = await api.GetOrderAsync(order!.PublicId.ToString("D"), Token);

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal(order.OrderNumber, confirmation!.OrderNumber);
        Assert.Equal(order.GrandTotal, confirmation.GrandTotal);
        Assert.False(confirmation.IsDuplicateSubmission);

        // The confirmation is cacheable by nothing: the URL is a bearer token and the body holds an
        // address.
        var cacheControl = Assert.Single(found.Headers.GetValues("Cache-Control"));
        Assert.Contains("no-store", cacheControl, StringComparison.Ordinal);

        // Another token, and an empty one: a flat 404 either way. "No such order" and "not yours" must be
        // the same answer, or guessing confirms the order exists.
        var (missing, _) = await api.GetOrderAsync(Guid.NewGuid().ToString("D"), Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var (empty, _) = await api.GetOrderAsync(Guid.Empty.ToString("D"), Token);
        Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
    }

    /// <summary>
    /// The order's confirmation is read back from the row, not assembled from the request — so the lines,
    /// the delivery snapshot and the money on the success page are what the database holds.
    /// </summary>
    [Fact]
    public async Task The_confirmation_is_the_order_row_and_not_a_copy_of_the_request()
    {
        var variantId = await InStockVariantIdAsync();
        var (_, _, api) = await NewBagAsync(variantId, quantity: 2);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);
        var (_, order) = await api.PlaceAsync(
            ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session)),
            Token);

        var row = await OrderByNumberAsync(order!.OrderNumber);
        var line = Assert.Single(order.Lines);
        var rowLine = Assert.Single(row.Lines);

        Assert.Equal(rowLine.ProductName, line.ProductName);
        Assert.Equal(rowLine.ProductCode, line.ProductCode);
        Assert.Equal(rowLine.Sku, line.Sku);
        Assert.Equal(rowLine.Quantity, line.Quantity);
        Assert.Equal(rowLine.LineSubtotal, line.LineSubtotal);

        Assert.Equal(DeliveryMethod.StandardCode, order.Delivery.MethodCode);
        Assert.Equal("Colombo", order.Delivery.DistrictName);
        Assert.Equal("14 Galle Road", order.Delivery.Line1);
        Assert.Equal((byte)1, order.Delivery.EstimatedDaysMin);
        Assert.Equal((byte)2, order.Delivery.EstimatedDaysMax);

        // Savings is markdown + campaign, derived from the row's own two columns.
        Assert.Equal(row.MarkdownDiscountTotal + row.CampaignDiscountTotal, order.SavedTotal);
        Assert.Equal(row.ItemsOriginalTotal - row.ItemsSubtotal, order.SavedTotal);
    }

    /// <summary>
    /// Doc 08 §5.4: "<c>UX_Orders_CheckoutIdempotencyKey</c> turns a double submit into a no-op". Sent one
    /// after the other — the simultaneous case is in <c>CheckoutConcurrencyTests</c>.
    /// </summary>
    [Fact]
    public async Task Re_sending_the_same_checkout_key_returns_the_first_order_and_creates_nothing()
    {
        var variantId = await InStockVariantIdAsync();
        await SetStockAsync(variantId, 10);

        var (_, _, api) = await NewBagAsync(variantId, quantity: 2);
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        var (_, session) = await api.GetAsync(colombo, standard, Token);
        var body = ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session));

        var (first, firstOrder) = await api.PlaceAsync(body, Token);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var (second, secondOrder) = await api.PlaceAsync(body, Token);

        // 200, not 201: this request created nothing.
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstOrder!.OrderNumber, secondOrder!.OrderNumber);
        Assert.Equal(firstOrder.PublicId, secondOrder.PublicId);
        Assert.True(secondOrder.IsDuplicateSubmission);
        Assert.False(firstOrder.IsDuplicateSubmission);

        using var scope = CreateScope();
        Assert.Equal(
            1,
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
                .CountAsync(order => order.CheckoutIdempotencyKey == session.CheckoutKey, Token));

        // **One reservation, not two.** The replay must not have claimed the stock again.
        Assert.Equal(2, (await StockAsync(variantId)).Reserved);
    }

    /// <summary>
    /// Doc 08 §3.5: "order numbers are allocated in a short separate transaction … gaps are allowed".
    /// Successive orders get successive numbers from the Colombo year's series.
    /// </summary>
    [Fact]
    public async Task Successive_orders_get_successive_numbers_from_the_year_s_series()
    {
        var variantId = await InStockVariantIdAsync();
        await SetStockAsync(variantId, 20);

        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        var numbers = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            var (_, _, api) = await NewBagAsync(variantId);
            var (_, session) = await api.GetAsync(colombo, standard, Token);
            var (response, order) = await api.PlaceAsync(
                ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session)),
                Token);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            numbers.Add(order!.OrderNumber);
        }

        Assert.All(numbers, number => Assert.Matches(OrderNumberShape, number));
        Assert.Equal(3, numbers.Distinct(StringComparer.Ordinal).Count());

        var sequences = numbers.Select(number => int.Parse(
            number.Split('-')[2],
            System.Globalization.CultureInfo.InvariantCulture)).ToList();

        Assert.Equal(sequences.OrderBy(value => value), sequences);
        Assert.Equal(sequences[0] + 1, sequences[1]);
        Assert.Equal(sequences[1] + 1, sequences[2]);

        // The series row exists and has been advanced past the last number handed out.
        using var scope = CreateScope();
        // Computed before the query, not inside the predicate: EF cannot translate a call to a Domain
        // method and threw "could not be translated" when it was written the other way round.
        var seriesKey = DocumentNumber.OrderSeriesKeyFor(DateTimeOffset.UtcNow);

        var series = await scope.ServiceProvider.GetRequiredService<AppDbContext>().NumberSeries
            .AsNoTracking()
            .SingleAsync(row => row.SeriesKey == seriesKey, Token);

        Assert.Equal(DocumentNumber.OrderNumberPrefix, series.Prefix);
        Assert.True(series.NextValue > sequences[^1]);
    }

    private async Task<int> InStockVariantIdAsync() => await CheckoutVariantIdAsync();

    /// <summary>How many carts exist. Compared before and after, never against a time window.</summary>
    private async Task<int> CartCountAsync()
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Carts.CountAsync(Token);
    }
}
