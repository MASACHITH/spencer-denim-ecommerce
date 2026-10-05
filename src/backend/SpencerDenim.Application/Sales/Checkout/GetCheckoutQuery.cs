using System.Collections.ObjectModel;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Sales.Cart;
using SpencerDenim.Application.Sales.Cart.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Checkout;

/// <summary>
/// Read the checkout page for one visitor (CHK-01..03).
/// </summary>
/// <param name="DistrictId">
/// The district to price delivery for, or <see langword="null"/> before one is chosen. <b>Nothing is
/// defaulted</b>: doc 08 §3.5 R7's two fallbacks are <c>Users.DefaultDistrictId</c> (no customer sign-in
/// exists, ACC-01) and the <c>Delivery.DefaultZone</c> setting (no <c>ops.Settings</c> table exists,
/// ADM-22), so a default here would be a zone this task chose, and a fee printed from a zone nobody chose
/// is the "made-up delivery charge" the brief rules out.
/// </param>
/// <param name="DeliveryMethodId">
/// The method to price, or <see langword="null"/>. An unknown or unavailable id is <b>not</b> an error on a
/// read: the page comes back with it unselected, which is what doc 11 P6 asks for when an option is
/// withdrawn after it was chosen ("this delivery option is no longer available, choose another").
/// </param>
public sealed record GetCheckoutQuery(CartLookup Lookup, int? DistrictId, int? DeliveryMethodId);

/// <summary>
/// What the GET answered: the session, plus whatever the guest cookie must now say.
/// <para>
/// The cookie travels for the same reason it does on a cart read: the decision follows from which cart was
/// resolved, and only the use case knows that. Checkout <b>never creates a cart</b> — a visitor with no bag
/// is told their bag is empty, not given one.
/// </para>
/// </summary>
public sealed record CheckoutView(CheckoutSessionDto Session, CartCookieAction Cookie, Guid? GuestToken);

/// <summary>
/// Builds the checkout page: the bag repriced, the districts, the delivery options priced for the chosen
/// district, the pickup locations, the enabled payment providers, and a fresh idempotency key.
/// </summary>
/// <remarks>
/// <b>Every checkout is a guest checkout today (CHK-03), and that is a fact about the repository rather
/// than a choice.</b> There is no customer authentication anywhere in it — ACC-01 is W3-01 and unbuilt — so
/// <c>CartLookup</c> can only ever arrive here with a guest token. The customer branch is not written
/// speculatively: <see cref="CheckoutSessionDto.SelectedDistrictId"/> says what a signed-in shopper would
/// get instead, and the placement command's <c>CustomerUserId</c> is the one field W3-01 fills in.
/// </remarks>
public sealed class GetCheckoutQueryHandler(ICartStore carts, ICheckoutReader reader)
    : IQueryHandler<GetCheckoutQuery, CheckoutView>
{
    public async Task<CheckoutView> HandleAsync(GetCheckoutQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var lookup = query.Lookup;
        var cart = await carts.FindAsync(lookup, cancellationToken);

        // Three things that need the reference data whether or not there is a bag, because an empty
        // checkout page still has to render its form.
        var districts = await reader.ActiveDistrictsAsync(cancellationToken);
        var pickupLocations = await reader.ActivePickupLocationsAsync(cancellationToken);
        var paymentMethods = await reader.EnabledPaymentMethodsAsync(cancellationToken);

        var lines = cart is { } found
            ? await reader.ReadCartAsync(found.Id, cancellationToken)
            : [];

        var district = query.DistrictId is { } districtId
            ? await reader.FindActiveDistrictAsync(districtId, cancellationToken)
            : null;

        // Store pickup's zone is the *location's* district zone, not the address district's (doc 08 §3.5
        // R7). Until a method is chosen there is nothing to resolve, and with no pickup location seeded
        // (§9 gives zero, client input D8) the pickup branch has nothing to resolve to either.
        var methods = district is { } chosen
            ? await reader.DeliveryMethodsForZoneAsync(chosen.DeliveryZone, cancellationToken)
            : [];

        var selectedMethod = methods.FirstOrDefault(method => method.Id == query.DeliveryMethodId);
        var totals = CheckoutQuote.Totals(lines, selectedMethod?.Rate);

        var session = new CheckoutSessionDto(
            // A fresh token per page load. It is the shopper's protection against their own double click,
            // not a session identifier, so there is nothing to keep and nothing to look up.
            Guid.NewGuid(),
            lines.Select(ToLine).ToList().AsReadOnly(),
            totals ?? EmptyTotals,
            districts.Select(ToDistrict).ToList().AsReadOnly(),
            DeliveryOptions(methods, totals?.ItemsSubtotal ?? 0m),
            pickupLocations.Select(ToPickupLocation).ToList().AsReadOnly(),
            paymentMethods.Select(ToPaymentMethod).ToList().AsReadOnly(),
            district?.Id,
            selectedMethod is { IsAvailable: true } ? selectedMethod.Id : null,
            Blockers(lines, district, methods, paymentMethods));

        if (cart is not { } identity)
        {
            // A guest whose cookie named a cart that is gone gets the token cleared, exactly as a bag read
            // does. Anything else leaves the cookie alone: checkout never issues one, because it never
            // creates a cart.
            return lookup.GuestToken is not null && !lookup.ByCustomer
                ? new CheckoutView(session, CartCookieAction.Clear, null)
                : new CheckoutView(session, CartCookieAction.Leave, null);
        }

        return identity.IsCustomerCart
            ? new CheckoutView(
                session,
                lookup.GuestToken is not null ? CartCookieAction.Clear : CartCookieAction.Leave,
                null)
            : new CheckoutView(session, CartCookieAction.Issue, identity.Token);
    }

    /// <summary>
    /// The reasons the Place order button cannot be pressed, in the order a shopper would meet them.
    /// </summary>
    internal static IReadOnlyList<CheckoutBlocker> Blockers(
        IReadOnlyList<CheckoutCartLine> lines,
        CheckoutDistrict? district,
        IReadOnlyList<CheckoutDeliveryMethod> methods,
        IReadOnlyList<CheckoutPaymentMethod> paymentMethods)
    {
        var blockers = new List<CheckoutBlocker>();

        if (lines.Count == 0)
        {
            blockers.Add(CheckoutBlocker.EmptyBag);
        }
        else if (!lines.All(CheckoutQuote.IsPlaceable))
        {
            blockers.Add(CheckoutBlocker.LineNotPurchasable);
        }

        // Only once a district has been chosen: "you have not told us where it is going" is the form's
        // job to say, and reporting it as "we do not deliver there" would be wrong and alarming.
        if (district is not null && !methods.Any(method => method.IsAvailable))
        {
            blockers.Add(CheckoutBlocker.NoDeliveryMethod);
        }

        if (paymentMethods.Count == 0)
        {
            blockers.Add(CheckoutBlocker.NoPaymentMethod);
        }

        return blockers.AsReadOnly();
    }

    private static ReadOnlyCollection<DeliveryOptionDto> DeliveryOptions(
        IReadOnlyList<CheckoutDeliveryMethod> methods,
        decimal itemsSubtotal) =>
        methods
            .Select(method => new DeliveryOptionDto(
                method.Id,
                method.Code,
                method.Name,
                method.Description,
                method.RequiresAddress,
                method.IsAvailable,

                // Null rather than zero for an unavailable option: "we do not know what this would cost"
                // and "this costs nothing" are different answers and the page draws them differently.
                method.IsAvailable ? method.Rate!.Value.Fee : null,
                method.IsAvailable ? method.Rate!.Value.FreeAboveAmount : null,
                method.IsAvailable
                    ? CheckoutQuote.FeeAfterDiscount(method.Rate!.Value, itemsSubtotal)
                    : null,
                method.IsAvailable ? method.Rate!.Value.EstimatedDaysMin : null,
                method.IsAvailable ? method.Rate!.Value.EstimatedDaysMax : null))
            .ToList()
            .AsReadOnly();

    private static CheckoutLineDto ToLine(CheckoutCartLine line) =>
        new(
            line.VariantId,
            line.ProductId,
            line.ProductName,
            line.Slug,
            line.ColourName,
            line.SizeLabel,
            line.ImageUrl,
            line.ImageAltText,
            line.Quantity,
            line.AvailableQuantity,
            line.UnitOriginalPrice,
            line.UnitPrice,
            line.UnitPrice * line.Quantity,
            line.Status);

    private static DistrictDto ToDistrict(CheckoutDistrict district) =>
        new(district.Id, district.Name, (byte)district.DeliveryZone);

    private static PickupLocationDto ToPickupLocation(CheckoutPickupLocation location) =>
        new(
            location.Id,
            location.Name,
            location.AddressLine,
            location.City,
            location.DistrictId,
            location.Phone,
            location.OpeningHours);

    private static PaymentMethodDto ToPaymentMethod(CheckoutPaymentMethod method) =>
        new(method.Code, method.DisplayName, method.CheckoutDescription);

    private static CheckoutTotalsDto EmptyTotals { get; } =
        new(0, 0, 0m, 0m, 0m, 0m, null, 0m, null, false, Catalog.Contracts.PriceDto.Lkr);
}
