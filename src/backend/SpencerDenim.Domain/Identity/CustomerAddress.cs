using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Reference;

namespace SpencerDenim.Domain.Identity;

/// <summary>
/// <c>identity.CustomerAddresses</c> (doc 08 §3.1, CHK-01 / ACC-02) [A]: one entry in a registered
/// customer's address book.
/// <para>
/// <b>It is not what an order ships to.</b> <c>sales.Orders.ShipAddress</c> is a snapshot complex type
/// with no FK to this table (see <c>AddressSnapshot</c>), so editing or deleting a saved address never
/// rewrites where a past order was sent. This table is the book a shopper picks from; the order keeps
/// the copy.
/// </para>
/// <para>
/// <b>Why the owning column is <see cref="UserId"/> and not <c>CustomerUserId</c>.</b> Doc 08 §3.1 names
/// the column <c>UserId</c>, and the table lives in the <c>identity</c> schema beside
/// <c>UserLogins</c>/<c>UserTokens</c>, which name theirs the same way. <c>sales.WishlistItems</c> and
/// <c>sales.Carts</c> say <c>CustomerUserId</c> because they are in another schema and have to say
/// <i>which</i> kind of user; here there is no ambiguity. The app rule that only a
/// <see cref="UserType.Customer"/> row may own one is enforced in the use case, exactly as
/// <c>identity.UserRoles</c>' mirror rule ("only <c>UserType = Staff</c> users receive roles") is.
/// </para>
/// <para>
/// <b>No <c>ISoftDeletable</c>.</b> Doc 08 §3.1 step 2 of the PDPA routine <i>deletes</i> these rows with
/// a set-based <c>ExecuteDelete</c>, and a shopper removing an address from their book means it is gone.
/// A soft-deleted address would keep a name, a phone number and a street in a table the PDPA acceptance
/// test searches.
/// </para>
/// </summary>
public sealed class CustomerAddress : IAuditable
{
    /// <summary><c>nvarchar(40)</c>, doc 08 §3.1.</summary>
    public const int LabelMaxLength = 40;

    /// <summary><c>nvarchar(120)</c>, doc 08 §3.1 — the same width as <c>Orders.Ship_RecipientName</c>.</summary>
    public const int RecipientNameMaxLength = 120;

    /// <summary><c>varchar(20)</c>, doc 08 §3.1.</summary>
    public const int PhoneMaxLength = 20;

    /// <summary><c>nvarchar(150)</c>, doc 08 §3.1.</summary>
    public const int LineMaxLength = 150;

    /// <summary><c>nvarchar(80)</c>, doc 08 §3.1.</summary>
    public const int CityMaxLength = 80;

    /// <summary><c>varchar(10)</c>, doc 08 §3.1.</summary>
    public const int PostalCodeMaxLength = 10;

    /// <summary><c>nvarchar(150)</c>, doc 08 §3.1.</summary>
    public const int LandmarkMaxLength = 150;

    /// <summary>
    /// Doc 08 §3.1: "App cap: 10 addresses per customer", restated in doc 11 P11 step 11 and in its
    /// business rules. A <i>cap</i> and not a constraint: SQL Server has no way to express "at most ten
    /// rows per owner" declaratively, so the eleventh insert is refused by the use case inside the same
    /// transaction that counts, and <c>CustomerAddressLimitTests</c> proves two parallel adds cannot both
    /// pass the count.
    /// </summary>
    public const int MaxPerCustomer = 10;

    public int Id { get; set; }

    /// <summary>
    /// The customer whose book this is. <c>NO ACTION</c> (doc 08 §1.6's default): a customer is
    /// anonymised rather than hard-deleted, and §3.1 step 2 clears these rows itself.
    /// </summary>
    public int UserId { get; set; }

    /// <summary>"Home", "Office" — the shopper's own word for it, or null when they did not give one.</summary>
    public string? Label { get; set; }

    public string RecipientName { get; set; } = string.Empty;

    /// <summary>E.164, as <c>identity.Users.PhoneNumber</c> and <c>Orders.Ship_Phone</c> are.</summary>
    public string Phone { get; set; } = string.Empty;

    public string Line1 { get; set; } = string.Empty;

    public string? Line2 { get; set; }

    public string City { get; set; } = string.Empty;

    /// <summary>FK <c>ref.Districts</c>. Drives the delivery zone, so it is <c>NOT NULL</c> (doc 08 §3.1).</summary>
    public int DistrictId { get; set; }

    public District? District { get; set; }

    public string? PostalCode { get; set; }

    /// <summary>A nearby landmark. Sri Lankan addresses often need one to be findable.</summary>
    public string? Landmark { get; set; }

    /// <summary>
    /// Doc 08 §3.1: <c>UX_CustomerAddresses_UserId_Default (UserId) WHERE IsDefault = 1</c> — at most one
    /// default per customer, enforced by the database rather than by whoever writes next.
    /// </summary>
    public bool IsDefault { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? CreatedByUserId { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }
}
