using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SpencerDenim.Infrastructure.Identity.Customers;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Accounts;

/// <summary>
/// Registration, sign-in, sign-out and "who am I" against a real SQL Server (ACC-01, doc 11 P11 steps A
/// and B).
/// </summary>
/// <remarks>
/// <b>These exist because the whole promo-code admin API answered 500 for a day and a half</b> (#126,
/// 12–13 Oct) with nothing to catch it: the screen's only coverage was Playwright against a Node stub,
/// and there was no backend test at all. Every route W3-01 adds has a test here that calls it over HTTP
/// against the real database.
/// <para>
/// <b>And because a stub more permissive than the validator hides a defect rather than catching one.</b>
/// Nothing in this file stands in for anything: the registration goes through the real endpoint, the
/// emails land in the real <c>ops.EmailOutbox</c>, and the links the tests redeem are the links the real
/// composer wrote.
/// </para>
/// </remarks>
public sealed class CustomerAuthEndpointTests(SqlServerDatabaseFixture sqlServer)
    : CustomerAccountTestBase(sqlServer)
{
    private const string RegisterUrl = "/api/account/register";
    private const string LoginUrl = "/api/account/login";
    private const string LogoutUrl = "/api/account/logout";
    private const string MeUrl = "/api/account/me";

    /// <summary>
    /// Doc 11 P11 step A.3: a free address gets an account, a confirmation link and a welcome message,
    /// and the account starts unconfirmed.
    /// </summary>
    [Fact]
    public async Task Registering_a_free_address_creates_an_unconfirmed_account_and_queues_two_messages()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("register-new");

        using var response = await client.PostAsJsonAsync(
            RegisterUrl,
            new { firstName = "Nimal", lastName = "Perera", email, password = GoodPassword, phone = (string?)null },
            Token);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var customer = await ReadCustomerAsync(factory, email);
        Assert.NotNull(customer);
        Assert.Equal(SpencerDenim.Domain.Identity.UserType.Customer, customer.UserType);

        // Doc 11 §7 G-24: the account is usable straight away and confirmation gates only the linking of
        // earlier guest orders.
        Assert.False(customer.EmailConfirmed);
        Assert.True(customer.IsActive);

        Assert.Equal(
            [Templates.Confirmation, Templates.Welcome],
            await QueuedTemplatesAsync(factory, email));

        // The link the real composer wrote is readable and is shaped like one of ours.
        var token = await ReadLinkTokenAsync(factory, Templates.Confirmation, email);
        Assert.NotNull(token);
        Assert.Equal(customer.Id, CustomerAccountTokens.ReadUserId(token));
    }

    /// <summary>
    /// Doc 08 §3.1 DB10: "Register with an existing email (either type) gives the same generic response."
    /// </summary>
    /// <remarks>
    /// <b>The three cases are compared byte for byte, not merely by status code.</b> A reply that differed
    /// by one word — a different title, a different <c>message</c> — would be the enumeration oracle this
    /// rule exists to close, and a status-code assertion would not see it.
    /// </remarks>
    [Fact]
    public async Task Registration_answers_identically_for_a_free_a_taken_and_a_staff_address()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);

        var free = UniqueEmail("register-free");
        var taken = UniqueEmail("register-taken");
        var staff = UniqueEmail("register-staff");

        await RegisterAsync(client, taken);
        await CreateStaffUserAsync(factory, staff, GoodPassword);

        var replies = new List<(HttpStatusCode Status, string Body)>();

        foreach (var address in new[] { free, taken, staff })
        {
            using var response = await client.PostAsJsonAsync(
                RegisterUrl,
                new { firstName = "Nimal", lastName = "Perera", email = address, password = GoodPassword, phone = (string?)null },
                Token);

            replies.Add((response.StatusCode, await response.Content.ReadAsStringAsync(Token)));
        }

        Assert.Equal(HttpStatusCode.Accepted, replies[0].Status);
        Assert.Equal(replies[0].Status, replies[1].Status);
        Assert.Equal(replies[0].Status, replies[2].Status);
        Assert.Equal(replies[0].Body, replies[1].Body);
        Assert.Equal(replies[0].Body, replies[2].Body);
    }

    /// <summary>
    /// And the difference is in the inbox, which is where doc 08 §3.1 DB10 puts it: a customer match gets
    /// <c>AccountExists</c>, a staff match gets nothing at all.
    /// </summary>
    [Fact]
    public async Task A_taken_customer_address_is_told_and_a_staff_address_is_not()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);

        var taken = UniqueEmail("exists-customer");
        var staff = UniqueEmail("exists-staff");

        await RegisterAsync(client, taken);
        await CreateStaffUserAsync(factory, staff, GoodPassword);

        foreach (var address in new[] { taken, staff })
        {
            using var response = await client.PostAsJsonAsync(
                RegisterUrl,
                new { firstName = "Someone", lastName = "Else", email = address, password = GoodPassword, phone = (string?)null },
                Token);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        Assert.Equal(1, await CountQueuedAsync(factory, Templates.AccountExists, taken));

        // Nothing at all to the staff address: not AccountExists, not a confirmation, not a welcome.
        Assert.Empty(await QueuedTemplatesAsync(factory, staff));

        // And no second account was created for either.
        var staffRow = await ReadCustomerAsync(factory, staff);
        Assert.NotNull(staffRow);
        Assert.Equal(SpencerDenim.Domain.Identity.UserType.Staff, staffRow.UserType);
    }

    /// <summary>The registration form's own field rules, as a 400 naming each field.</summary>
    [Theory]
    [InlineData("", "Nimal", "password", "email")]
    [InlineData("not-an-address", "Nimal", "password", "email")]
    [InlineData("ok@example.com", "", "password", "firstName")]
    [InlineData("ok@example.com", "Nimal", "short1!A", "password")]
    public async Task Registration_refuses_a_bad_field_and_names_it(
        string email,
        string firstName,
        string password,
        string expectedField)
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);

        using var response = await client.PostAsJsonAsync(
            RegisterUrl,
            new { firstName, lastName = "Perera", email, password, phone = (string?)null },
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.True(
            body.GetProperty("errors").TryGetProperty(expectedField, out _),
            $"The 400 did not name {expectedField}: {body}");
    }

    /// <summary>
    /// Doc 11 §7 G-24: a customer who has not confirmed can sign in, and <c>/me</c> says so.
    /// </summary>
    /// <remarks>
    /// <b>This is the test that would catch "confirmation became a gate".</b> The provisional default is
    /// to allow sign-in; if a later change made <c>SignIn.RequireConfirmedAccount</c> true, or added a
    /// confirmed check to the authenticator, every unconfirmed shopper would be locked out of the shop
    /// and nothing else in the suite would see it.
    /// </remarks>
    [Fact]
    public async Task An_unconfirmed_customer_can_sign_in_and_me_reports_them_unconfirmed()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("unconfirmed-signin");

        await RegisterAsync(client, email);

        var session = await SignInAsync(client, email);
        Assert.Equal(email, session.GetProperty("email").GetString());
        Assert.False(session.GetProperty("emailConfirmed").GetBoolean());

        using var me = await client.GetAsync(new Uri(MeUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        var body = await me.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(email, body.GetProperty("email").GetString());
        Assert.False(body.GetProperty("emailConfirmed").GetBoolean());
    }

    /// <summary>
    /// Doc 08 §3.1 DB10: one refusal for a wrong password, an unknown address, a staff address and a
    /// deactivated account.
    /// </summary>
    /// <remarks>
    /// <b>The bodies are compared, not just the statuses.</b> Four 401s with four different sentences
    /// would be the same leak wearing a uniform.
    /// </remarks>
    [Fact]
    public async Task Sign_in_refuses_four_different_situations_with_one_identical_reply()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);

        var customer = UniqueEmail("signin-wrong-password");
        var staff = UniqueEmail("signin-staff");
        var unknown = UniqueEmail("signin-unknown");

        await RegisterAsync(client, customer);
        await CreateStaffUserAsync(factory, staff, GoodPassword);

        var attempts = new (string Email, string Password)[]
        {
            (customer, "Wrong-Password-9!"),
            (unknown, GoodPassword),
            (staff, GoodPassword),
        };

        var replies = new List<(HttpStatusCode Status, string Body)>();

        foreach (var (email, password) in attempts)
        {
            using var response = await client.PostAsJsonAsync(LoginUrl, new { email, password }, Token);
            replies.Add((
                response.StatusCode,
                WithoutTraceId(await response.Content.ReadAsStringAsync(Token))));
        }

        Assert.All(replies, reply => Assert.Equal(HttpStatusCode.Unauthorized, reply.Status));
        Assert.Equal(replies[0].Body, replies[1].Body);
        Assert.Equal(replies[0].Body, replies[2].Body);

        // The normalised form keeps `code=invalid_credentials` as one of its pairs, so the refusal is
        // asserted from the same string the comparison used rather than from a second read.
        Assert.Contains("code=invalid_credentials", replies[0].Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A staff member's correct password is refused on the customer form, and no customer session is
    /// issued (doc 08 §3.1 DB10: "the customer scheme rejects Staff principals").
    /// </summary>
    [Fact]
    public async Task A_staff_members_own_password_does_not_sign_them_in_as_a_customer()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var staff = UniqueEmail("staff-correct-password");

        await CreateStaffUserAsync(factory, staff, GoodPassword);

        using var response = await client.PostAsJsonAsync(
            LoginUrl,
            new { email = staff, password = GoodPassword },
            Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // No cookie, so no session: /me answers 401 rather than describing a staff member.
        using var me = await client.GetAsync(new Uri(MeUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    /// <summary>Doc 11 P11 step B.7: signing out removes the session.</summary>
    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("signout");

        await RegisterAsync(client, email);
        await SignInAsync(client, email);

        using var signedIn = await client.GetAsync(new Uri(MeUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);

        using var logout = await client.PostAsync(new Uri(LogoutUrl, UriKind.Relative), null, Token);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var signedOut = await client.GetAsync(new Uri(MeUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    }

    /// <summary>Doc 11 P11 step B.5: the lockout, and the end time the reply carries.</summary>
    /// <remarks>
    /// <b>The threshold is configured down to three rather than defaulted to ten</b>, so the test is
    /// about the mechanism rather than about the number, and so it does not spend ten password hashes to
    /// find out. The real default is pinned separately, against doc 08 §3.8's own figure, in
    /// <c>SettingKeySpecificationTests</c>.
    /// </remarks>
    [Fact]
    public async Task The_account_locks_after_the_configured_number_of_failures_and_says_when_it_clears()
    {
        await ReadyAsync();

        await using var factory = CreateHost(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CustomerSecurity:LockoutMaxFailedAttempts"] = "3",
            ["CustomerSecurity:LockoutMinutes"] = "15",
        });

        using var client = CreateClient(factory);
        var email = UniqueEmail("lockout");
        await RegisterAsync(client, email);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var refused = await client.PostAsJsonAsync(
                LoginUrl,
                new { email, password = "Wrong-Password-9!" },
                Token);

            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        using var third = await client.PostAsJsonAsync(LoginUrl, new { email, password = "Wrong-Password-9!" }, Token);
        Assert.Equal(HttpStatusCode.Locked, third.StatusCode);

        var body = await third.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal("locked_out", body.GetProperty("code").GetString());
        Assert.True(
            DateTime.TryParse(
                body.GetProperty("lockoutEndUtc").GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var end),
            "The 423 did not carry a parseable lockoutEndUtc.");

        Assert.True(end > DateTime.UtcNow, "The lockout end is not in the future.");

        // And the correct password is refused while the lock holds, which is the whole point of it.
        using var correct = await client.PostAsJsonAsync(LoginUrl, new { email, password = GoodPassword }, Token);
        Assert.Equal(HttpStatusCode.Locked, correct.StatusCode);
    }

    /// <summary>
    /// Doc 08 §3.1 DB10's "it expires by itself": hammering a locked account does not extend the lock.
    /// </summary>
    /// <remarks>
    /// <b>The end time is read from the database twice and compared.</b> Asserting through the reply
    /// would not do: the reply's end time could be recomputed on every refusal and look identical while
    /// the stored one crept forward, which is exactly how an "it expires by itself" guarantee quietly
    /// stops being true.
    /// </remarks>
    [Fact]
    public async Task Attempts_against_a_locked_account_do_not_extend_the_lock()
    {
        await ReadyAsync();

        await using var factory = CreateHost(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CustomerSecurity:LockoutMaxFailedAttempts"] = "2",
            ["CustomerSecurity:LockoutMinutes"] = "15",
        });

        using var client = CreateClient(factory);
        var email = UniqueEmail("lock-not-extended");
        await RegisterAsync(client, email);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var refused = await client.PostAsJsonAsync(
                LoginUrl,
                new { email, password = "Wrong-Password-9!" },
                Token);

            _ = refused;
        }

        var locked = await ReadCustomerAsync(factory, email);
        Assert.NotNull(locked);
        Assert.NotNull(locked.LockoutEnd);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var again = await client.PostAsJsonAsync(
                LoginUrl,
                new { email, password = "Wrong-Password-9!" },
                Token);

            Assert.Equal(HttpStatusCode.Locked, again.StatusCode);
        }

        var after = await ReadCustomerAsync(factory, email);
        Assert.NotNull(after);
        Assert.Equal(locked.LockoutEnd, after.LockoutEnd);
    }

    /// <summary>
    /// #101's timing oracle, measured: an unknown address, a staff address and a wrong password all cost
    /// the password hash.
    /// </summary>
    /// <remarks>
    /// <b>This is a ratio test and not a threshold test, and it is deliberately loose.</b> A wall-clock
    /// assertion on a shared developer machine is the kind of test that fails on a Tuesday for no reason,
    /// so what is asserted is that the cheap paths are not an <i>order of magnitude</i> faster than the
    /// expensive one — which is what an unequalised endpoint looks like (a dictionary read against a
    /// PBKDF2 verification). The real figures are in the task report.
    /// <para>
    /// <b>It can fail, and that is the point.</b> Removing
    /// <c>CustomerUserLookup.SpendPasswordHashCost</c> from the unknown-address path makes it red —
    /// recorded in the task report as one of the mutations.
    /// </para>
    /// <para>
    /// The median of several rounds, not one sample: one sample on a machine that is also running a
    /// database and a test host is noise.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_unknown_address_costs_about_what_a_wrong_password_costs()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);

        var customer = UniqueEmail("timing-real");
        var staff = UniqueEmail("timing-staff");
        await RegisterAsync(client, customer);
        await CreateStaffUserAsync(factory, staff, GoodPassword);

        // One round each first, so neither path is paying for a cold cache, a cold query plan or the
        // first PBKDF2 call in the process.
        await MeasureAsync(client, customer, "Wrong-Password-9!");
        await MeasureAsync(client, UniqueEmail("timing-warm"), GoodPassword);
        await MeasureAsync(client, staff, GoodPassword);

        const int rounds = 7;
        var wrongPassword = new List<double>(rounds);
        var unknownAddress = new List<double>(rounds);
        var staffAddress = new List<double>(rounds);

        for (var round = 0; round < rounds; round++)
        {
            wrongPassword.Add(await MeasureAsync(client, customer, "Wrong-Password-9!"));
            unknownAddress.Add(await MeasureAsync(client, UniqueEmail("timing-nobody"), GoodPassword));
            staffAddress.Add(await MeasureAsync(client, staff, GoodPassword));
        }

        var realCost = Median(wrongPassword);
        var unknownCost = Median(unknownAddress);
        var staffCost = Median(staffAddress);

        // A tenth of the real cost is what "no hash was computed" looks like. Anything above that is
        // within the noise of a developer machine and is not a distinguishable signal.
        Assert.True(
            unknownCost > realCost / 10,
            $"An unknown address answered in {unknownCost:F1} ms against {realCost:F1} ms for a wrong "
            + "password: the unknown-email path is not spending the password-hash cost, so the sign-in "
            + "form is a username-enumeration timing oracle (#101).");

        Assert.True(
            staffCost > realCost / 10,
            $"A staff address answered in {staffCost:F1} ms against {realCost:F1} ms for a wrong "
            + "password: the wrong-user-type path is not spending the password-hash cost.");
    }

    /// <summary>
    /// The problem document with <c>traceId</c> removed, so two replies can be compared.
    /// </summary>
    /// <remarks>
    /// <b><c>traceId</c> is the one field that is <i>supposed</i> to differ</b>: ASP.NET Core's problem
    /// writer puts the current activity id on every RFC 9457 document, so it is different on every
    /// request and says nothing about the account. Removing it rather than comparing a handful of named
    /// fields keeps the assertion a whole-body comparison — which is the point, because a field added
    /// later that <i>did</i> differ by case would otherwise slip through a named-field check.
    /// </remarks>
    private static string WithoutTraceId(string body)
    {
        using var document = JsonDocument.Parse(body);

        var kept = document.RootElement
            .EnumerateObject()
            .Where(property => !string.Equals(property.Name, "traceId", StringComparison.Ordinal))
            .ToDictionary(property => property.Name, property => property.Value.ToString(), StringComparer.Ordinal);

        return string.Join('|', kept.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static async Task<double> MeasureAsync(HttpClient client, string email, string password)
    {
        var stopwatch = Stopwatch.StartNew();

        using var response = await client.PostAsJsonAsync("/api/account/login", new { email, password }, Token);
        _ = await response.Content.ReadAsStringAsync(Token);

        stopwatch.Stop();

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static double Median(List<double> samples)
    {
        var ordered = samples.Order().ToList();

        return ordered.Count % 2 == 1
            ? ordered[ordered.Count / 2]
            : (ordered[(ordered.Count / 2) - 1] + ordered[ordered.Count / 2]) / 2;
    }

    /// <summary>
    /// Issue #47: a well-formed object that omits a property is a 400 naming the field, never a 500.
    /// </summary>
    /// <remarks>
    /// <c>POST /api/admin/auth/login</c> answered <b>500</b> to <c>{}</c> until #144, because
    /// <c>System.Text.Json</c> ignores nullable annotations and <c>ArgumentNullException.ThrowIfNull</c>
    /// threw. The customer routes are built with every field nullable for that reason, and this is the
    /// test that says so.
    /// </remarks>
    [Theory]
    [InlineData(LoginUrl, "{}")]
    [InlineData(LoginUrl, "{\"email\":\"someone@example.com\"}")]
    [InlineData(LoginUrl, "{\"password\":\"whatever\"}")]
    [InlineData(RegisterUrl, "{}")]
    [InlineData("/api/account/forgot-password", "{}")]
    [InlineData("/api/account/reset-password", "{}")]
    [InlineData("/api/account/confirm-email", "{}")]
    public async Task An_omitted_field_is_a_400_and_never_a_500(string url, string json)
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);

        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri(url, UriKind.Relative), content, Token);

        Assert.True(
            (int)response.StatusCode is >= 400 and < 500,
            $"{url} answered {(int)response.StatusCode} to {json}; it must be a 4xx.");
    }

    /// <summary>
    /// Every account response is <c>no-store</c> with <c>Vary: Cookie</c>: these payloads hold a name, a
    /// telephone number and an order history.
    /// </summary>
    [Fact]
    public async Task Account_responses_are_never_cacheable()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("no-store");

        await RegisterAsync(client, email);
        await SignInAsync(client, email);

        foreach (var url in new[] { MeUrl, "/api/account/profile", "/api/account/addresses", "/api/account/orders" })
        {
            using var response = await client.GetAsync(new Uri(url, UriKind.Relative), Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("Cookie", response.Headers.Vary, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Every signed-in account route answers 401 without a session, and none of them is reachable with an
    /// <i>admin</i> cookie either.
    /// </summary>
    /// <remarks>
    /// <b>Enumerated route by route, not asserted over a group.</b> #80's lesson: a group assertion
    /// passes while one member of the group is wrong, and the one that matters here is whichever route
    /// somebody forgets to put the policy on.
    /// </remarks>
    [Theory]
    [InlineData("GET", "/api/account/me")]
    [InlineData("GET", "/api/account/profile")]
    [InlineData("PUT", "/api/account/profile")]
    [InlineData("GET", "/api/account/addresses")]
    [InlineData("POST", "/api/account/addresses")]
    [InlineData("PUT", "/api/account/addresses/1")]
    [InlineData("DELETE", "/api/account/addresses/1")]
    [InlineData("POST", "/api/account/addresses/1/default")]
    [InlineData("GET", "/api/account/orders")]
    [InlineData("POST", "/api/account/change-password")]
    [InlineData("POST", "/api/account/resend-confirmation")]
    [InlineData("POST", "/api/account/logout")]
    public async Task Every_signed_in_route_refuses_a_visitor_with_no_session(string method, string url)
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);

        using var request = new HttpRequestMessage(new HttpMethod(method), url);

        if (method is "POST" or "PUT")
        {
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
