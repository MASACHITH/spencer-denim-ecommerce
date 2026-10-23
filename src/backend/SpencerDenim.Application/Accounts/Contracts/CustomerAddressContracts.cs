namespace SpencerDenim.Application.Accounts.Contracts;

/// <summary>
/// One entry in a customer's address book, as My account shows it (ACC-02, doc 11 P11 step 11).
/// </summary>
/// <param name="DistrictName">
/// Joined in so the screen can label the district without fetching the whole list of 25 to resolve one
/// id — and so a <i>deactivated</i> district still reads as its own name rather than as a blank.
/// </param>
/// <param name="DistrictIsActive">
/// False when the shop has stopped serving that district since the address was saved. The card says so
/// rather than silently offering an address checkout will not quote for — <c>ref.Districts.IsActive</c>
/// is admin-editable (doc 08 §3.1), so this is an ordinary state and not an error.
/// </param>
public sealed record CustomerAddressDto(
    int Id,
    string? Label,
    string RecipientName,
    string Phone,
    string Line1,
    string? Line2,
    string City,
    int? DistrictId,
    string? DistrictName,
    bool DistrictIsActive,
    string? PostalCode,
    string? Landmark,
    bool IsDefault,
    string CountryCode = Domain.Reference.Country.SriLankaCode,
    string CountryName = "Sri Lanka",
    string? Region = null);

/// <summary>What a customer may type into the address form (doc 11 P11 step 11).</summary>
public sealed record CustomerAddressEdit(
    string? Label,
    string RecipientName,
    string Phone,
    string Line1,
    string? Line2,
    string City,
    int? DistrictId,
    string? PostalCode,
    string? Landmark,
    bool IsDefault,
    string CountryCode = Domain.Reference.Country.SriLankaCode,
    string? Region = null);

/// <summary>The outcome of adding, editing or deleting an address.</summary>
public abstract record CustomerAddressResult
{
    private CustomerAddressResult()
    {
    }

    /// <summary>The book as it stands afterwards, so the screen re-renders from one answer.</summary>
    /// <remarks>
    /// <b>The whole book and not the one row that changed.</b> Setting a default clears the previous
    /// one, and deleting the default may promote another, so a response carrying only the edited row
    /// would leave the screen showing two defaults or none. One answer, one render.
    /// </remarks>
    public sealed record Saved(IReadOnlyList<CustomerAddressDto> Addresses) : CustomerAddressResult;

    /// <summary>
    /// Doc 08 §3.1's "App cap: 10 addresses per customer", restated in doc 11 P11 step 11.
    /// </summary>
    public sealed record LimitReached(int Limit) : CustomerAddressResult;

    /// <summary>
    /// The district is not one the shop serves — unknown, or <c>IsActive = 0</c>.
    /// </summary>
    /// <remarks>
    /// <b>Refused rather than stored.</b> An unknown id would now fail at the foreign key W3-01 added,
    /// with a constraint error no shopper can read; an inactive one satisfies the constraint and then has
    /// no delivery rate, so checkout could not quote for the address the shopper had just been allowed to
    /// save.
    /// </remarks>
    public sealed record UnknownDistrict : CustomerAddressResult;

    /// <summary>No such address, or it is not this customer's.</summary>
    /// <remarks>
    /// <b>One case for both, deliberately.</b> "That address is not yours" tells a caller that the id
    /// exists, which is the same leak the guest order link's single opaque refusal avoids.
    /// </remarks>
    public sealed record NotFound : CustomerAddressResult;
}
