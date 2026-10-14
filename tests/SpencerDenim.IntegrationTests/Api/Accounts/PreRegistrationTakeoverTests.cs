using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Infrastructure.Identity.Customers;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.IntegrationTests.Sales;

namespace SpencerDenim.IntegrationTests.Api.Accounts;

/// <summary>
/// Doc 08 §3.1 D5's guest-order linking cannot hand a victim's orders to somebody who registered their
/// address first.
/// </summary>
/// <remarks>
/// <b>The attack the independent tester reproduced.</b> An attacker registers the victim's address with a
/// password the attacker chose — registration is open and answers the same sentence for every address, so
/// nothing stops it. The victim receives the confirmation email, whose own copy invites them to click
/// ("links any orders you placed as a guest"). They click. Before this fix that confirmed the address,
/// <b>linked the victim's guest orders to the attacker's account</b>, and signed the victim's browser in to
/// it; the attacker then signed in with their own password and read the victim's orders — each with its
/// <c>token</c>, which opens the full order page: name, address, telephone.
/// <para>
/// <b>The flaw is that the two proofs were never required together.</b> The link proves the clicker
/// controls the <i>mailbox</i>; the password belongs to whoever <i>registered</i>. Linking must need both.
/// So confirming now requires the confirming browser to be signed in <b>as the account the token
/// names</b> (the tester's option (a)): the victim can do that only by resetting the password through the
/// same mailbox, which kills the attacker's password and — because a reset rotates the security stamp and
/// the account routes revalidate it on every request — the attacker's session too. The attacker can sign
/// in but never holds the token. Confirming no longer issues a session either.
/// </para>
/// </remarks>
public sealed class PreRegistrationTakeoverTests(SqlServerDatabaseFixture sqlServer)
    : CustomerAccountTestBase(sqlServer)
{
    private const string AttackerPassword = "Attacker-Chosen-Pass-1!";
    private const string VictimPassword = "Victims-Own-Passphrase-7!";

    /// <summary>
    /// The tester's reproduction, as a regression test: the victim's click links nothing and signs nobody
    /// in, and the attacker reads an empty history.
    /// </summary>
    [Fact]
    public async Task Clicking_the_link_does_not_hand_the_victims_orders_to_whoever_registered_first()
    {
        await ReadyAsync();

        var reference = await ReferenceAsync();
        var victim = UniqueEmail("takeover-victim");
        var victimsOrder = await InsertGuestOrderAsync(reference, victim);

        await using var factory = CreateHost();

        using var attacker = CreateClient(factory);
        await RegisterAsync(attacker, victim, AttackerPassword);
        await SignInAsync(attacker, victim, AttackerPassword);

        // The victim clicks the link in their inbox, in their own browser, signed in as nobody.
        var token = await ReadLinkTokenAsync(factory, Templates.Confirmation, victim);
        Assert.NotNull(token);

        using var victimBrowser = CreateClient(factory);
        using var clicked = await victimBrowser.PostAsJsonAsync("/api/account/confirm-email", new { token }, Token);

        Assert.Equal(HttpStatusCode.Unauthorized, clicked.StatusCode);

        var body = await clicked.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal("sign_in_required", body.GetProperty("code").GetString());

        // Nothing was linked, nothing was confirmed, and the victim's browser was not signed in to the
        // attacker's account.
        Assert.Null(await ReadOwnerAsync(victimsOrder));

        var account = await ReadCustomerAsync(factory, victim);
        Assert.NotNull(account);
        Assert.False(account.EmailConfirmed);

        using var victimMe = await victimBrowser.GetAsync(new Uri("/api/account/me", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, victimMe.StatusCode);

        // The token was not spent by a click that could not use it, so the victim can still confirm
        // once they own the account.
        Assert.Equal(1, await CountTokensAsync(factory, account.Id, CustomerAccountTokens.EmailConfirmationName));

        // The attacker, signed in with their own password, sees no orders.
        using var stolen = await attacker.GetAsync(new Uri("/api/account/orders", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, stolen.StatusCode);
        var history = await stolen.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(0, history.GetProperty("totalCount").GetInt32());
    }

    /// <summary>
    /// The victim's way out, end to end: reset the password through their own mailbox, sign in, confirm —
    /// and the orders link to an account only they can open, while the attacker is locked out at once.
    /// </summary>
    /// <remarks>
    /// <b>"At once" is the property that matters.</b> A customer session normally revalidates its security
    /// stamp every five minutes; if that applied here the attacker's cookie would keep reading the
    /// freshly-linked orders for up to five minutes after the reset. The account and checkout routes
    /// therefore revalidate on every request, and this asserts the attacker's very next request is
    /// refused — with the production interval, not a shortened one.
    /// </remarks>
    [Fact]
    public async Task After_the_victim_resets_and_confirms_the_orders_are_theirs_and_the_attacker_is_out()
    {
        await ReadyAsync();

        var reference = await ReferenceAsync();
        var victim = UniqueEmail("takeover-recover");
        var victimsOrder = await InsertGuestOrderAsync(reference, victim);

        await using var factory = CreateHost();

        using var attacker = CreateClient(factory);
        await RegisterAsync(attacker, victim, AttackerPassword);
        await SignInAsync(attacker, victim, AttackerPassword);

        var confirmToken = await ReadLinkTokenAsync(factory, Templates.Confirmation, victim);

        // The attacker uses their session before the reset. **This line is what makes the test able to
        // fail**: the first version left it out, so the attacker's first account request came after the
        // reset, was the first revalidation of that cookie, and was refused for that reason alone — the
        // five-minute window it exists to close never opened, and the test passed on the unfixed code.
        using (var attackerBefore = await attacker.GetAsync(new Uri("/api/account/orders", UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.OK, attackerBefore.StatusCode);
        }

        // The victim cannot sign in — they never chose this password — so they reset it.
        using var victimBrowser = CreateClient(factory);

        using (var forgot = await victimBrowser.PostAsJsonAsync("/api/account/forgot-password", new { email = victim }, Token))
        {
            Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        }

        var resetToken = await ReadLinkTokenAsync(factory, Templates.PasswordReset, victim);

        using (var reset = await victimBrowser.PostAsJsonAsync(
            "/api/account/reset-password",
            new { token = resetToken, password = VictimPassword },
            Token))
        {
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        }

        // The attacker's very next request on an account route is refused: the stamp moved.
        using (var attackerAfterReset = await attacker.GetAsync(new Uri("/api/account/orders", UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, attackerAfterReset.StatusCode);
        }

        // And their password no longer works.
        using var attackerAgain = CreateClient(factory);
        using (var refused = await attackerAgain.PostAsJsonAsync(
            "/api/account/login",
            new { email = victim, password = AttackerPassword },
            Token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        // The victim signs in with the password they chose and confirms.
        await SignInAsync(victimBrowser, victim, VictimPassword);

        using var confirmed = await victimBrowser.PostAsJsonAsync(
            "/api/account/confirm-email",
            new { token = confirmToken },
            Token);

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        var account = await ReadCustomerAsync(factory, victim);
        Assert.NotNull(account);
        Assert.True(account.EmailConfirmed);
        Assert.Equal(account.Id, await ReadOwnerAsync(victimsOrder));

        using var mine = await victimBrowser.GetAsync(new Uri("/api/account/orders", UriKind.Relative), Token);
        var history = await mine.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(1, history.GetProperty("totalCount").GetInt32());
    }

    /// <summary>
    /// Signed in as a <i>different</i> account, the token is refused with the one opaque sentence — and is
    /// not spent, so it cannot be burned by a stranger to stop the owner confirming.
    /// </summary>
    [Fact]
    public async Task A_token_presented_by_a_different_signed_in_account_is_refused_and_not_spent()
    {
        await ReadyAsync();

        var owner = UniqueEmail("takeover-owner");
        var stranger = UniqueEmail("takeover-stranger");

        await using var factory = CreateHost();

        using var ownerBrowser = CreateClient(factory);
        await RegisterAsync(ownerBrowser, owner);
        var token = await ReadLinkTokenAsync(factory, Templates.Confirmation, owner);

        using var strangerBrowser = CreateClient(factory);
        await RegisterAsync(strangerBrowser, stranger);
        await SignInAsync(strangerBrowser, stranger);

        using var refused = await strangerBrowser.PostAsJsonAsync("/api/account/confirm-email", new { token }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var body = await refused.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal("invalid_link", body.GetProperty("code").GetString());

        var ownerRow = await ReadCustomerAsync(factory, owner);
        Assert.NotNull(ownerRow);
        Assert.False(ownerRow.EmailConfirmed);
        Assert.Equal(1, await CountTokensAsync(factory, ownerRow.Id, CustomerAccountTokens.EmailConfirmationName));

        // The owner can still use it.
        await SignInAsync(ownerBrowser, owner);
        using var confirmed = await ownerBrowser.PostAsJsonAsync("/api/account/confirm-email", new { token }, Token);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
    }

    /// <summary>
    /// Confirming never signs a browser in. A signed-out browser holding the owner's real link gets no
    /// customer cookie at all, and a signed-in one stays signed in as the same customer.
    /// </summary>
    /// <remarks>
    /// <b>This is the half of the takeover that put the victim's browser inside the attacker's account.</b>
    /// The first version of this test asserted "no <c>Set-Cookie</c> on a confirmation", which was the
    /// wrong property: the account routes now revalidate the session on every request, and a revalidated
    /// cookie is <i>renewed</i> — the caller's own session, re-sent with a fresh timestamp. What must never
    /// happen is a browser that was signed in as nobody, or as somebody else, coming out of a confirmation
    /// signed in as the account the token names.
    /// </remarks>
    [Fact]
    public async Task Confirming_never_signs_a_browser_in()
    {
        await ReadyAsync();

        var email = UniqueEmail("takeover-no-session");

        await using var factory = CreateHost();
        using var owner = CreateClient(factory);
        await RegisterAsync(owner, email);

        var token = await ReadLinkTokenAsync(factory, Templates.Confirmation, email);

        // A signed-out browser, holding a real, live link for a real account.
        using var stranger = CreateClient(factory);
        using var refused = await stranger.PostAsJsonAsync("/api/account/confirm-email", new { token }, Token);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.False(
            refused.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(value => value.StartsWith(CustomerAuthenticationDefaults.CookieName + "=", StringComparison.Ordinal)),
            "A signed-out browser was given a customer session by a confirmation link.");

        // The owner, signed in, confirms and is still themselves afterwards.
        await SignInAsync(owner, email);

        using var confirmed = await owner.PostAsJsonAsync("/api/account/confirm-email", new { token }, Token);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        using var me = await owner.GetAsync(new Uri("/api/account/me", UriKind.Relative), Token);
        var body = await me.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(email, body.GetProperty("email").GetString());
        Assert.True(body.GetProperty("emailConfirmed").GetBoolean());
    }

    private async Task<int> InsertGuestOrderAsync(SalesReferenceData reference, string email)
    {
        var row = SalesRowWriter.OrderRow(
            reference.PaymentProviderCode,
            reference.StandardDeliveryMethodId,
            SpencerDenim.Domain.Sales.DeliveryMethod.StandardCode);

        row["Email"] = email;
        row["EmailNormalized"] = email.ToUpperInvariant();
        row["CustomerUserId"] = null;

        var orderId = await Writer.InsertAsync("sales.Orders", row, Token);

        await Writer.InsertAsync(
            "sales.OrderLines",
            SalesRowWriter.OrderLineRow(orderId, reference.ProductId, reference.VariantId, reference.DepartmentId, reference.CategoryId),
            Token);

        return orderId;
    }

    private async Task<int?> ReadOwnerAsync(int orderId)
    {
        DbContext.ChangeTracker.Clear();

        return await DbContext.Orders
            .AsNoTracking()
            .Where(order => order.Id == orderId)
            .Select(order => order.CustomerUserId)
            .FirstAsync(Token);
    }
}
