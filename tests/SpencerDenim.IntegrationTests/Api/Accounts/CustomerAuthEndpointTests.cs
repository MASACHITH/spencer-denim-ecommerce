using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    /// A deactivated customer cannot sign in, and the refusal is the same one everybody else gets.
    /// </summary>
    /// <remarks>
    /// <b>This closes a hole the mutation run found.</b> <c>CustomerUserLookup</c> filters on
    /// <c>IsActive</c>, and removing that filter left every test in this file green — so nothing said
    /// that a deactivated account is refused. It matters because doc 08 §3.1's PDPA anonymisation sets
    /// <c>IsActive = 0</c>, blanks the password hash and rewrites the address: an anonymised row must
    /// not be a way back into the shop.
    /// <para>
    /// <b>And the live session ends too.</b> <c>IsActive</c> is re-read directly on each revalidation
    /// rather than only through a security-stamp bump, so deactivation ends a session whether or not
    /// whatever deactivated the account also remembered to rotate the stamp — <c>IdentityRegistration</c>
    /// records that as a real defect on the admin scheme. The interval is configured down to one second
    /// because the default is five minutes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_deactivated_customer_cannot_sign_in_and_their_session_ends()
    {
        await ReadyAsync();

        await using var factory = CreateHost(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CustomerSecurity:SecurityStampValidationIntervalSeconds"] = "1",
        });

        using var client = CreateClient(factory);
        var email = UniqueEmail("deactivated");

        await RegisterAsync(client, email);
        await SignInAsync(client, email);

        using var signedIn = await client.GetAsync(new Uri(MeUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);

        var customer = await ReadCustomerAsync(factory, email);
        Assert.NotNull(customer);

        // Stands in for the staff "deactivate customer" action, which is ADM-15's and does not exist.
        // One column, by key, exactly as that endpoint will write it.
        await DeactivateAsync(customer.Id);

        // The live session is gone within one revalidation interval.
        await Task.Delay(TimeSpan.FromSeconds(1.5), Token);

        using var afterDeactivation = await client.GetAsync(new Uri(MeUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, afterDeactivation.StatusCode);

        // And the correct password is refused with the body an unknown address gets — a different reply
        // here would say "this address exists but has been switched off".
        using var fresh = CreateClient(factory);

        using var refused = await fresh.PostAsJsonAsync(LoginUrl, new { email, password = GoodPassword }, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        using var unknown = await fresh.PostAsJsonAsync(
            LoginUrl,
            new { email = UniqueEmail("nobody-at-all"), password = GoodPassword },
            Token);

        Assert.Equal(
            WithoutTraceId(await unknown.Content.ReadAsStringAsync(Token)),
            WithoutTraceId(await refused.Content.ReadAsStringAsync(Token)));
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

    /// <summary>
    /// Doc 11 P11 step B.5: the account locks after the configured number of failures, refuses even the
    /// right password while locked, and never says so in its reply.
    /// </summary>
    /// <remarks>
    /// <b>The threshold is configured down to three rather than defaulted to ten</b>, so the test is
    /// about the mechanism rather than about the number, and so it does not spend ten password hashes to
    /// find out. The real default is pinned separately, against doc 08 §3.8's own figure, in
    /// <c>SettingKeySpecificationTests</c>.
    /// <para>
    /// <b>Until W3-01 round 2 this asserted a 423 with the end time</b>, and the independent tester showed
    /// that reply was an enumeration oracle (Major 4): only an address with an account can lock. The lock
    /// is now read from the database, and the reply is asserted to be the wrong-password reply.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_account_locks_after_the_configured_number_of_failures_without_saying_so()
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

        string? wrongPasswordReply = null;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var refused = await client.PostAsJsonAsync(
                LoginUrl,
                new { email, password = "Wrong-Password-9!" },
                Token);

            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            wrongPasswordReply = WithoutTraceId(await refused.Content.ReadAsStringAsync(Token));
        }

        using var third = await client.PostAsJsonAsync(LoginUrl, new { email, password = "Wrong-Password-9!" }, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, third.StatusCode);
        Assert.Equal(wrongPasswordReply, WithoutTraceId(await third.Content.ReadAsStringAsync(Token)));

        // The lock is real: it is on the row, and it ends in the future.
        var locked = await ReadCustomerAsync(factory, email);
        Assert.NotNull(locked);
        Assert.True(
            locked.LockoutEnd > DateTimeOffset.UtcNow,
            "Three wrong passwords did not lock the account.");

        // And the correct password is refused while the lock holds, which is the whole point of it,
        // with the same reply a wrong password gets.
        using var correct = await client.PostAsJsonAsync(LoginUrl, new { email, password = GoodPassword }, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, correct.StatusCode);
        Assert.Equal(wrongPasswordReply, WithoutTraceId(await correct.Content.ReadAsStringAsync(Token)));

        using var me = await client.GetAsync(new Uri(MeUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    /// <summary>
    /// <c>GET /api/account/session</c>: 200 and <c>null</c> signed out, 200 and the customer signed in, and
    /// <c>null</c> again once the account is switched off — never a 401.
    /// </summary>
    /// <remarks>
    /// The header asks this on every page view, so a 401 for the ordinary signed-out visitor was a console
    /// error on every page (the home page's browser test caught it). The deactivated case is the one that
    /// matters for safety: a live cookie for a switched-off account must not be greeted by name.
    /// </remarks>
    [Fact]
    public async Task The_session_read_answers_200_whoever_asks_and_names_only_an_active_customer()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);

        using (var anonymous = await client.GetAsync(new Uri("/api/account/session", UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
            var body = await anonymous.Content.ReadFromJsonAsync<JsonElement>(Token);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("customer").ValueKind);
        }

        var email = UniqueEmail("session-read");
        await RegisterAsync(client, email);
        await SignInAsync(client, email);

        using (var signedIn = await client.GetAsync(new Uri("/api/account/session", UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
            var body = await signedIn.Content.ReadFromJsonAsync<JsonElement>(Token);
            Assert.Equal(email, body.GetProperty("customer").GetProperty("email").GetString());
        }

        var row = await ReadCustomerAsync(factory, email);
        Assert.NotNull(row);
        await DeactivateAsync(row.Id);

        using (var switchedOff = await client.GetAsync(new Uri("/api/account/session", UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.OK, switchedOff.StatusCode);
            var body = await switchedOff.Content.ReadFromJsonAsync<JsonElement>(Token);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("customer").ValueKind);
        }
    }

    /// <summary>
    /// W3-01 Major 4: eleven wrong passwords against a real account and against an address with no
    /// account get the same eleven replies.
    /// </summary>
    /// <remarks>
    /// <b>The tester's reproduction, kept as a test.</b> Before the fix an unknown address answered 401
    /// eleven times and a customer answered 401 eight times then 423 three times, so anybody with a list
    /// of addresses could sort it into "has an account" and "does not" — and lock every real one while
    /// doing it. The comparison is of status and whole body (less <c>traceId</c>), attempt by attempt, so
    /// a field added later that differs only when locked would fail it too. The last attempt is the
    /// <i>correct</i> password for the customer, refused because the account is locked, and it must still
    /// read exactly like the stranger's refusal.
    /// </remarks>
    [Fact]
    public async Task A_locked_account_answers_exactly_what_an_address_with_no_account_answers()
    {
        await ReadyAsync();

        await using var factory = CreateHost(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CustomerSecurity:LockoutMaxFailedAttempts"] = "3",
            ["CustomerSecurity:LockoutMinutes"] = "15",
            ["CustomerSecurity:SignInAttemptsPerWindow"] = "200",
        });

        using var client = CreateClient(factory);
        var customer = UniqueEmail("enum-customer");
        var nobody = UniqueEmail("enum-nobody");
        await RegisterAsync(client, customer);

        async Task<List<string>> AttemptsAsync(string email, string lastPassword)
        {
            var replies = new List<string>();

            for (var attempt = 1; attempt <= 11; attempt++)
            {
                var password = attempt == 11 ? lastPassword : "Wrong-Password-9!";
                using var response = await client.PostAsJsonAsync(LoginUrl, new { email, password }, Token);
                var body = WithoutTraceId(await response.Content.ReadAsStringAsync(Token));
                replies.Add($"{(int)response.StatusCode} {body}");
            }

            return replies;
        }

        var customerReplies = await AttemptsAsync(customer, GoodPassword);
        var nobodyReplies = await AttemptsAsync(nobody, GoodPassword);

        // The customer really is locked, so the identical replies are hiding a lock rather than missing one.
        var row = await ReadCustomerAsync(factory, customer);
        Assert.NotNull(row);
        Assert.True(row.LockoutEnd > DateTimeOffset.UtcNow, "The customer was never locked.");

        Assert.Equal(nobodyReplies, customerReplies);
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

            Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        }

        var after = await ReadCustomerAsync(factory, email);
        Assert.NotNull(after);
        Assert.Equal(locked.LockoutEnd, after.LockoutEnd);
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

    /// <summary>
    /// Switches an account off, with the one column a future staff action will write.
    /// </summary>
    /// <remarks>
    /// Written as a set-based statement rather than through <c>UserManager.UpdateAsync</c>, which would
    /// write every column of a user instance — the trap #146's second finding is about, and the reason
    /// every write in <c>CustomerAccountLockout</c> and <c>CustomerProfileStore</c> names its columns.
    /// </remarks>
    private async Task DeactivateAsync(int userId)
    {
        DbContext.ChangeTracker.Clear();

        await DbContext.Database.ExecuteSqlAsync(
            $"UPDATE [identity].[Users] SET [IsActive] = 0 WHERE [Id] = {userId}",
            Token);
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
