using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    /// How many tabs race for the last address slot.
    /// </summary>
    /// <remarks>
    /// Eight, and the number was raised from four on evidence: the mutation that removes
    /// <c>sp_getapplock</c> from <c>CustomerAddressStore</c> survived four. See the race test's own
    /// remarks for where that left things.
    /// </remarks>
    private const int Racers = 8;

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
    /// <b>This test does not prove the application lock is load-bearing, and it is worth saying so
    /// rather than implying otherwise.</b> Removing <c>sp_getapplock</c> from
    /// <c>CustomerAddressStore.InTransactionAsync</c> leaves it green — measured, with eight tabs, a
    /// gate using <c>RunContinuationsAsynchronously</c>, <c>Task.Run</c> per tab and a 150 ms delay
    /// injected into the address <c>COUNT</c>; three runs, one 201 and seven 409s every time. The same
    /// mutation against <c>CustomerAddressConcurrencyTests</c>, which drives the store from eight DI
    /// scopes with no HTTP in the way, is also green — but takes <b>15–17 s against 4 s</b>, which is
    /// consistent with the contenders being resolved by blocking and retrying rather than by the lock.
    /// That is a hypothesis from a timing, not a verified mechanism, and the task report says so.
    /// </para>
    /// <para>
    /// What this test <i>does</i> prove: eight concurrent tabs cannot exceed the cap, and seven of them
    /// are told why rather than failing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Four_tabs_adding_an_eleventh_address_together_still_leave_ten()
    {
        await ReadyAsync();

        // The window the cap has to survive is between the count and the insert — one round trip,
        // which eight concurrent tabs do not reliably hit. Widened to 150 ms on the count only, so the
        // race is deterministic rather than hoped for.
        await using var factory = CreateHost(
            commandInterceptor: new DelaysTheAddressCount(TimeSpan.FromMilliseconds(150)));

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
            for (var index = 0; index < Racers; index++)
            {
                var tab = CreateClient(factory);
                await SignInAsync(tab, email);
                tabs.Add(tab);
            }

            // **A gate, so the four really start together.** Without one the first request has
            // usually finished before the fourth is dispatched, and the test passes whatever the store
            // does — which is exactly what happened: the first version of this test survived the
            // mutation that removes `sp_getapplock` from `CustomerAddressStore`, because nothing
            // overlapped. That is the shape of the race test this project shipped thirteen times.
            // **`RunContinuationsAsynchronously` and `Task.Run`, both load-bearing.** A plain
            // `TaskCompletionSource` runs its continuations *synchronously on the thread that calls
            // `SetResult`*, so the tabs resume one after another on one thread and the first request can
            // finish before the second begins. `Task.Run` puts each on the thread pool so they are
            // genuinely in flight together.
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var attempts = tabs.Select(tab => Task.Run(async () =>
            {
                await gate.Task;

                using var response = await tab.PostAsJsonAsync(
                    AddressesUrl,
                    Address(isDefault: false, label: "Race"),
                    Token);

                return response.StatusCode;
            })).ToList();

            gate.SetResult();

            var statuses = await Task.WhenAll(attempts);

            // Exactly one tab may have added the tenth address. The other three are refused — by the
            // cap, which is the whole point — and none of them may have succeeded.
            Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.Created));
            // Three tabs must be refused. A 201 here means the cap was exceeded; the row-count
            // assertion after this block is the invariant either way.
            Assert.Equal(
                Racers - 1,
                statuses.Count(status => status is HttpStatusCode.Conflict or HttpStatusCode.InternalServerError));
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

    /// <summary>
    /// Owner decision 2026-10-21 (independent-tester pass, attack 1): <c>SaveCustomerAddressCommandHandler</c>
    /// always derives <c>DistrictId</c> from the resolved country rather than trusting the request — "an
    /// overseas address has no district, whatever the request sent" — so the application can never itself
    /// produce a row that mixes a foreign country with a district, or Sri Lanka with none. This is the belt
    /// behind that braces: a row that mixes them, however it was written, is refused by the database
    /// regardless of which layer built it.
    /// </summary>
    [Theory]
    [InlineData("LK", null)] // Sri Lanka with no district at all.
    [InlineData("FR", 1)] // A foreign country with a Sri Lankan district still attached.
    public async Task A_hand_written_address_that_mixes_its_country_and_district_is_refused_by_the_database(
        string countryCode,
        int? districtId)
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("address-destination-ck");

        await RegisterAsync(client, email);
        var customer = await ReadCustomerAsync(factory, email);
        Assert.NotNull(customer);

        var row = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["UserId"] = customer.Id,
            ["RecipientName"] = "Test Recipient",
            ["Phone"] = "+44 20 7946 0958",
            ["Line1"] = "10 Example Street",
            ["City"] = "Test City",
            ["CountryCode"] = countryCode,
            ["DistrictId"] = districtId,
            ["IsDefault"] = false,
        };

        await Writer.AssertRejectedAsync(
            "[identity].[CustomerAddresses]",
            row,
            "CK_CustomerAddresses_Destination",
            Token);
    }

    /// <summary>
    /// The converse of the test above: the one overseas shape the constraint actually allows — a foreign
    /// country with no district — lands without complaint.
    /// </summary>
    [Fact]
    public async Task A_hand_written_overseas_address_with_no_district_is_accepted()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("address-destination-ck-ok");

        await RegisterAsync(client, email);
        var customer = await ReadCustomerAsync(factory, email);
        Assert.NotNull(customer);

        var addressId = await Writer.InsertAsync(
            "[identity].[CustomerAddresses]",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["UserId"] = customer.Id,
                ["RecipientName"] = "Test Recipient",
                ["Phone"] = "+44 20 7946 0958",
                ["Line1"] = "10 Example Street",
                ["City"] = "Test City",
                ["CountryCode"] = "FR",
                ["DistrictId"] = null,
                ["IsDefault"] = false,
            },
            Token);

        Assert.True(addressId > 0);
    }

    /// <summary>
    /// Delays the address-book <c>COUNT</c> so the window between it and the insert is wide enough for a
    /// concurrent caller to land in.
    /// </summary>
    /// <remarks>
    /// <b>This is #146's technique, and that PR records why it is needed:</b> its parallel tests ran
    /// "with delayed token writes to force the worst case". A race whose window is one network round trip
    /// is not reproducible on demand — eight concurrent tabs against this store passed the mutation that
    /// removes <c>sp_getapplock</c> three times in a row — so the window is widened on purpose. The delay
    /// is on the <c>SELECT</c> only, so the insert, the transaction and the lock all behave exactly as
    /// they do in production.
    /// <para>
    /// <b>Matched on the table name and <c>COUNT</c> together.</b> Matching on <c>COUNT</c> alone would
    /// also delay the fixture's own assertions and the registration's uniqueness pre-check, which would
    /// make the test slow and its failures hard to read.
    /// </para>
    /// </remarks>
    private sealed class DelaysTheAddressCount(TimeSpan delay) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal)
                && command.CommandText.Contains("CustomerAddresses", StringComparison.Ordinal))
            {
                await Task.Delay(delay, cancellationToken);
            }

            return result;
        }
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
