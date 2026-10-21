using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Accounts.Abstractions;
using SpencerDenim.Application.Accounts.Contracts;
using SpencerDenim.Application.Sales.Checkout;
using SpencerDenim.Application.Sales.Checkout.Abstractions;

namespace SpencerDenim.Application.Accounts;

/// <summary>My account's profile read (ACC-02, doc 11 P11 step 10).</summary>
/// <param name="UserId">From the session cookie. Never from the request.</param>
public sealed record GetCustomerProfileQuery(int UserId);

/// <summary>Reads a customer's own profile.</summary>
public sealed class GetCustomerProfileQueryHandler(ICustomerProfileStore store)
    : IQueryHandler<GetCustomerProfileQuery, CustomerProfile?>
{
    public async Task<CustomerProfile?> HandleAsync(
        GetCustomerProfileQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return await store.FindAsync(query.UserId, cancellationToken);
    }
}

/// <summary>My account's profile save (ACC-02, doc 11 P11 step 10).</summary>
public sealed record SaveCustomerProfileCommand(
    int UserId,
    string? FirstName,
    string? LastName,
    string? Phone,
    int? DefaultDistrictId);

/// <summary>Saves a customer's own profile.</summary>
/// <remarks>
/// <b>A district the shop does not serve is silently dropped rather than refused</b>, and that is the
/// store's decision — see <c>ICustomerProfileStore.SaveAsync</c>. The reason it is not a validation error
/// is that the shopper's other three fields should still save: a profile form that refused the whole save
/// because a district was deactivated between the page load and the submit would lose a name change for a
/// reason that is nothing to do with the name. The response carries the saved profile, so the screen
/// shows the district as unset and the shopper can choose again.
/// </remarks>
public sealed class SaveCustomerProfileCommandHandler(ICustomerProfileStore store)
    : ICommandHandler<SaveCustomerProfileCommand, Result<CustomerProfile?>>
{
    public async Task<Result<CustomerProfile?>> HandleAsync(
        SaveCustomerProfileCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var edit = new CustomerProfileEdit(
            command.FirstName ?? string.Empty,
            command.LastName,
            command.Phone,
            command.DefaultDistrictId);

        var errors = CustomerAccountValidator.ValidateProfile(edit);
        if (errors.Count > 0)
        {
            return Result.Failure<CustomerProfile?>(errors);
        }

        return Result.Success(await store.SaveAsync(command.UserId, edit, cancellationToken));
    }
}

/// <summary>My account's address book (ACC-02, doc 11 P11 step 11).</summary>
public sealed record GetCustomerAddressesQuery(int UserId);

/// <summary>Reads a customer's own address book.</summary>
public sealed class GetCustomerAddressesQueryHandler(ICustomerAddressStore store)
    : IQueryHandler<GetCustomerAddressesQuery, IReadOnlyList<CustomerAddressDto>>
{
    public async Task<IReadOnlyList<CustomerAddressDto>> HandleAsync(
        GetCustomerAddressesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return await store.ListAsync(query.UserId, cancellationToken);
    }
}

/// <summary>Adding or replacing one saved address (ACC-02, doc 11 P11 step 11).</summary>
/// <param name="AddressId">
/// <see langword="null"/> to add. Any other value replaces that address, and only if it is this
/// customer's — the store filters on the owner in the <c>WHERE</c> clause of the write itself.
/// </param>
public sealed record SaveCustomerAddressCommand(
    int UserId,
    int? AddressId,
    string? Label,
    string? RecipientName,
    string? Phone,
    string? Line1,
    string? Line2,
    string? City,
    int DistrictId,
    string? PostalCode,
    string? Landmark,
    bool IsDefault,
    string? CountryCode = null,
    string? Region = null);

/// <summary>Saves one address.</summary>
/// <remarks>
/// <b>The country decides the shape</b> (owner decision 2026-10-21): a Sri Lankan address keeps its district
/// and is validated exactly as before; an overseas one has no district and follows
/// <see cref="DestinationRules"/> — the same rules checkout applies, so a saved address checkout would refuse
/// cannot be saved. Saving an address in a country the shop does not currently deliver to is allowed: the book
/// is the shopper's, and checkout is where "not at the moment" is said.
/// </remarks>
public sealed class SaveCustomerAddressCommandHandler(ICustomerAddressStore store, IDeliveryCountryReader countries)
    : ICommandHandler<SaveCustomerAddressCommand, Result<CustomerAddressResult>>
{
    public async Task<Result<CustomerAddressResult>> HandleAsync(
        SaveCustomerAddressCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var countryCode = DestinationRules.NormaliseCountryCode(command.CountryCode);
        var country = countryCode is null ? null : await countries.FindCountryAsync(countryCode, cancellationToken);
        var domestic = country is null or { IsDomestic: true };

        var edit = new CustomerAddressEdit(
            command.Label,
            command.RecipientName ?? string.Empty,
            command.Phone ?? string.Empty,
            command.Line1 ?? string.Empty,
            command.Line2,
            command.City ?? string.Empty,

            // An overseas address has no district, whatever the request sent.
            domestic ? command.DistrictId : null,
            command.PostalCode,
            command.Landmark,
            command.IsDefault,
            // The code as resolved, or as sent when it names no country, so the validator can refuse it.
            country?.Code ?? (string.IsNullOrWhiteSpace(command.CountryCode)
                ? Domain.Reference.Country.SriLankaCode
                : command.CountryCode.Trim()),
            domestic ? null : command.Region);

        var errors = CustomerAccountValidator.ValidateAddress(edit, country);
        if (errors.Count > 0)
        {
            return Result.Failure<CustomerAddressResult>(errors);
        }

        var result = command.AddressId is { } addressId
            ? await store.UpdateAsync(command.UserId, addressId, edit, cancellationToken)
            : await store.AddAsync(command.UserId, edit, cancellationToken);

        return Result.Success(result);
    }
}

/// <summary>Removing one saved address (ACC-02, doc 11 P11 step 11).</summary>
public sealed record DeleteCustomerAddressCommand(int UserId, int AddressId);

/// <summary>Removes one address.</summary>
public sealed class DeleteCustomerAddressCommandHandler(ICustomerAddressStore store)
    : ICommandHandler<DeleteCustomerAddressCommand, CustomerAddressResult>
{
    public async Task<CustomerAddressResult> HandleAsync(
        DeleteCustomerAddressCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return await store.DeleteAsync(command.UserId, command.AddressId, cancellationToken);
    }
}

/// <summary>Making one saved address the default (ACC-02, doc 11 P11 step 11).</summary>
public sealed record SetDefaultCustomerAddressCommand(int UserId, int AddressId);

/// <summary>Sets the default.</summary>
public sealed class SetDefaultCustomerAddressCommandHandler(ICustomerAddressStore store)
    : ICommandHandler<SetDefaultCustomerAddressCommand, CustomerAddressResult>
{
    public async Task<CustomerAddressResult> HandleAsync(
        SetDefaultCustomerAddressCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return await store.SetDefaultAsync(command.UserId, command.AddressId, cancellationToken);
    }
}

/// <summary>
/// My account's order history (ACC-02, doc 11 P11 step 12) — <b>the one shopper-facing gap this task
/// closes</b>.
/// </summary>
/// <param name="CustomerUserId">From the session cookie. Never from the request.</param>
public sealed record GetCustomerOrdersQuery(int CustomerUserId, int Page, int PageSize);

/// <summary>Reads a customer's own orders, newest first, with their tracking.</summary>
public sealed class GetCustomerOrdersQueryHandler(ICustomerOrderReader reader)
    : IQueryHandler<GetCustomerOrdersQuery, CustomerOrderPageDto>
{
    /// <summary>
    /// Rows per page when the caller does not say.
    /// </summary>
    /// <remarks>
    /// Ten. Doc 11 P11 step 12 asks only for "newest first" and names no page size, so this is a
    /// decision: ten rows with a thumbnail each is about one screen on a telephone and one short scroll
    /// on a desktop, and it keeps the first page of a long-standing customer's history cheap.
    /// </remarks>
    public const int DefaultPageSize = 10;

    public async Task<CustomerOrderPageDto> HandleAsync(
        GetCustomerOrdersQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return await reader.ListAsync(
            query.CustomerUserId,
            query.Page,
            query.PageSize <= 0 ? DefaultPageSize : query.PageSize,
            cancellationToken);
    }
}
