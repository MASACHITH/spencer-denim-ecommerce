using System.Globalization;

namespace SpencerDenim.Application.Sales.Payments.PayHere;

/// <summary>
/// The <c>order_id</c> the shop gives PayHere, and the way back from it (doc 11 P8b step 1).
/// <para>
/// <b>It names the attempt and not only the order.</b> The value is <c>{OrderNumber}-{PaymentId}</c>, for
/// example <c>SD-2026-000123-4567</c>, because an order can hold several payment rows (a failed attempt is
/// followed by a fresh one) and a notification has to say which of them it is about. Both halves are covered
/// by the signature on the checkout form and on the notification, so neither can be changed in transit: a
/// notification for another order's payment fails <c>md5sig</c> before this is ever parsed.
/// </para>
/// <para>
/// <b>Parsing is a strict inverse, not a best effort.</b> A value is accepted only if formatting what was
/// parsed gives the same text back, so <c>SD-2026-000123-007</c>, <c>SD-2026-000123- 7</c> and
/// <c>SD-2026-000123-+7</c> are all refused rather than read as payment 7. The order number is then compared
/// to the payment's own order by the caller, so even a well-formed value only ever finds a payment whose
/// order really has that number.
/// </para>
/// <para>
/// <b>Within PayHere's limits.</b> An order number is at most 20 characters (<c>varchar(20)</c>) and a payment
/// id at most ten digits, so the whole value is at most 31 characters.
/// </para>
/// </summary>
public static class PayHereOrderReference
{
    /// <summary><c>sales.Orders.OrderNumber</c> is <c>varchar(20)</c>.</summary>
    public const int OrderNumberMaxLength = 20;

    /// <summary>The longest value this class produces: an order number, a hyphen and ten digits.</summary>
    public const int MaxLength = OrderNumberMaxLength + 1 + 10;

    /// <summary>The <c>order_id</c> for payment <paramref name="paymentId"/> of the order numbered <paramref name="orderNumber"/>.</summary>
    /// <exception cref="ArgumentException">The order number is empty, too long or not made of letters, digits and hyphens.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The payment id is not positive.</exception>
    public static string Format(string orderNumber, int paymentId)
    {
        if (!IsValidOrderNumber(orderNumber))
        {
            throw new ArgumentException(
                "An order number is 1 to 20 letters, digits and hyphens.",
                nameof(orderNumber));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(paymentId);

        return string.Create(CultureInfo.InvariantCulture, $"{orderNumber}-{paymentId}");
    }

    /// <summary>
    /// Splits an <c>order_id</c> back into the order number and payment id it was made from, or answers
    /// <see langword="false"/> for anything this class would not have produced.
    /// </summary>
    public static bool TryParse(string? orderId, out string orderNumber, out int paymentId)
    {
        orderNumber = string.Empty;
        paymentId = 0;

        if (string.IsNullOrEmpty(orderId) || orderId.Length > MaxLength)
        {
            return false;
        }

        var split = orderId.LastIndexOf('-');

        if (split <= 0 || split == orderId.Length - 1)
        {
            return false;
        }

        var number = orderId[..split];
        var suffix = orderId[(split + 1)..];

        if (!IsValidOrderNumber(number)
            || !int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || id <= 0)
        {
            return false;
        }

        // The strict-inverse rule: what was read must format back to exactly what was given.
        if (!string.Equals(Format(number, id), orderId, StringComparison.Ordinal))
        {
            return false;
        }

        orderNumber = number;
        paymentId = id;

        return true;
    }

    private static bool IsValidOrderNumber(string? value)
    {
        // No leading, trailing or doubled hyphen: the hyphen is also the separator before the payment id, and
        // a number that could end in one would let "SD-2026-000123--5" read as order "SD-2026-000123-".
        if (string.IsNullOrEmpty(value)
            || value.Length > OrderNumberMaxLength
            || value[0] == '-'
            || value[^1] == '-'
            || value.Contains("--", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value)
        {
            var allowed = character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-';

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}
