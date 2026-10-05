using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Checkout.Abstractions;

/// <summary>
/// Everything the placement transaction writes for one order, worked out by the use case before the
/// transaction opens.
/// </summary>
/// <remarks>
/// <b>This is a value, not a builder, and the store does no arithmetic and no lookups with it.</b> Doc 08
/// §5.4 splits placement into "price the cart and allocate the number" and then "one transaction". Keeping
/// the two apart in the types as well means the transaction is as short as it can be — it holds row locks
/// on <c>StockLevels</c>, which every other shopper's checkout is waiting on — and it means the figures in
/// the order row are provably the figures the use case compared against the shopper's screen.
/// </remarks>
public sealed record OrderPlacement(
    int CartId,
    string OrderNumber,
    Guid CheckoutIdempotencyKey,
    int? CustomerUserId,
    string PaymentProviderCode,
    string Email,
    string? Phone,
    string FirstName,
    string? LastName,
    int DeliveryDistrictId,
    string DeliveryDistrictName,
    DeliveryZone DeliveryZone,
    AddressSnapshot ShipAddress,
    int DeliveryMethodId,
    int? PickupLocationId,
    DeliverySnapshot Delivery,
    OrderPricingResult Pricing,
    IReadOnlyList<OrderLineSnapshot> Lines,
    string? CustomerNote,
    string? PlacedIpAddress,
    DateTime PlacedAtUtc,
    DateTime PaymentDueAtUtc);

/// <summary>
/// The catalogue facts an order line keeps, paired with the priced figures. Split from
/// <see cref="PricedOrderLine"/> so that the Domain's arithmetic knows nothing about product names.
/// </summary>
public sealed record OrderLineSnapshot(
    short LineNumber,
    int ProductId,
    int VariantId,
    int DepartmentId,
    int CategoryId,
    string ProductName,
    string ProductCode,
    string Sku,
    string ColourName,
    string SizeLabel,
    string? ThumbnailStorageKey,
    PricedOrderLine Priced);

/// <summary>What the placement attempt did.</summary>
public enum OrderPlacementStatus : byte
{
    /// <summary>The order exists. Stock is reserved and the bag is empty.</summary>
    Placed = 1,

    /// <summary>
    /// At least one line could not be reserved. Nothing was written: doc 08 §5.4 step 3 rolls the whole
    /// transaction back rather than placing a short order or quietly dropping a line.
    /// </summary>
    OutOfStock = 2,

    /// <summary>
    /// An order with this <c>CheckoutIdempotencyKey</c> already existed, so this attempt wrote nothing.
    /// The caller returns the first order.
    /// </summary>
    DuplicateSubmission = 3,
}

/// <summary>
/// The outcome of one placement transaction.
/// </summary>
/// <param name="ShortLines">
/// For <see cref="OrderPlacementStatus.OutOfStock"/>: how many of each variant are available now, which is
/// what doc 08 §5.4 step 3 and doc 11 P6 ask the shopper to be told ("only 1 left"). Empty otherwise.
/// </param>
public sealed record OrderPlacementResult(
    OrderPlacementStatus Status,
    Guid? PublicId,
    IReadOnlyDictionary<int, int> ShortLines)
{
    // A static readonly field declared *before* the members that use it, not a property: static property
    // initialisers run in declaration order, so `DuplicateSubmission` initialised before `EmptyShortLines`
    // did and was handed null. The compiler caught it; at run time it would have been a dictionary that was
    // null only on the duplicate-submission path.
    private static readonly IReadOnlyDictionary<int, int> EmptyShortLines = new Dictionary<int, int>();

    public static OrderPlacementResult Placed(Guid publicId) =>
        new(OrderPlacementStatus.Placed, publicId, EmptyShortLines);

    public static OrderPlacementResult OutOfStock(IReadOnlyDictionary<int, int> shortLines) =>
        new(OrderPlacementStatus.OutOfStock, null, shortLines);

    public static OrderPlacementResult DuplicateSubmission { get; } =
        new(OrderPlacementStatus.DuplicateSubmission, null, EmptyShortLines);
}

/// <summary>
/// The one transaction of doc 08 §5.4 / doc 11 P6 step 9.
/// </summary>
/// <remarks>
/// <b>One method, because it is one atomic act.</b> Reserving stock, taking the order number's place in
/// the world, writing the order, its lines, its status history and its payment row, and emptying the bag
/// either all happened or none of it did. An interface with a <c>BeginTransaction</c> on it would let a
/// caller get that wrong, so there is nothing here to get wrong.
/// </remarks>
public interface IOrderPlacementStore
{
    /// <summary>
    /// Reserves stock for every line in <c>VariantId</c> order (doc 08 §5.2/§5.3), then inserts the order
    /// and empties the bag. Retried by EF's execution strategy on a deadlock, which is safe because every
    /// step is conditional (§5.3).
    /// </summary>
    Task<OrderPlacementResult> PlaceAsync(OrderPlacement placement, CancellationToken cancellationToken);
}
