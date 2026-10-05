using SpencerDenim.Application.Abstractions.Results;

namespace SpencerDenim.Application.Sales.Checkout;

/// <summary>
/// The field-level validation of doc 11 P6 step 8, done before anything touches the database.
/// <para>
/// <b>Shape first, references second.</b> The order of the checks is the same discipline
/// <c>AddToCartCommandHandler</c> records: a request naming nothing valid must not cost a query, and a
/// request missing six fields must come back with six messages rather than the first one. Everything here
/// is decided from the command alone; whether a district or a delivery method actually exists is the
/// handler's job, because only the database knows.
/// </para>
/// </summary>
internal static class PlaceOrderValidator
{
    /// <summary><c>Orders.FirstName</c> / <c>LastName</c>, <c>nvarchar(80)</c>.</summary>
    public const int MaxNameLength = 80;

    /// <summary><c>Orders.Email</c>, <c>nvarchar(256)</c>.</summary>
    public const int MaxEmailLength = 256;

    /// <summary><c>Orders.Phone</c> and <c>Ship_Phone</c>, <c>varchar(20)</c>.</summary>
    public const int MaxPhoneLength = 20;

    /// <summary><c>Ship_RecipientName</c>, <c>nvarchar(120)</c>.</summary>
    public const int MaxRecipientNameLength = 120;

    /// <summary><c>Ship_Line1</c> / <c>Line2</c> / <c>Landmark</c>, <c>nvarchar(150)</c>.</summary>
    public const int MaxAddressLineLength = 150;

    /// <summary><c>Ship_City</c>, <c>nvarchar(80)</c>.</summary>
    public const int MaxCityLength = 80;

    /// <summary><c>Ship_PostalCode</c>, <c>varchar(10)</c>.</summary>
    public const int MaxPostalCodeLength = 10;

    /// <summary><c>Orders.CustomerNote</c>, <c>nvarchar(500)</c>.</summary>
    public const int MaxCustomerNoteLength = 500;

    /// <summary>
    /// Every reason the request cannot be acted on, or an empty list.
    /// </summary>
    /// <param name="requiresAddress">
    /// Whether the chosen delivery method needs a shipping address — <c>DeliveryMethods.RequiresAddress</c>,
    /// which only the handler can know. Passed in rather than inferred from the id, because
    /// <c>CK_Orders_ShipAddress</c> keys on the method's code and nothing else may decide this.
    /// <para>
    /// <see langword="null"/> means <b>the method could not be resolved</b> (missing id, unknown id, or one
    /// that has since been switched off). The delivery branch is then skipped entirely rather than guessed
    /// at: demanding address fields of a shopper who chose store pickup, because the option vanished under
    /// them, would be four wrong messages on top of the one true one.
    /// </para>
    /// </param>
    public static IReadOnlyList<ValidationError> Validate(PlaceOrderCommand command, bool? requiresAddress)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();

        if (command.CheckoutKey == Guid.Empty)
        {
            errors.Add(new ValidationError(
                "checkoutKey",
                "This checkout session has expired. Reload the page and try again."));
        }

        Required(errors, "firstName", command.FirstName, MaxNameLength, "Enter your first name.");
        Optional(errors, "lastName", command.LastName, MaxNameLength);

        var email = Trimmed(command.Email);
        if (email is null)
        {
            errors.Add(new ValidationError("email", "Enter your email address."));
        }
        else if (email.Length > MaxEmailLength)
        {
            errors.Add(new ValidationError(
                "email",
                $"An email address can be at most {MaxEmailLength} characters."));
        }
        else if (!LooksLikeEmail(email))
        {
            errors.Add(new ValidationError("email", "Enter an email address we can send your order to."));
        }

        // Required although `Orders.Phone` is nullable, and the column's nullability is not the reason to
        // skip it: doc 11 P6 step 2 asks for a phone number, a courier in Sri Lanka needs one, and the
        // column is nullable only so that a PDPA anonymisation can clear it later (doc 08 §3.1, N5).
        Required(errors, "phone", command.Phone, MaxPhoneLength, "Enter a phone number for the delivery.");

        if (command.DeliveryMethodId is null or <= 0)
        {
            errors.Add(new ValidationError("deliveryMethodId", "Choose how you would like your order delivered."));
        }

        if (string.IsNullOrWhiteSpace(command.PaymentMethodCode))
        {
            errors.Add(new ValidationError("paymentMethodCode", "Choose how you would like to pay."));
        }

        if (command.ExpectedGrandTotal is null)
        {
            // G-10's guard cannot be skipped by omitting the field: without it the server would place an
            // order at a total nobody had agreed to, which is the whole thing the rule exists to stop.
            errors.Add(new ValidationError(
                "expectedGrandTotal",
                "The order total is missing. Reload the page and try again."));
        }
        else if (command.ExpectedGrandTotal < 0m)
        {
            errors.Add(new ValidationError("expectedGrandTotal", "The order total is not a valid amount."));
        }

        Optional(errors, "customerNote", command.CustomerNote, MaxCustomerNoteLength);

        ValidateDelivery(command, requiresAddress, errors);

        return errors.AsReadOnly();
    }

    private static void ValidateDelivery(
        PlaceOrderCommand command,
        bool? requiresAddress,
        List<ValidationError> errors)
    {
        if (requiresAddress is not { } needsAddress)
        {
            return;
        }

        if (!needsAddress)
        {
            // Store pickup. `CK_Orders_Pickup` requires the location, and R7 takes the district from it, so
            // a district on the request is ignored rather than rejected — the shopper did not choose it.
            if (command.PickupLocationId is null or <= 0)
            {
                errors.Add(new ValidationError("pickupLocationId", "Choose which store to collect from."));
            }

            return;
        }

        if (command.DistrictId is null or <= 0)
        {
            errors.Add(new ValidationError("districtId", "Choose the district your order is going to."));
        }

        var address = command.Address;
        if (address is null)
        {
            // One message rather than five: the form was not filled in at all, and five identical-looking
            // errors against fields the caller never sent would be noise.
            errors.Add(new ValidationError("address", "Enter the delivery address."));
            return;
        }

        // The four `CK_Orders_ShipAddress` requires for a non-pickup order, named as the shopper sees them.
        Required(
            errors,
            "address.recipientName",
            address.RecipientName,
            MaxRecipientNameLength,
            "Enter the name of the person receiving the order.");
        Required(
            errors,
            "address.phone",
            address.Phone,
            MaxPhoneLength,
            "Enter a phone number for the delivery address.");
        Required(errors, "address.line1", address.Line1, MaxAddressLineLength, "Enter the street address.");
        Required(errors, "address.city", address.City, MaxCityLength, "Enter the town or city.");

        Optional(errors, "address.line2", address.Line2, MaxAddressLineLength);
        Optional(errors, "address.postalCode", address.PostalCode, MaxPostalCodeLength);
        Optional(errors, "address.landmark", address.Landmark, MaxAddressLineLength);
    }

    private static void Required(
        List<ValidationError> errors,
        string field,
        string? value,
        int maxLength,
        string missingMessage)
    {
        var trimmed = Trimmed(value);

        if (trimmed is null)
        {
            errors.Add(new ValidationError(field, missingMessage));
        }
        else if (trimmed.Length > maxLength)
        {
            errors.Add(new ValidationError(field, TooLong(maxLength)));
        }
    }

    private static void Optional(List<ValidationError> errors, string field, string? value, int maxLength)
    {
        if (Trimmed(value) is { } trimmed && trimmed.Length > maxLength)
        {
            errors.Add(new ValidationError(field, TooLong(maxLength)));
        }
    }

    private static string TooLong(int maxLength) => $"This can be at most {maxLength} characters.";

    /// <summary>
    /// The value with surrounding whitespace removed, or <see langword="null"/> when there is nothing
    /// there. One helper, so "missing" means the same thing for a field the client omitted, sent as
    /// <c>null</c>, or sent as <c>"   "</c> — three shapes that reach the server as different values and
    /// are the same mistake.
    /// </summary>
    public static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Deliberately permissive: one <c>@</c> with something either side, a dot in the domain, and no
    /// whitespace.
    /// <para>
    /// <b>Why not a strict pattern.</b> A regex that tries to implement RFC 5322 rejects real addresses —
    /// apostrophes, plus-addressing, long and new top-level domains — and the cost of being wrong here is
    /// that a shopper who typed their own address correctly cannot buy anything. The address is confirmed
    /// by the order email reaching it, not by this check, whose only job is to catch a phone number typed
    /// into the wrong box.
    /// </para>
    /// </summary>
    public static bool LooksLikeEmail(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var at = candidate.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != candidate.LastIndexOf('@'))
        {
            return false;
        }

        var domain = candidate[(at + 1)..];
        var dot = domain.IndexOf('.', StringComparison.Ordinal);

        return dot > 0 && dot < domain.Length - 1;
    }
}
