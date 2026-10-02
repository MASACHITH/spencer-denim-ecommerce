namespace SpencerDenim.Domain.Sales;

/// <summary>
/// The one place that decides what a cart quantity may be (doc 08 §3.5: <c>CK Quantity 1–10</c>).
/// <para>
/// Two different rules live here and they are not interchangeable:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>What a caller may ask for</b> (<see cref="IsRequestable"/>) — 1 to 10. Anything else is a
/// rejected request, not a clamp: a shopper who typed 11 is told 10 is the limit rather than being
/// silently given 10, and a quantity of 0 is a removal, which is a different endpoint.
/// </description></item>
/// <item><description>
/// <b>What an add may end up at</b> (<see cref="ClampTotal"/>) — an add of 6 to a row that already
/// holds 6 lands on 10, not 12, and not an error. That is doc 08 §3.5's own
/// <c>IIF(Quantity + @q &gt; 10, 10, Quantity + @q)</c>, and the clamp happens in the database so two
/// concurrent adds cannot step over the ceiling between a read and a write.
/// </description></item>
/// </list>
/// </summary>
public static class CartQuantity
{
    public const short Min = CartItem.MinQuantity;

    public const short Max = CartItem.MaxQuantity;

    /// <summary>True when a caller may ask for exactly this many.</summary>
    public static bool IsRequestable(int quantity) => quantity >= Min && quantity <= Max;

    /// <summary>
    /// What a row holds after adding <paramref name="add"/> to <paramref name="existing"/>: the sum,
    /// capped at <see cref="Max"/>. Mirrors the SQL so a unit test and the database agree.
    /// </summary>
    public static short ClampTotal(int existing, int add)
    {
        var total = (long)existing + add;
        return total > Max ? Max : (short)Math.Max(total, Min);
    }
}
