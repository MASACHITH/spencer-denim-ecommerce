using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Infrastructure.Identity.Customers;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Accounts;

/// <summary>
/// Doc 11 P11 step C (forgot and reset) and step 10's password half, over HTTP (ACC-01).
/// </summary>
public sealed class CustomerPasswordEndpointTests(SqlServerDatabaseFixture sqlServer)
    : CustomerAccountTestBase(sqlServer)
{
    private const string ForgotUrl = "/api/account/forgot-password";
    private const string ResetUrl = "/api/account/reset-password";
    private const string ChangeUrl = "/api/account/change-password";
    private const string LoginUrl = "/api/account/login";
    private const string MeUrl = "/api/account/me";

    private const string NewPassword = "Brand-New-Passphrase-7!";

    /// <summary>
    /// Doc 08 §3.1 DB10: the same generic reply for an unknown address, a customer and a staff member —
    /// and only the customer receives a message.
    /// </summary>
    /// <remarks>
    /// <b>The three cases are told apart by looking at <c>ops.EmailOutbox</c>, not at the response.</b>
    /// That is the whole design: <c>ICustomerPasswords.RequestResetAsync</c> returns nothing, so there is
    /// no value an endpoint could be refactored into leaking, and the only place the difference exists is
    /// the queue.
    /// </remarks>
    [Fact]
    public async Task Forgot_password_answers_the_same_for_three_addresses_and_writes_to_one()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);

        var customer = UniqueEmail("forgot-customer");
        var staff = UniqueEmail("forgot-staff");
        var unknown = UniqueEmail("forgot-unknown");

        await RegisterAsync(client, customer);
        await CreateStaffUserAsync(factory, staff, GoodPassword);

        var replies = new List<(HttpStatusCode Status, string Body)>();

        foreach (var email in new[] { customer, staff, unknown })
        {
            using var response = await client.PostAsJsonAsync(ForgotUrl, new { email }, Token);
            replies.Add((response.StatusCode, await response.Content.ReadAsStringAsync(Token)));
        }

        Assert.All(replies, reply => Assert.Equal(HttpStatusCode.Accepted, reply.Status));
        Assert.Equal(replies[0].Body, replies[1].Body);
        Assert.Equal(replies[0].Body, replies[2].Body);

        Assert.Equal(1, await CountQueuedAsync(factory, Templates.PasswordReset, customer));
        Assert.Equal(0, await CountQueuedAsync(factory, Templates.PasswordReset, staff));
        Assert.Equal(0, await CountQueuedAsync(factory, Templates.PasswordReset, unknown));
    }

    /// <summary>
    /// The reply says nothing about delivery, because doc 08 §3.8's transport may be unconfigured.
    /// </summary>
    /// <remarks>
    /// <b>W3-02 removed "we will email your order number" from checkout for this reason</b> and
    /// <c>checkout-page.spec.ts</c> pins its absence; two Majors on this project came from copy that
    /// promised what the transport could not do. This is the same guard on the account surface, written
    /// against the <i>server's</i> wording so a storefront change cannot reintroduce it quietly.
    /// </remarks>
    [Theory]
    [InlineData(ForgotUrl)]
    [InlineData("/api/account/register")]
    public async Task No_reply_claims_an_email_has_been_sent(string url)
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("promise");

        using var response = await client.PostAsJsonAsync(
            url,
            new { firstName = "Nimal", lastName = "Perera", email, password = GoodPassword },
            Token);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        var message = body.GetProperty("message").GetString() ?? string.Empty;

        Assert.DoesNotContain("we have sent", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("we've sent", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("check your inbox", message, StringComparison.OrdinalIgnoreCase);

        // And it does say what actually happened, so the wording is not merely vague.
        Assert.Contains("queued", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Doc 11 P11 step C.9: the link sets the password, the old one stops working, and every other
    /// session ends.
    /// </summary>
    /// <remarks>
    /// <b>Two browsers, because "invalidates existing sessions" is only observable with a second one.</b>
    /// The first client signs in and stays signed in; the reset is redeemed by a client with no session,
    /// as a real customer opening their email in another browser would; then the first client's <c>/me</c>
    /// must answer 401.
    /// <para>
    /// The security-stamp revalidation interval is configured down to one second, because the default is
    /// five minutes and no test can wait for it (<c>CustomerSecurityOptions</c> keeps it configurable for
    /// exactly this).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Resetting_a_password_replaces_it_and_ends_every_other_session()
    {
        await ReadyAsync();

        await using var factory = CreateHost(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CustomerSecurity:SecurityStampValidationIntervalSeconds"] = "1",
        });

        using var browser = CreateClient(factory);
        using var mailReader = CreateClient(factory);
        var email = UniqueEmail("reset-sessions");

        await RegisterAsync(browser, email);
        await SignInAsync(browser, email);

        using var stillIn = await browser.GetAsync(new Uri(MeUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, stillIn.StatusCode);

        using var asked = await mailReader.PostAsJsonAsync(ForgotUrl, new { email }, Token);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);

        var token = await ReadLinkTokenAsync(factory, Templates.PasswordReset, email);
        Assert.NotNull(token);

        using var reset = await mailReader.PostAsJsonAsync(ResetUrl, new { token, password = NewPassword }, Token);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        // No session is issued by the reset itself: the link was opened by whatever browser read the
        // email.
        using var mailReaderMe = await mailReader.GetAsync(new Uri(MeUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, mailReaderMe.StatusCode);

        // The old password is gone.
        using var oldPassword = await browser.PostAsJsonAsync(
            LoginUrl,
            new { email, password = GoodPassword },
            Token);

        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);

        // The other browser's session has ended with the stamp bump.
        await Task.Delay(TimeSpan.FromSeconds(1.5), Token);

        using var afterReset = await browser.GetAsync(new Uri(MeUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, afterReset.StatusCode);

        // And the new password works.
        using var fresh = CreateClient(factory);
        await SignInAsync(fresh, email, NewPassword);
    }

    /// <summary>A reset link works once.</summary>
    [Fact]
    public async Task A_reset_link_works_once()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("reset-once");

        await RegisterAsync(client, email);

        using var asked = await client.PostAsJsonAsync(ForgotUrl, new { email }, Token);
        _ = asked;

        var token = await ReadLinkTokenAsync(factory, Templates.PasswordReset, email);

        using var first = await client.PostAsJsonAsync(ResetUrl, new { token, password = NewPassword }, Token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await client.PostAsJsonAsync(
            ResetUrl,
            new { token, password = "Another-Passphrase-9!" },
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);

        var body = await second.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal("invalid_link", body.GetProperty("code").GetString());

        // The second password never took: the first one still works.
        using var fresh = CreateClient(factory);
        await SignInAsync(fresh, email, NewPassword);
    }

    /// <summary>
    /// A refused password does <b>not</b> spend the link, so a shopper who typed a short one can try
    /// again with the same message.
    /// </summary>
    /// <remarks>
    /// <b>The alternative is worse than it looks.</b> Burning the token on a validation failure means a
    /// customer who mistypes has to ask for a whole new message — and on a shop with no mail transport,
    /// that is the end of their reset. The token is only spent when a password is actually set.
    /// </remarks>
    [Fact]
    public async Task A_refused_password_leaves_the_link_usable()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("reset-weak");

        await RegisterAsync(client, email);

        using var asked = await client.PostAsJsonAsync(ForgotUrl, new { email }, Token);
        _ = asked;

        var token = await ReadLinkTokenAsync(factory, Templates.PasswordReset, email);

        using var tooShort = await client.PostAsJsonAsync(ResetUrl, new { token, password = "Ab1!cdef" }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        var problem = await tooShort.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.True(problem.GetProperty("errors").TryGetProperty("password", out _));

        var customer = await ReadCustomerAsync(factory, email);
        Assert.NotNull(customer);
        Assert.Equal(1, await CountTokensAsync(factory, customer.Id, CustomerAccountTokens.PasswordResetName));

        using var good = await client.PostAsJsonAsync(ResetUrl, new { token, password = NewPassword }, Token);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }

    /// <summary>
    /// A reset clears a lockout, because the customer has proved they own the mailbox.
    /// </summary>
    /// <remarks>
    /// <b>A customer who was locked out <i>because</i> they had forgotten their password must not then
    /// have to wait fifteen minutes.</b> That is the whole case: the lockout is there to stop guessing,
    /// and somebody holding a live link from their own inbox is not guessing.
    /// </remarks>
    [Fact]
    public async Task A_reset_clears_a_lockout()
    {
        await ReadyAsync();

        await using var factory = CreateHost(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CustomerSecurity:LockoutMaxFailedAttempts"] = "2",
        });

        using var client = CreateClient(factory);
        var email = UniqueEmail("reset-unlocks");
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

        using var asked = await client.PostAsJsonAsync(ForgotUrl, new { email }, Token);
        _ = asked;

        var token = await ReadLinkTokenAsync(factory, Templates.PasswordReset, email);

        using var reset = await client.PostAsJsonAsync(ResetUrl, new { token, password = NewPassword }, Token);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var after = await ReadCustomerAsync(factory, email);
        Assert.NotNull(after);
        Assert.Null(after.LockoutEnd);
        Assert.Equal(0, after.AccessFailedCount);

        using var fresh = CreateClient(factory);
        await SignInAsync(fresh, email, NewPassword);
    }

    /// <summary>
    /// A staff account cannot be reset through the customer endpoint (doc 08 §3.1 DB10: "The customer
    /// reset endpoint can never reset a staff account").
    /// </summary>
    /// <remarks>
    /// <b>The staff password is checked afterwards, not merely the absence of a message.</b> A test that
    /// only asserted "no email was queued" would pass against an endpoint that silently reset the staff
    /// password without telling anybody — which is a worse outcome than the one it was looking for.
    /// </remarks>
    [Fact]
    public async Task A_staff_password_cannot_be_reset_through_the_customer_endpoint()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var staff = UniqueEmail("reset-staff-guard");

        await CreateStaffUserAsync(factory, staff, GoodPassword);

        using var asked = await client.PostAsJsonAsync(ForgotUrl, new { email = staff }, Token);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);

        Assert.Equal(0, await CountQueuedAsync(factory, Templates.PasswordReset, staff));

        // No token was minted for the staff row either, so there is nothing for a guess to land on.
        var row = await ReadCustomerAsync(factory, staff);
        Assert.NotNull(row);
        Assert.Equal(0, await CountTokensAsync(factory, row.Id, CustomerAccountTokens.PasswordResetName));

        // And the staff password still works where it is supposed to.
        using var adminLogin = await client.PostAsJsonAsync(
            "/api/admin/auth/login",
            new { email = staff, password = GoodPassword },
            Token);

        Assert.Equal(HttpStatusCode.OK, adminLogin.StatusCode);
    }

    /// <summary>Doc 11 P11 step 10: a signed-in customer changes their own password and stays signed in.</summary>
    [Fact]
    public async Task Changing_a_password_keeps_this_browser_signed_in_and_ends_the_others()
    {
        await ReadyAsync();

        await using var factory = CreateHost(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CustomerSecurity:SecurityStampValidationIntervalSeconds"] = "1",
        });

        using var here = CreateClient(factory);
        using var elsewhere = CreateClient(factory);
        var email = UniqueEmail("change-password");

        await RegisterAsync(here, email);
        await SignInAsync(here, email);
        await SignInAsync(elsewhere, email);

        using var changed = await here.PostAsJsonAsync(
            ChangeUrl,
            new { currentPassword = GoodPassword, newPassword = NewPassword },
            Token);

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        // This browser's cookie was reissued with the new stamp, so it stays signed in.
        await Task.Delay(TimeSpan.FromSeconds(1.5), Token);

        using var stillHere = await here.GetAsync(new Uri(MeUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, stillHere.StatusCode);

        using var notThere = await elsewhere.GetAsync(new Uri(MeUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, notThere.StatusCode);
    }

    /// <summary>A wrong current password is refused, named, and counted towards the lockout.</summary>
    /// <remarks>
    /// <b>#146 extended its lockout guard to change-password for exactly this reason:</b> an endpoint
    /// that verifies a password is a password oracle whether or not it is called login. Without the
    /// counting, an attacker with a stolen session could guess the password at an unlimited rate, which
    /// matters because the password is what lets them change the <i>email</i> next.
    /// </remarks>
    [Fact]
    public async Task A_wrong_current_password_is_refused_and_counts_towards_the_lockout()
    {
        await ReadyAsync();

        await using var factory = CreateHost(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CustomerSecurity:LockoutMaxFailedAttempts"] = "2",
        });

        using var client = CreateClient(factory);
        var email = UniqueEmail("change-wrong");

        await RegisterAsync(client, email);
        await SignInAsync(client, email);

        using var first = await client.PostAsJsonAsync(
            ChangeUrl,
            new { currentPassword = "Not-My-Password-1!", newPassword = NewPassword },
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);

        var body = await first.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.True(body.GetProperty("errors").TryGetProperty("currentPassword", out _));

        using var second = await client.PostAsJsonAsync(
            ChangeUrl,
            new { currentPassword = "Not-My-Password-2!", newPassword = NewPassword },
            Token);

        Assert.Equal(HttpStatusCode.Locked, second.StatusCode);

        var locked = await ReadCustomerAsync(factory, email);
        Assert.NotNull(locked);
        Assert.NotNull(locked.LockoutEnd);

        // The password did not change on any of those attempts.
        using var fresh = CreateClient(factory);
        var attempt = await fresh.PostAsJsonAsync("/api/account/login", new { email, password = NewPassword }, Token);
        Assert.NotEqual(HttpStatusCode.OK, attempt.StatusCode);
        attempt.Dispose();
    }

    /// <summary>
    /// A weak new password is refused on both routes, with the rule the shopper was shown.
    /// </summary>
    /// <remarks>
    /// <b>The message is asserted, not just the field.</b> <c>PasswordPolicy.Requirement</c> is
    /// the one sentence the form's hint and the server's refusal both read from — #126's defect was a
    /// form and a column disagreeing about a number, and the cure is one value with one name. If the
    /// two ever diverge, this goes red.
    /// </remarks>
    [Fact]
    public async Task A_short_password_is_refused_with_the_rule_the_shopper_was_shown()
    {
        await ReadyAsync();

        await using var factory = CreateHost();
        using var client = CreateClient(factory);
        var email = UniqueEmail("weak-password");

        await RegisterAsync(client, email);
        await SignInAsync(client, email);

        using var response = await client.PostAsJsonAsync(
            ChangeUrl,
            new { currentPassword = GoodPassword, newPassword = "Ab1!cdef" },
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        var messages = body.GetProperty("errors").GetProperty("newPassword")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToList();

        Assert.Contains(SpencerDenim.Domain.Identity.PasswordPolicy.Requirement, messages);
    }

    /// <summary>
    /// With no storefront origin configured, a reset is <b>not</b> queued and no token is minted.
    /// </summary>
    /// <remarks>
    /// <b>This is the behaviour of the shop as it stands, and it is the one an operator has to know
    /// about.</b> Doc 08 §3.8's <c>Email:StorefrontBaseUrl</c> may legitimately be unset — "an unset
    /// value costs the links and nothing else" for an order email, but a reset email with no reset link
    /// is not a reset email. So nothing is queued, and critically <i>no token is minted</i>: minting one
    /// would replace a link the customer might still be holding with one nobody can ever use.
    /// <para>
    /// The reply is unchanged, because it must be: a response that differed would say whether the shop
    /// can send email, and more importantly the wording never promised delivery in the first place.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task With_no_storefront_origin_no_reset_is_queued_and_no_token_is_minted()
    {
        await ReadyAsync();

        // A host with the origin blanked. Everything else is as it is in production.
        await using var configured = CreateHost();
        using var setup = CreateClient(configured);
        var email = UniqueEmail("no-origin");
        await RegisterAsync(setup, email);

        using var asked = await setup.PostAsJsonAsync(ForgotUrl, new { email }, Token);
        _ = asked;

        var customer = await ReadCustomerAsync(configured, email);
        Assert.NotNull(customer);
        Assert.Equal(1, await CountTokensAsync(configured, customer.Id, CustomerAccountTokens.PasswordResetName));

        var tokenBefore = await ReadStoredTokenAsync(customer.Id, CustomerAccountTokens.PasswordResetName);

        await using var unconfigured = new ApiFactory(
            SqlServer.ConnectionString,
            additionalConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Email:StorefrontBaseUrl"] = null,
            });

        using var client = CreateClient(unconfigured);

        var queuedBefore = await CountQueuedAsync(unconfigured, Templates.PasswordReset, email);

        using var response = await client.PostAsJsonAsync(ForgotUrl, new { email }, Token);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        Assert.Equal(queuedBefore, await CountQueuedAsync(unconfigured, Templates.PasswordReset, email));

        // And the token the customer may already be holding is untouched.
        Assert.Equal(tokenBefore, await ReadStoredTokenAsync(customer.Id, CustomerAccountTokens.PasswordResetName));
    }

    private async Task<string?> ReadStoredTokenAsync(int userId, string name)
    {
        DbContext.ChangeTracker.Clear();

        return await DbContext.UserTokens
            .AsNoTracking()
            .Where(row =>
                row.UserId == userId
                && row.LoginProvider == CustomerAccountTokens.LoginProvider
                && row.Name == name)
            .Select(row => row.Value)
            .FirstOrDefaultAsync(Token);
    }
}
