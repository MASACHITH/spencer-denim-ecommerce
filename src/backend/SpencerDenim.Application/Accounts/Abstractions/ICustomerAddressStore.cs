using SpencerDenim.Application.Accounts.Contracts;

namespace SpencerDenim.Application.Accounts.Abstractions;

/// <summary>
/// A customer's address book: <c>identity.CustomerAddresses</c> (ACC-02, doc 11 P11 step 11).
/// </summary>
/// <remarks>
/// <b>Every method takes the customer's user id, and it always comes from the session cookie.</b> There
/// is no overload that takes an address id alone: a port whose methods could be called without an owner
/// is a port an endpoint can call with a client-supplied owner, which is how a shopper reads somebody
/// else's address book. The ownership filter is in the <c>WHERE</c> clause of every statement rather than
/// in a check the caller is trusted to make first.
/// </remarks>
public interface ICustomerAddressStore
{
    /// <summary>
    /// This customer's addresses, default first and then newest first.
    /// </summary>
    /// <remarks>
    /// <b>Default first, because that is the one a shopper is looking for</b>, and then newest first
    /// because an address book has no other meaningful order — alphabetical by recipient would put a
    /// shopper's own name next to their mother's with nothing to distinguish them, and primary-key order
    /// reads as arbitrary.
    /// </remarks>
    Task<IReadOnlyList<CustomerAddressDto>> ListAsync(int userId, CancellationToken cancellationToken);

    /// <summary>
    /// Adds one, enforcing doc 08 §3.1's cap of ten inside the same transaction that counts.
    /// </summary>
    /// <remarks>
    /// <b>The count and the insert are one transaction, because a count outside one is a suggestion.</b>
    /// Two tabs adding an eleventh address would both count ten and both insert. SQL Server cannot
    /// express "at most ten rows per owner" declaratively, so the transaction and a lock on the owner are
    /// what make the cap real.
    /// </remarks>
    Task<CustomerAddressResult> AddAsync(
        int userId,
        CustomerAddressEdit edit,
        CancellationToken cancellationToken);

    /// <summary>Replaces one of this customer's addresses.</summary>
    Task<CustomerAddressResult> UpdateAsync(
        int userId,
        int addressId,
        CustomerAddressEdit edit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes one of this customer's addresses.
    /// </summary>
    /// <remarks>
    /// <b>A hard delete, and no order is affected by it.</b> <c>sales.Orders.ShipAddress</c> is a
    /// snapshot with no foreign key to this table (<c>AddressSnapshot</c>), so a past order keeps showing
    /// where it was actually sent. Doc 08 §3.1 step 2's PDPA routine deletes these rows the same way.
    /// <para>
    /// <b>Deleting the default promotes the next one</b>, so a customer is never left with a book and no
    /// default — which would make checkout's "use my saved address" have nothing to pre-select.
    /// </para>
    /// </remarks>
    Task<CustomerAddressResult> DeleteAsync(int userId, int addressId, CancellationToken cancellationToken);

    /// <summary>
    /// Makes one of this customer's addresses the default (doc 11 P11 step 11: "sets one default").
    /// </summary>
    /// <remarks>
    /// <b>One statement pair in one transaction, because <c>UX_CustomerAddresses_UserId_Default</c> is a
    /// unique filtered index.</b> Setting the new default before clearing the old one violates it, so the
    /// clear comes first; and two tabs doing this together must not both clear and both set, which is
    /// what the transaction and the owner lock prevent.
    /// </remarks>
    Task<CustomerAddressResult> SetDefaultAsync(
        int userId,
        int addressId,
        CancellationToken cancellationToken);
}
