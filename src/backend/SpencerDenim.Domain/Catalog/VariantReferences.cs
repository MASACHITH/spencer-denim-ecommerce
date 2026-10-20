namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// What happens to each thing that points at a <see cref="ProductVariant"/> when an admin takes that
/// variant off a product (ADM-10, doc 11 P1 step 9).
/// <para>
/// <b>Why this is a declaration rather than a few lines inside the store.</b> Doc 08 §1.6 makes
/// <c>ON DELETE NO ACTION</c> the default and lists the handful of cascades; <c>ProductVariants</c> is not
/// among them, and neither is anything pointing at it. So every reference has to be dealt with by hand,
/// and each one that is forgotten is a 500 the admin cannot act on — <i>three</i> were forgotten in a row
/// on this task alone (<c>StockLevels</c>, the colourway's composite key, then <c>CartItems</c>), each
/// found only by a test that happened to touch a real database. Guessing which is the last one is how a
/// fourth arrives.
/// </para>
/// <para>
/// <b>What makes it hold.</b> <c>VariantReferenceConventionTests</c> reflects over the EF model, finds
/// every foreign key whose principal is <see cref="ProductVariant"/>, and fails if one is not named here.
/// It needs no database. It did its job when ADM-12 added <c>inventory.InventoryMovements</c> (doc 08
/// §3.4, issue <b>#108</b>): the test went red the same day, rather than the delete silently destroying an
/// append-only ledger or answering 500 in front of a client.
/// </para>
/// </summary>
public static class VariantReferences
{
    /// <summary>What a reference does to a variant's deletion.</summary>
    public enum Disposition
    {
        /// <summary>
        /// The row is deleted in the same unit of work, before the variant. Legitimate only where the row
        /// is a <i>current</i> fact about a variant that is going away, never where it is history.
        /// </summary>
        DeletedWithTheVariant,

        /// <summary>
        /// The variant cannot be hard-deleted while such a row exists; the admin deactivates it instead.
        /// For history that doc 08 §1.6 keeps "for the life of the system".
        /// </summary>
        BlocksTheDelete,
    }

    /// <summary>
    /// Every entity that holds a foreign key to <see cref="ProductVariant"/>, by CLR type name, and what
    /// this task does about it.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>
    /// <see cref="Inventory.StockLevel"/> — <b>deleted with the variant.</b> It is 1:1 and holds the
    /// variant's current count, so it has no meaning without it. A variant that holds stock is separately
    /// <i>blocked</i> by a business rule (deleting it would take a count of real garments with it), so
    /// every row deleted here reads zero on hand and zero reserved.
    /// </item>
    /// <item>
    /// <see cref="Sales.CartItem"/> — <b>deleted with the variant.</b> A bag line is a shopper's current
    /// intention, not a record to keep: doc 11 P1's edge cases name this exact case ("Product unpublished
    /// or removed while in customers' bags") and P5 already has to cope with a line disappearing. Blocking
    /// instead would let any shopper who left a size in their bag stop an admin correcting the catalogue.
    /// </item>
    /// <item>
    /// <see cref="Inventory.InventoryMovement"/> — <b>blocks the delete</b> (ADM-12, #108). The ledger is kept
    /// "for the life of the system" (doc 08 §3.4) and never deleted (§1.6), so a variant whose stock has ever
    /// been changed by hand is switched off rather than removed, exactly as an ordered one is.
    /// </item>
    /// <item>
    /// <see cref="Sales.OrderLine"/> — <b>blocks the delete.</b> Doc 08 §3.2: "Deactivate once ordered;
    /// never delete", and §1.6 never deletes an order line. The admin switches the variant off.
    /// </item>
    /// </list>
    /// </remarks>
    public static IReadOnlyDictionary<string, Disposition> Known { get; } =
        new Dictionary<string, Disposition>(StringComparer.Ordinal)
        {
            [nameof(Inventory.StockLevel)] = Disposition.DeletedWithTheVariant,
            [nameof(Sales.CartItem)] = Disposition.DeletedWithTheVariant,
            [nameof(Sales.OrderLine)] = Disposition.BlocksTheDelete,
            [nameof(Inventory.InventoryMovement)] = Disposition.BlocksTheDelete,
        }.AsReadOnly();
}
