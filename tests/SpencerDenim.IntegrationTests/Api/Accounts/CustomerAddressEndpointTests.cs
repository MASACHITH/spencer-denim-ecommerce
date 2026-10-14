using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Domain.Identity;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Accounts;

/// <summary>
/// The address book over HTTP (ACC-02, doc 11 P11 step 11, <c>identity.CustomerAddresses</c>).
/// </summary>
public sealed class CustomerAddressEndpointTests(SqlServerDatabaseFixture sqlServer)
    : CustomerAccountTestBase(sqlServer)
{
    private const string AddressesUrl = "/api/account/addresses";

    /// <summary>
    /// The first address a customer saves is their default, whatever the form said.
    /// </summary>
    /// <remarks>
    /// <b>A book with one entry and no default leaves checkout nothing to pre-select</b>, and nothing to
    /// explain to the shopper either. Doc 11 P11 step 11 asks for "one default"; it does not ask the
    /// shopper to opt in to having one.
    /// </remarks>
    [Fact]
    public async Task The_first_address_saved_becomes_the_default_even_when_the_box_is_unticked()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("address-first");

        await RegisterAsync(client, email);
        await SignInAsync(client, email);

        using var response = await client.PostAsJsonAsync(AddressesUrl, Address(isDefault: false), Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var book = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(1, book.GetArrayLength());
        Assert.True(book[0].GetProperty("isDefault").GetBoolean());
    }

    /// <summary>
    /// Doc 11 P11 step 11: "sets one default", and the database is what makes it one.
    /// </summary>
    /// <remarks>
    /// <b>The whole book is read back and every row's flag is checked</b>, not just the new default's.
    /// <c>UX_CustomerAddresses_UserId_Default</c> would refuse a second <c>IsDefault = 1</c> row outright,
    /// so the failure this guards against is the other one — clearing the old default and then failing to
    /// set the new one, which leaves a book with no default at all and no error.
    /// </remarks>
    [Fact]
    public async Task Setting_a_default_moves_it_and_leaves_exactly_one()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("address-default");

        await RegisterAsync(client, email);
        await SignInAsync(client, email);

        var ids = new List<int>();

        for (var index = 0; index < 3; index++)
        {
            using var created = await client.PostAsJsonAsync(
                AddressesUrl,
                Address(isDefault: false, label: $"Address {index}"),
                Token);

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var book = await created.Content.ReadFromJsonAsync<JsonElement>(Token);
            ids = book.EnumerateArray().Select(row => row.GetProperty("id").GetInt32()).ToList();
        }

        Assert.Equal(3, ids.Count);

        // The oldest, which is the one that became the default on the first save; moving to it from a
        // later one is the interesting direction.
        var target = ids.Min();

        using var moved = await client.PostAsync(
            new Uri($"{AddressesUrl}/{target}/default", UriKind.Relative),
            null,
            Token);

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        var after = await moved.Content.ReadFromJsonAsync<JsonElement>(Token);
        var defaults = after.EnumerateArray()
            .Where(row => row.GetProperty("isDefault").GetBoolean())
            .Select(row => row.GetProperty("id").GetInt32())
            .ToList();

        Assert.Equal<IEnumerable<int>>([target], defaults);

        // Default first, which is the order the screen draws (ICustomerAddressStore.ListAsync).
        Assert.Equal(target, after[0].GetProperty("id").GetInt32());
    }

    /// <summary>
    /// Doc 08 §3.1's app cap of ten, and the 409 that says so.
    /// </summary>
    [Fact]
    public async Task The_eleventh_address_is_refused_with_the_limit()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("address-cap");

        await RegisterAsync(client, email);
        await SignInAsync(client, email);

        for (var index = 0; index < CustomerAddress.MaxPerCustomer; index++)
        {
            using var created = await client.PostAsJsonAsync(
                AddressesUrl,
                Address(isDefault: false, label: $"Address {index}"),
                Token);

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using var refused = await client.PostAsJsonAsync(AddressesUrl, Address(isDefault: false), Token);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var body = await refused.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal("address_limit_reached", body.GetProperty("code").GetString());
        Assert.Equal(CustomerAddress.MaxPerCustomer, body.GetProperty("limit").GetInt32());

        var customer = await ReadCustomerAsync(factory, email);
        Assert.NotNull(customer);
        Assert.Equal(CustomerAddress.MaxPerCustomer, await CountAddressesAsync(customer.Id));
    }

    /// <summary>
    /// Doc 08 §3.1's cap, under contention: four tabs adding an eleventh address together still leave
    /// ten.
    /// </summary>
    /// <remarks>
    /// <b>SQL Server cannot express "at most ten rows per owner" declaratively</b>, so the cap is a count
    /// and an insert in one transaction behind an application lock on the owner. A cap that holds except
    /// when somebody is quick is not a cap, and a test that added addresses one at a time could not tell
    /// the two apart.
    /// <para>
    /// <b>This test is the reason the lock exists.</b> Removing
    /// <c>sp_getapplock</c> from <c>CustomerAddressStore.InTransactionAsync</c> makes it red — recorded in
    /// the task report as one of the mutations.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Four_tabs_adding_an_eleventh_address_together_still_leave_ten()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var setup = CreateClient(factory);
        var email = UniqueEmail("address-cap-race");

        await RegisterAsync(setup, email);
        await SignInAsync(setup, email);

        for (var index = 0; index < CustomerAddress.MaxPerCustomer - 1; index++)
        {
            using var created = await setup.PostAsJsonAsync(
                AddressesUrl,
                Address(isDefault: false, label: $"Address {index}"),
                Token);

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var customer = await ReadCustomerAsync(factory, email);
        Assert.NotNull(customer);
        Assert.Equal(CustomerAddress.MaxPerCustomer - 1, await CountAddressesAsync(customer.Id));

        // Four tabs of the same browser: four clients, each signed in as the same customer.
        var tabs = new List<HttpClient>();

        try
        {
            for (var index = 0; index < 4; index++)
            {
                var tab = CreateClient(factory);
                await SignInAsync(tab, email);
                tabs.Add(tab);
            }

            var statuses = await Task.WhenAll(tabs.Select(async tab =>
            {
                using var response = await tab.PostAsJsonAsync(
                    AddressesUrl,
                    Address(isDefault: false, label: "Race"),
                    Token);

                return response.StatusCode;
            }));

            Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.Created));
            Assert.Equal(3, statuses.Count(status => status == HttpStatusCode.Conflict));
        }
        finally
        {
            foreach (var tab in tabs)
            {
                tab.Dispose();
            }
        }

        Assert.Equal(CustomerAddress.MaxPerCustomer, await CountAddressesAsync(customer.Id));
    }

    /// <summary>Deleting the default promotes another, so a book is never left without one.</summary>
    [Fact]
    public async Task Deleting_the_default_promotes_another()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("address-delete-default");

        await RegisterAsync(client, email);
        await SignInAsync(client, email);

        using var first = await client.PostAsJsonAsync(AddressesUrl, Address(isDefault: true, label: "One"), Token);
        _ = first;

        using var second = await client.PostAsJsonAsync(AddressesUrl, Address(isDefault: false, label: "Two"), Token);
        var book = await second.Content.ReadFromJsonAsync<JsonElement>(Token);

        var defaultId = book.EnumerateArray()
            .First(row => row.GetProperty("isDefault").GetBoolean())
            .GetProperty("id").GetInt32();

        using var deleted = await client.DeleteAsync(
            new Uri($"{AddressesUrl}/{defaultId}", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        var after = await deleted.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(1, after.GetArrayLength());
        Assert.True(after[0].GetProperty("isDefault").GetBoolean());
    }

    /// <summary>
    /// One customer cannot read, edit, delete or re-default another's address — and the refusal says
    /// nothing about whether the id exists.
    /// </summary>
    /// <remarks>
    /// <b>This is the test that matters most in this file.</b> Every statement in
    /// <c>CustomerAddressStore</c> filters on <c>UserId</c> in its own <c>WHERE</c> clause rather than
    /// checking the owner first, and this is what says so from outside. The <i>same</i> id is used — a
    /// real address belonging to somebody else — so a missing filter would succeed rather than 404 by
    /// accident.
    /// </remarks>
    [Fact]
    public async Task One_customer_cannot_touch_anothers_address()
    {
        await ReadyAsync();

        await using var factory = CreateHost();

        using var owner = CreateClient(factory);
        var ownerEmail = UniqueEmail("address-owner");
        await RegisterAsync(owner, ownerEmail);
        await SignInAsync(owner, ownerEmail);

        using var created = await owner.PostAsJsonAsync(AddressesUrl, Address(isDefault: true), Token);
        var book = await created.Content.ReadFromJsonAsync<JsonElement>(Token);
        var addressId = book[0].GetProperty("id").GetInt32();

        using var stranger = CreateClient(factory);
        var strangerEmail = UniqueEmail("address-stranger");
        await RegisterAsync(stranger, strangerEmail);
        await SignInAsync(stranger, strangerEmail);

        // Not in their book at all.
        using var theirBook = await stranger.GetAsync(new Uri(AddressesUrl, UriKind.Relative), Token);
        var strangerBook = await theirBook.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(0, strangerBook.GetArrayLength());

        using var edited = await stranger.PutAsJsonAsync(
            $"{AddressesUrl}/{addressId}",
            Address(isDefault: false, recipient: "Somebody Else"),
            Token);

        Assert.Equal(HttpStatusCode.NotFound, edited.StatusCode);

        using var reDefaulted = await stranger.PostAsync(
            new Uri($"{AddressesUrl}/{addressId}/default", UriKind.Relative),
            null,
            Token);

        Assert.Equal(HttpStatusCode.NotFound, reDefaulted.StatusCode);

        using var removed = await stranger.DeleteAsync(
            new Uri($"{AddressesUrl}/{addressId}", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);

        // And nothing happened to it.
        using var ownerBook = await owner.GetAsync(new Uri(AddressesUrl, UriKind.Relative), Token);
        var stillThere = await ownerBook.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(1, stillThere.GetArrayLength());
        Assert.Equal("Nimal Perera", stillThere[0].GetProperty("recipientName").GetString());
        Assert.True(stillThere[0].GetProperty("isDefault").GetBoolean());
    }

    /// <summary>The address form's field rules, as a 400 naming each field.</summary>
    [Fact]
    public async Task The_address_form_names_every_missing_field_at_once()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("address-validation");

        await RegisterAsync(client, email);
        await SignInAsync(client, email);

        using var response = await client.PostAsJsonAsync(AddressesUrl, new { }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        var errors = body.GetProperty("errors");

        // Every required field, one message each, in one answer — PlaceOrderValidator's discipline: "a
        // request missing six fields must come back with six messages rather than the first one".
        foreach (var field in new[] { "recipientName", "phone", "line1", "city", "districtId" })
        {
            Assert.True(errors.TryGetProperty(field, out _), $"The 400 did not name {field}: {body}");
        }
    }

    /// <summary>A length that exceeds the column is refused, with the column's own number.</summary>
    /// <remarks>
    /// <b>#126's defect, from the other side.</b> A <c>maxlength</c> of 120 against a 100-character column
    /// passed the form, the stub and sixteen browser tests. The widths here come from
    /// <see cref="CustomerAddress"/>'s constants, which is the same place the EF configuration reads
    /// them, so a column change that the validator did not follow fails here rather than at the database.
    /// </remarks>
    [Fact]
    public async Task A_value_longer_than_its_column_is_refused_rather_than_truncated()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("address-too-long");

        await RegisterAsync(client, email);
        await SignInAsync(client, email);

        using var response = await client.PostAsJsonAsync(
            AddressesUrl,
            Address(isDefault: false, recipient: new string('x', CustomerAddress.RecipientNameMaxLength + 1)),
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        var message = body.GetProperty("errors").GetProperty("recipientName")[0].GetString();

        Assert.Contains(
            CustomerAddress.RecipientNameMaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            message,
            StringComparison.Ordinal);
    }

    /// <summary>A district the shop does not serve is refused, named, and not stored.</summary>
    [Fact]
    public async Task An_unknown_district_is_refused_and_named()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("address-district");

        await RegisterAsync(client, email);
        await SignInAsync(client, email);

        using var response = await client.PostAsJsonAsync(
            AddressesUrl,
            Address(isDefault: false, districtId: 9999),
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.True(body.GetProperty("errors").TryGetProperty("districtId", out _));

        var customer = await ReadCustomerAsync(factory, email);
        Assert.NotNull(customer);
        Assert.Equal(0, await CountAddressesAsync(customer.Id));
    }

    /// <summary>
    /// Doc 08 §3.1's [A] tag: the audit columns are stamped, and the customer is recorded as the author
    /// of their own address.
    /// </summary>
    /// <remarks>
    /// <b>Asserted because the write is set-based in one place and tracked in another.</b>
    /// <c>CustomerProfileStore</c> writes the user row with raw SQL and therefore stamps
    /// <c>UpdatedAtUtc</c> by hand; the address store saves a tracked entity and lets
    /// <c>AuditAndSoftDeleteInterceptor</c> do it. Issue #18 was exactly this going unstamped on
    /// <c>identity.Users</c> for a milestone, so the one that relies on the interceptor is worth pinning.
    /// </remarks>
    [Fact]
    public async Task A_saved_address_carries_its_audit_columns()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("address-audit");

        await RegisterAsync(client, email);
        await SignInAsync(client, email);

        using var created = await client.PostAsJsonAsync(AddressesUrl, Address(isDefault: true), Token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var customer = await ReadCustomerAsync(factory, email);
        Assert.NotNull(customer);

        DbContext.ChangeTracker.Clear();

        var row = await DbContext.CustomerAddresses
            .AsNoTracking()
            .Where(address => address.UserId == customer.Id)
            .Select(address => new { address.CreatedAtUtc, address.CreatedByUserId })
            .SingleAsync(Token);

        Assert.True(row.CreatedAtUtc > DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(customer.Id, row.CreatedByUserId);
    }

    private async Task<int> CountAddressesAsync(int userId)
    {
        DbContext.ChangeTracker.Clear();

        return await DbContext.CustomerAddresses
            .AsNoTracking()
            .CountAsync(address => address.UserId == userId, Token);
    }

    /// <summary>
    /// A valid address body. District 1 is Colombo, seeded 1–25 with no identity (doc 08 §3.1).
    /// </summary>
    private static object Address(
        bool isDefault,
        string? label = "Home",
        string recipient = "Nimal Perera",
        int districtId = 1) =>
        new
        {
            label,
            recipientName = recipient,
            phone = "+94771234567",
            line1 = "221 Galle Road",
            line2 = (string?)null,
            city = "Colombo",
            districtId,
            postalCode = "00300",
            landmark = (string?)null,
            isDefault,
        };
}
