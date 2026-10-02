using SpencerDenim.Application.Abstractions.Results;

namespace SpencerDenim.Application.Sales.Cart;

/// <summary>How a cart write ended, in the terms the transport maps to a status code.</summary>
public enum CartWriteStatus : byte
{
    /// <summary>Applied. The view is the bag as it now stands.</summary>
    Ok = 1,

    /// <summary>
    /// The request itself was wrong — a quantity outside 1–10, a variant id that is not a positive
    /// integer. A 400 naming the field.
    /// </summary>
    Invalid = 2,

    /// <summary>
    /// The variant is not something this shop sells (unknown id, unpublished product, deactivated
    /// department, category or variant). A 400 naming <c>variantId</c>: from outside, "no such variant"
    /// and "not for sale" are one answer on purpose, so ids cannot be probed.
    /// </summary>
    NotSellable = 3,

    /// <summary>
    /// A real variant with nothing left. A 409: the request was well formed and the shop's state is what
    /// refused it, and the same request may succeed after a restock.
    /// </summary>
    OutOfStock = 4,
}

/// <summary>The outcome of an add, a quantity change or a removal.</summary>
public sealed record CartWriteResult(
    CartWriteStatus Status,
    CartView? View,
    IReadOnlyList<ValidationError> Errors)
{
    private static readonly IReadOnlyList<ValidationError> NoErrors = [];

    public static CartWriteResult Ok(CartView view) => new(CartWriteStatus.Ok, view, NoErrors);

    public static CartWriteResult Invalid(string field, string message) =>
        new(CartWriteStatus.Invalid, null, [new ValidationError(field, message)]);

    public static CartWriteResult NotSellable() =>
        new(
            CartWriteStatus.NotSellable,
            null,
            [new ValidationError("variantId", "That item is not available to buy.")]);

    public static CartWriteResult OutOfStock() => new(CartWriteStatus.OutOfStock, null, NoErrors);
}
