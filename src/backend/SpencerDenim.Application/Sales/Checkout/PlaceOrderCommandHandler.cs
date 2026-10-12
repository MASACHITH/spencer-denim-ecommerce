using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Cart.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Checkout;

/// <summary>
/// Place order (CHK-01..04): doc 11 P6 steps 8 to 10, and doc 08 §5.4.
/// </summary>
/// <remarks>
/// <b>The order of the steps is the design, and each one is here because skipping it has a name.</b>
/// <list type="number">
/// <item><description>
/// <b>Replay first.</b> A submission whose idempotency key already placed an order returns that order and
/// stops. It has to come first: a successful placement empties the bag, so a later replay would otherwise
/// be told its bag was empty — the right answer to the wrong question.
/// </description></item>
/// <item><description>
/// <b>And replay last, on every refusal.</b> The pre-check above closes the <i>sequential</i> resubmit
/// only, and it cannot close the simultaneous one: between it and the repricing read this handler makes
/// about five more round trips, and a winner's transaction can reserve the stock, write the order and
/// delete <c>sales.CartItems</c> inside that window. The loser then reads nothing in the bag and used to
/// answer 409 "your bag is empty" to a shopper whose order had just been placed — observed in CI, and
/// reproducible over HTTP in roughly a quarter of rounds at 24 contenders. So every refusal that depends
/// on state the winner can have changed goes through
/// <see cref="RefuseUnlessAlreadyPlacedAsync"/>, which asks the index one more question before answering.
/// </description></item>
/// <item><description>
/// <b>Resolve the delivery method, then validate.</b> <c>RequiresAddress</c> decides which half of the
/// request is required, so it cannot be checked before it is known.
/// </description></item>
/// <item><description>
/// <b>Price the bag once.</b> This read is where the price is frozen. Everything written afterwards comes
/// from it, and nothing re-reads the catalogue.
/// </description></item>
/// <item><description>
/// <b>Compare with the shopper's total (G-10)</b> before allocating a number or opening a transaction. An
/// order is never placed at a figure the customer has not seen.
/// </description></item>
/// <item><description>
/// <b>Allocate the order number in its own short transaction</b> (doc 08 §3.5), so concurrent checkouts do
/// not queue on the series row. Gaps are allowed and expected.
/// </description></item>
/// <item><description>
/// <b>One transaction</b> reserves the stock, writes the order and empties the bag
/// (<see cref="IOrderPlacementStore"/>).
/// </description></item>
/// </list>
/// <para>
/// <b>Where the money begins, and why there is none of it in this file (PAY-03).</b> This handler knows
/// that a payment provider exists, that it has a code and that it answers two questions. It does not know
/// what a bank transfer <i>is</i>: not the 48 hours, not the <c>Pending</c> status, not the account
/// details, not the reference. All of that lives behind <see cref="IPaymentProvider"/>, and
/// <c>CheckoutNamesNoProviderTests</c> is what keeps it there — a convention test asserting that no
/// identifier in the checkout namespace names a specific gateway. The whole claim of PAY-03 is that
/// adding a gateway does not edit this file, and a claim like that is worth exactly as much as the test
/// under it.
/// </para>
/// </remarks>
public sealed class PlaceOrderCommandHandler(
    ICartStore carts,
    ICheckoutReader reader,
    INumberSeriesAllocator numbers,
    IOrderPlacementStore placements,
    IClock clock,
    IPaymentProviderRegistry paymentProviders)
    : ICommandHandler<PlaceOrderCommand, PlaceOrderResult>
{

    public async Task<PlaceOrderResult> HandleAsync(
        PlaceOrderCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // 1. A double submit that has already succeeded. Before anything else: see the remarks.
        if (command.CheckoutKey != Guid.Empty
            && await reader.FindOrderByCheckoutKeyAsync(command.CheckoutKey, cancellationToken)
                is { } alreadyPlaced)
        {
            return PlaceOrderResult.AlreadyPlaced(alreadyPlaced);
        }

        // 2. The delivery method, which decides what the rest of the request must contain.
        var method = command.DeliveryMethodId is { } methodId and > 0
            ? await reader.FindEnabledDeliveryMethodAsync(methodId, cancellationToken)
            : null;

        var errors = PlaceOrderValidator.Validate(command, method?.RequiresAddress);

        if (method is null && command.DeliveryMethodId is > 0)
        {
            // Asked for a real-looking id that is not an enabled method. Doc 11 P6's exception, in its own
            // words rather than a generic "invalid": the shopper chose something that has gone.
            errors =
            [
                ..errors,
                new ValidationError(
                    "deliveryMethodId",
                    "This delivery option is no longer available. Choose another."),
            ];
        }

        if (errors.Count > 0)
        {
            return PlaceOrderResult.Invalid(errors);
        }

        // Non-null from here: the validator reports a missing method and we returned above.
        var deliveryMethod = method!;

        var choice = await ResolvePaymentProviderAsync(command.PaymentMethodCode!, cancellationToken);
        if (choice.Error is { } paymentError)
        {
            return PlaceOrderResult.Invalid([paymentError]);
        }

        // Non-null from here: the branch above returns whenever the provider could not be resolved.
        var paymentProvider = choice.Provider!;

        // 3. Where the order is going, and therefore which zone prices it (doc 08 §3.5 R7).
        var destination = await ResolveDestinationAsync(command, deliveryMethod, cancellationToken);
        if (destination.Error is { } destinationError)
        {
            return PlaceOrderResult.Invalid([destinationError]);
        }

        var district = destination.District!.Value;

        var rate = await reader.FindZoneRateAsync(deliveryMethod.Id, district.DeliveryZone, cancellationToken);
        if (rate is not { IsAvailable: true } zoneRate)
        {
            // No row, or a row that says this method is not offered in this zone. Doc 08 §6 step 10 has no
            // fee to give, so the order is refused rather than priced with an invented one.
            return PlaceOrderResult.Invalid(
            [
                new ValidationError(
                    "deliveryMethodId",
                    "This delivery option is not available for that district. Choose another."),
            ]);
        }

        var cart = await carts.FindAsync(command.Lookup, cancellationToken);
        if (cart is not { } identity)
        {
            return await RefuseUnlessAlreadyPlacedAsync(
                command,
                PlaceOrderResult.Blocked([CheckoutBlocker.EmptyBag]),
                cancellationToken);
        }

        // 4. The repricing read. **This is where the price is frozen.**
        var lines = await reader.ReadCartAsync(identity.Id, cancellationToken);

        if (lines.Count == 0)
        {
            return await RefuseUnlessAlreadyPlacedAsync(
                command,
                PlaceOrderResult.Blocked([CheckoutBlocker.EmptyBag]),
                cancellationToken);
        }

        if (!lines.All(CheckoutQuote.IsPlaceable))
        {
            // G-08: warn and block. Nothing is dropped and no quantity is reduced on the shopper's behalf —
            // an order silently missing an item is the defect this rule exists to prevent.
            return await RefuseUnlessAlreadyPlacedAsync(
                command,
                PlaceOrderResult.Blocked([CheckoutBlocker.LineNotPurchasable]),
                cancellationToken);
        }

        // 4a. ADM-13, doc 11 P3 step 12: "re-validates the code on every bag read **and again at
        // placement**". The same resolver the GET uses, so the two cannot reach different verdicts — if
        // they could, the G-10 check below would refuse the order and nothing on screen would say why.
        //
        // A code that has stopped qualifying since the quote is refused here rather than dropped: the
        // shopper agreed to a total that included it, and placing the order without it would charge more
        // than they agreed. The per-customer limit is *not* decided here — it is counted inside the
        // transaction under an app lock, where the race lives.
        var promo = await PromoResolver.ResolveAsync(
            command.PromoCode,
            lines,
            reader,
            clock.UtcNow.UtcDateTime,
            cancellationToken);

        if (promo.Refusal is { } promoRefusal)
        {
            return PlaceOrderResult.PromoRejected(promoRefusal, promo.MinimumSpend);
        }

        var pricing = CheckoutQuote.Price(lines, zoneRate, promo.Charge);

        // 5. G-10. The figure the shopper was looking at, or nothing is placed.
        if (pricing.GrandTotal != command.ExpectedGrandTotal)
        {
            return PlaceOrderResult.TotalChanged(CheckoutQuote.Totals(lines, zoneRate, promo.Charge)!);
        }

        var placedAt = clock.UtcNow;

        // 6. A short transaction of its own (doc 08 §3.5). Gaps are allowed: if the placement below fails,
        // this number is simply never used.
        var orderNumber = await numbers.AllocateOrderNumberAsync(placedAt, cancellationToken);

        // The provider's own answer, asked before the transaction opens: doc 08 §5.4 keeps everything that
        // is not a write out of it, and a provider that has to read its settings (PayPal's exchange rate)
        // must not do so while this request holds StockLevels row locks.
        var initiation = await paymentProvider.InitiateAsync(
            new PaymentInitiationRequest(pricing.GrandTotal, placedAt.UtcDateTime),
            cancellationToken);

        if (initiation.PaymentDueAtUtc <= placedAt.UtcDateTime)
        {
            // A provider that answered with a deadline already past would have its orders expired by the
            // job before the customer could pay, and the order row would be written with a lie on it.
            // Loud here rather than quietly unpayable: this is a bug in a provider, not a shopper's input.
            throw new InvalidOperationException(
                $"Payment provider '{paymentProvider.Code}' returned a payment due time of "
                + $"{initiation.PaymentDueAtUtc:O}, which is not after the placement time "
                + $"{placedAt.UtcDateTime:O}.");
        }

        // What the customer is told to do next, asked of the provider **before** the transaction opens,
        // for the same reason InitiateAsync is: doc 08 §5.4 keeps every read out of the transaction that
        // holds StockLevels row locks, and this one reads the shop's bank details out of ops.Settings.
        //
        // It travels on to the placement because the transaction queues the emails (doc 11 P6 step 9), and
        // one of them is the instructions. Null is a real answer — for a bank transfer it means the shop's
        // bank details are not set — and the store queues no instructions email in that case.
        //
        // `OrderUrl: null` here, as on the order page: the link in the email is built inside the store,
        // which is where the order's PublicId is generated, and `PaymentInstructions` carries no URL.
        var instructions = await paymentProvider.DescribeAsync(
            new PaymentInstructionsRequest(
                orderNumber,
                pricing.GrandTotal,
                initiation.PaymentDueAtUtc,
                OrderUrl: null),
            cancellationToken);

        var placement = BuildPlacement(
            command,
            identity.Id,
            orderNumber,
            deliveryMethod,
            destination,
            zoneRate,
            lines,
            pricing,
            placedAt,
            initiation,
            choice.DisplayName!,
            instructions,
            promo);

        // 7. One transaction: reserve, insert, empty the bag.
        var result = await placements.PlaceAsync(placement, cancellationToken);

        switch (result.Status)
        {
            case OrderPlacementStatus.Placed:
                var confirmation = await reader.FindOrderAsync(result.PublicId!.Value, cancellationToken);

                // Read back rather than assembled from the request, so the success page shows what the
                // database holds. An assertion that reads back a declaration tests nothing; a confirmation
                // page built from the request would be the same mistake in production.
                return confirmation is null
                    ? throw new InvalidOperationException(
                        $"Order {orderNumber} was placed but could not be read back by its public id.")
                    : PlaceOrderResult.Placed(confirmation);

            case OrderPlacementStatus.OutOfStock:
                // Re-checked for the same reason as the blockers above, and this one the brief did not
                // name: `OrderPlacementStore` reserves *before* it inserts, so a loser whose bag holds
                // exactly the available stock is refused by the reserving UPDATE and never reaches
                // `UX_Orders_CheckoutIdempotencyKey` at all. Without this, that loser is told "sold out"
                // about an order it has already placed.
                return await RefuseUnlessAlreadyPlacedAsync(
                    command,
                    PlaceOrderResult.OutOfStock(result.ShortLines),
                    cancellationToken);

            case OrderPlacementStatus.PromoRejected:
                // The conditional UPDATE matched no row, or the per-customer count reached the limit
                // under the app lock. Nothing was written by *this* request — the transaction rolled back
                // and took the stock reservation with it — but that says nothing about the key.
                //
                // **Re-checked for the same reason as OutOfStock above, and this one is not a race to
                // lose.** Two submissions of one checkout key pass the step-1 pre-check truthfully, both
                // reach `OrderPromoUse.TakeAsync`, and the loser waits on the winner's `sp_getapplock`
                // and then counts the winner's own order as a held use for its own email. So a
                // per-customer limit of one refuses a double-click *deterministically*, before
                // `UX_Orders_CheckoutIdempotencyKey` is ever consulted — and the refusal says "nothing
                // has been charged" about an order that exists. `CheckoutPromoRaceTests` measures it.
                return await RefuseUnlessAlreadyPlacedAsync(
                    command,
                    PlaceOrderResult.PromoRejected(result.PromoRefusal!.Value),
                    cancellationToken);

            case OrderPlacementStatus.DuplicateSubmission:
                // Two submissions genuinely in flight at once: the unique index on
                // CheckoutIdempotencyKey rejected the second insert, so this request reserved nothing.
                var winner = await reader.FindOrderByCheckoutKeyAsync(command.CheckoutKey, cancellationToken);

                return winner is null
                    ? throw new InvalidOperationException(
                        "A duplicate checkout key was rejected by the database but no order holds it.")
                    : PlaceOrderResult.AlreadyPlaced(winner);

            default:
                throw new InvalidOperationException($"Unhandled placement status {result.Status}.");
        }
    }

    /// <summary>
    /// The refusal, unless this checkout key has in the meantime produced an order — in which case the
    /// order is the answer.
    /// </summary>
    /// <remarks>
    /// <b>This is the second half of the idempotency guarantee, and the first half cannot do it.</b> Step
    /// 1's pre-check answers a resubmit that arrives <i>after</i> the first one finished. A resubmit that
    /// arrives <i>during</i> it passes that check truthfully — no order existed yet — and then runs into
    /// the winner's committed work: an emptied bag, a line the reservation made unbuyable, a reserving
    /// UPDATE that matches no row. Each of those is a correct description of the database and the wrong
    /// answer to the question asked, which was "place my order", and the order is placed.
    /// <para>
    /// <b>It costs one query, and only on a path that is already refusing.</b> The happy path is
    /// untouched; so is every refusal decided from the command alone (<see cref="PlaceOrderValidator"/>,
    /// the payment method, the district, the zone rate), because those answers do not change when another
    /// request commits — two submissions of one key carry the same fields and would be refused alike.
    /// </para>
    /// <para>
    /// <b>Why this is safe rather than a way to swallow a real refusal.</b> The only thing that turns the
    /// refusal into a replay is an order row holding <i>this</i> key, which only a committed placement of
    /// this very submission can create — <c>UX_Orders_CheckoutIdempotencyKey</c> makes the key unique, and
    /// the server is the only party that issues one (<c>CheckoutSessionDto.CheckoutKey</c>). With no such
    /// row the refusal is returned exactly as it was built.
    /// </para>
    /// </remarks>
    private async Task<PlaceOrderResult> RefuseUnlessAlreadyPlacedAsync(
        PlaceOrderCommand command,
        PlaceOrderResult refusal,
        CancellationToken cancellationToken)
    {
        // Non-empty from here: the validator reports an empty key and the caller returned long ago.
        var winner = await reader.FindOrderByCheckoutKeyAsync(command.CheckoutKey, cancellationToken);

        return winner is null ? refusal : PlaceOrderResult.AlreadyPlaced(winner);
    }

    /// <summary>
    /// The chosen payment provider must be one the shop has switched on (doc 11 P6 precondition 2).
    /// </summary>
    /// <remarks>
    /// <b>Two different questions, asked in this order (PAY-03).</b>
    /// <list type="number">
    /// <item><description>
    /// <b>Is the provider switched on?</b> <c>sales.PaymentProviders.IsEnabled</c>, which doc 08 §3.5
    /// makes the shop's own switch and which checkout already reads. A provider the shop has turned off is
    /// "not available", because that is a decision somebody took.
    /// </description></item>
    /// <item><description>
    /// <b>Does this build have code for it?</b> <see cref="IPaymentProviderRegistry"/>. A provider enabled
    /// in the database with no implementation behind it must refuse the checkout rather than write an
    /// order nobody can pay for. Doc 08 §9 seeds the PayPal row <b>disabled</b>, so question 1 already
    /// stops it — this is the belt for somebody enabling it in SQL before W4-01 lands, and the two
    /// messages differ so an admin can tell which of the two refused them.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>What this method no longer does</b> is compare the code against a literal. It used to read
    /// <c>code == PaymentProviderCodes.BankTransfer</c>, with a note saying <c>IPaymentProvider</c> would
    /// replace it.
    /// </para>
    /// </remarks>
    private async Task<PaymentChoice> ResolvePaymentProviderAsync(
        string code,
        CancellationToken cancellationToken)
    {
        var enabled = await reader.EnabledPaymentMethodsAsync(cancellationToken);
        var trimmed = code.Trim();

        // Ordinal against the enabled list, as it always was: this compares a value from the request with
        // values from the same column, and the shopper's radio carries the code the session handed it. The
        // registry's own lookup is case-insensitive instead, because that one compares a database value
        // against a compile-time constant.
        var match = enabled.FirstOrDefault(
            method => string.Equals(method.Code, trimmed, StringComparison.Ordinal));

        if (match is null)
        {
            return new PaymentChoice(
                null,
                null,
                new ValidationError(
                    "paymentMethodCode",
                    "That payment method is not available. Choose another."));
        }

        return paymentProviders.Find(trimmed) is { } provider
            ? new PaymentChoice(provider, match.DisplayName, null)
            : new PaymentChoice(
                null,
                null,
                new ValidationError(
                    "paymentMethodCode",
                    "That payment method cannot be used yet. Choose another."));
    }

    /// <summary>The chosen provider and its display name, or why it cannot be used.</summary>
    /// <param name="DisplayName">
    /// <c>sales.PaymentProviders.DisplayName</c> for the chosen code, taken from the list this method has
    /// already read rather than re-read later. Non-null whenever <paramref name="Provider"/> is: the
    /// match below is what produces it. It is on the order's emails, so a customer reads "Bank / online
    /// transfer" and not a code — and it comes from the row the shop can edit rather than from a literal
    /// in this file, which is also what keeps this type's name clear of PAY-03's rule.
    /// </param>
    private readonly record struct PaymentChoice(
        IPaymentProvider? Provider,
        string? DisplayName,
        ValidationError? Error);

    /// <summary>
    /// Doc 08 §3.5 R7: a delivery order's district is the address's; a pickup order's is the pickup
    /// location's. The zone follows from whichever it is, and both are snapshotted on to the order.
    /// </summary>
    private async Task<Destination> ResolveDestinationAsync(
        PlaceOrderCommand command,
        CheckoutDeliveryMethod method,
        CancellationToken cancellationToken)
    {
        if (!method.RequiresAddress)
        {
            var location = await reader.FindActivePickupLocationAsync(
                command.PickupLocationId!.Value,
                cancellationToken);

            return location is null
                ? new Destination(
                    null,
                    null,
                    new ValidationError(
                        "pickupLocationId",
                        "That store is no longer available for collection. Choose another."))
                : new Destination(
                    new CheckoutDistrict(location.DistrictId, location.DistrictName, location.DeliveryZone),
                    location,
                    null);
        }

        var district = await reader.FindActiveDistrictAsync(command.DistrictId!.Value, cancellationToken);

        return district is null
            ? new Destination(
                null,
                null,
                new ValidationError("districtId", "Choose the district your order is going to."))
            : new Destination(district, null, null);
    }

    private static OrderPlacement BuildPlacement(
        PlaceOrderCommand command,
        int cartId,
        string orderNumber,
        CheckoutDeliveryMethod method,
        Destination destination,
        CheckoutZoneRate rate,
        IReadOnlyList<CheckoutCartLine> lines,
        OrderPricingResult pricing,
        DateTimeOffset placedAt,
        PaymentInitiation payment,
        string paymentMethodName,
        PaymentInstructions? instructions,
        ResolvedPromo promo)
    {
        var district = destination.District!.Value;
        var isPickup = !method.RequiresAddress;
        var address = command.Address;

        var ship = isPickup
            // Every Ship_* column null. CK_Orders_ShipAddress allows exactly that for a PICKUP order, and
            // writing the shopper's own address into a parcel that is not being shipped would be a record
            // of something that did not happen.
            ? new AddressSnapshot()
            : new AddressSnapshot
            {
                RecipientName = PlaceOrderValidator.Trimmed(address!.RecipientName),
                Phone = PlaceOrderValidator.Trimmed(address.Phone),
                Line1 = PlaceOrderValidator.Trimmed(address.Line1),
                Line2 = PlaceOrderValidator.Trimmed(address.Line2),
                City = PlaceOrderValidator.Trimmed(address.City),
                PostalCode = PlaceOrderValidator.Trimmed(address.PostalCode),
                Landmark = PlaceOrderValidator.Trimmed(address.Landmark),
            };

        var delivery = new DeliverySnapshot
        {
            DeliveryMethodCode = method.Code,
            DeliveryMethodName = method.Name,
            PickupLocationName = destination.PickupLocation?.Name,

            // The estimate is snapshotted with the rest, so a later change to the zone rate does not
            // rewrite what a shopper was promised.
            EstimatedDaysMin = rate.EstimatedDaysMin,
            EstimatedDaysMax = rate.EstimatedDaysMax,
        };

        // The priced lines come back in the order they went in, so zipping them is safe — and asserted,
        // because a silent misalignment here would attach one product's name to another's price.
        if (pricing.Lines.Count != lines.Count)
        {
            throw new InvalidOperationException(
                $"Priced {pricing.Lines.Count} lines for a bag of {lines.Count}.");
        }

        var snapshots = new List<OrderLineSnapshot>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var priced = pricing.Lines[index];

            if (priced.VariantId != line.VariantId)
            {
                throw new InvalidOperationException(
                    $"Line {index} priced variant {priced.VariantId} against bag variant {line.VariantId}.");
            }

            snapshots.Add(new OrderLineSnapshot(
                // 1-based, in the order the bag read them (oldest first), so the order reads like the bag.
                (short)(index + 1),
                line.ProductId,
                line.VariantId,
                line.DepartmentId,
                line.CategoryId,
                line.ProductName,
                line.ProductCode,
                line.Sku,
                line.ColourName,
                line.SizeLabel,
                line.ThumbnailStorageKey,
                priced));
        }

        var email = PlaceOrderValidator.Trimmed(command.Email)!;

        return new OrderPlacement(
            cartId,
            orderNumber,
            command.CheckoutKey,
            command.CustomerUserId,
            command.PaymentMethodCode!.Trim(),
            email,
            PlaceOrderValidator.Trimmed(command.Phone),
            PlaceOrderValidator.Trimmed(command.FirstName)!,
            PlaceOrderValidator.Trimmed(command.LastName),
            district.Id,
            district.Name,
            district.DeliveryZone,
            ship,
            method.Id,
            destination.PickupLocation?.Id,
            delivery,
            pricing,
            snapshots.AsReadOnly(),
            PlaceOrderValidator.Trimmed(command.CustomerNote),
            PlaceOrderValidator.Trimmed(command.PlacedIpAddress),
            placedAt.UtcDateTime,

            // The provider's answer, unexamined. Doc 11 P6's "48 hours" is a bank-transfer fact and lives
            // in BankTransferProvider; PayPal's window is minutes and lives in its own. Checkout's only
            // interest in the deadline is the one guard above — that it is in the future.
            payment,

            // Likewise unexamined: the display name the shop set on the provider row, and whatever the
            // provider had to say about paying. Checkout does not read either one; the placement
            // transaction puts them into the emails it queues (doc 11 P6 step 9).
            paymentMethodName,
            instructions,

            // ADM-13. Only the id and the stored code: the two limits are applied inside the transaction
            // against the row as it is then, not against the snapshot this resolution read (doc 08 §3.6).
            promo.Terms is { } terms ? new PromoTake(terms.Id, terms.Code) : null);
    }

    /// <summary>Where the order is going, or why it cannot be worked out.</summary>
    private readonly record struct Destination(
        CheckoutDistrict? District,
        CheckoutPickupLocation? PickupLocation,
        ValidationError? Error);
}
