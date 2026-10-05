using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Cart.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Contracts;
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
/// <b>Where this task stops.</b> It places an order and shows its number. The <c>Payments</c> row it
/// writes is the one doc 08 §4.1 lists as a side effect of "— → PendingPayment", because doc 11 P7's
/// preconditions require an open bank payment to exist on a PendingPayment order — but <b>nothing about
/// the money</b> is here: no bank details, no reference to quote, no slip upload, no approval, no expiry
/// job, no emails. Those are PAY-01/PAY-03, W3-03's steps 2 and 3.
/// </para>
/// </remarks>
public sealed class PlaceOrderCommandHandler(
    ICartStore carts,
    ICheckoutReader reader,
    INumberSeriesAllocator numbers,
    IOrderPlacementStore placements,
    IClock clock,
    CheckoutOptions options)
    : ICommandHandler<PlaceOrderCommand, PlaceOrderResult>
{
    // The bound value, not IOptions<T>: this project deliberately takes no options package, so
    // Infrastructure registers the settings object itself (the same convention CatalogOptions follows).
    private readonly CheckoutOptions _options = options ?? throw new ArgumentNullException(nameof(options));

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

        var paymentError = await ValidatePaymentMethodAsync(command.PaymentMethodCode!, cancellationToken);
        if (paymentError is not null)
        {
            return PlaceOrderResult.Invalid([paymentError]);
        }

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
            return PlaceOrderResult.Blocked([CheckoutBlocker.EmptyBag]);
        }

        // 4. The repricing read. **This is where the price is frozen.**
        var lines = await reader.ReadCartAsync(identity.Id, cancellationToken);

        if (lines.Count == 0)
        {
            return PlaceOrderResult.Blocked([CheckoutBlocker.EmptyBag]);
        }

        if (!lines.All(CheckoutQuote.IsPlaceable))
        {
            // G-08: warn and block. Nothing is dropped and no quantity is reduced on the shopper's behalf —
            // an order silently missing an item is the defect this rule exists to prevent.
            return PlaceOrderResult.Blocked([CheckoutBlocker.LineNotPurchasable]);
        }

        var pricing = CheckoutQuote.Price(lines, zoneRate);

        // 5. G-10. The figure the shopper was looking at, or nothing is placed.
        if (pricing.GrandTotal != command.ExpectedGrandTotal)
        {
            return PlaceOrderResult.TotalChanged(CheckoutQuote.Totals(lines, zoneRate)!);
        }

        var placedAt = clock.UtcNow;

        // 6. A short transaction of its own (doc 08 §3.5). Gaps are allowed: if the placement below fails,
        // this number is simply never used.
        var orderNumber = await numbers.AllocateOrderNumberAsync(placedAt, cancellationToken);

        var placement = BuildPlacement(
            command,
            identity.Id,
            orderNumber,
            deliveryMethod,
            destination,
            zoneRate,
            lines,
            pricing,
            placedAt);

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
                return PlaceOrderResult.OutOfStock(result.ShortLines);

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
    /// The chosen payment provider must be one the shop has switched on (doc 11 P6 precondition 2).
    /// </summary>
    /// <remarks>
    /// <b>And it must be bank transfer, which is a boundary and not a design.</b> PAY-03's
    /// <c>IPaymentProvider</c> is W3-03's step 2; until it exists, the only initial payment state this
    /// task knows how to write is doc 08 §4.1's bank-transfer one (<c>Pending</c>, due in 48 h). PayPal
    /// needs an <c>Initiated</c> row, a USD conversion and a redirect, all of which are W4-01. The row is
    /// seeded <b>disabled</b> (doc 08 §9) so this branch is unreachable through the UI — the check is here
    /// so that enabling PayPal in the database before its code exists refuses the checkout instead of
    /// writing an order nobody can pay for.
    /// </remarks>
    private async Task<ValidationError?> ValidatePaymentMethodAsync(
        string code,
        CancellationToken cancellationToken)
    {
        var enabled = await reader.EnabledPaymentMethodsAsync(cancellationToken);
        var trimmed = code.Trim();

        if (!enabled.Any(method => string.Equals(method.Code, trimmed, StringComparison.Ordinal)))
        {
            return new ValidationError(
                "paymentMethodCode",
                "That payment method is not available. Choose another.");
        }

        return string.Equals(trimmed, PaymentProviderCodes.BankTransfer, StringComparison.Ordinal)
            ? null
            : new ValidationError(
                "paymentMethodCode",
                "That payment method cannot be used yet. Choose bank or online transfer.");
    }

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

    private OrderPlacement BuildPlacement(
        PlaceOrderCommand command,
        int cartId,
        string orderNumber,
        CheckoutDeliveryMethod method,
        Destination destination,
        CheckoutZoneRate rate,
        IReadOnlyList<CheckoutCartLine> lines,
        OrderPricingResult pricing,
        DateTimeOffset placedAt)
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

            // Doc 11 P6: placed time + 48 h for a bank transfer. The window is configuration until
            // ops.Settings exists (see CheckoutOptions), and nothing in this task enforces it: the
            // ExpireUnpaidOrders job is W3-03's step 3.
            placedAt.UtcDateTime.AddHours(_options.BankTransferExpiryHours));
    }

    /// <summary>Where the order is going, or why it cannot be worked out.</summary>
    private readonly record struct Destination(
        CheckoutDistrict? District,
        CheckoutPickupLocation? PickupLocation,
        ValidationError? Error);
}
