using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Ops.Email.Contracts;
using SpencerDenim.Domain.Branding;

namespace SpencerDenim.UnitTests.Infrastructure.Ops;

/// <summary>
/// The four account emails of doc 11 P11 (<see cref="AccountEmailComposer"/>).
/// </summary>
/// <remarks>
/// <b>The composer is pure, so the bodies themselves are asserted</b> — <c>OrderEmailComposer</c>'s
/// argument, and the reason it matters more for this set is that two of these messages are nothing but a
/// link and the third is the only thing that keeps registration non-enumerating.
/// </remarks>
public sealed class AccountEmailComposerTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);

    private static AccountEmailComposer Composer(string? origin = "https://shop.test") =>
        new(new EmailOptions { StorefrontBaseUrl = origin });

    private static AccountEmailFacts Facts(string? link = null, int hours = 2) =>
        new(new AccountEmailAddressee("buyer@example.com", "Nimal", "Perera"), link, Now)
        {
            LinkValidHours = hours,
        };

    /// <summary>
    /// No body promises an email, because doc 08 §3.8's transport may be unconfigured.
    /// </summary>
    /// <remarks>
    /// <b>W3-02 removed "we will email your order number" from checkout for exactly this reason</b>, and
    /// two Majors on this project came from copy that promised what the transport could not do. The
    /// subtle case this catches is <c>AccountExists</c>: it may say "use the forgot-password page",
    /// because a page is a page — it may <b>not</b> say "we have emailed you a reset link", because the
    /// shop has not sent one.
    /// <para>
    /// A promise made <i>inside</i> a message that arrived would in fact be safe — the reader holding it
    /// proves the transport works. The rule is kept anyway, because the shape is indistinguishable from
    /// the unsafe one in review and the copy loses nothing by being accurate.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_body_says_an_email_has_been_sent()
    {
        var composer = Composer();

        var bodies = new[]
        {
            composer.EmailConfirmation(Facts("https://shop.test/account/confirm-email?token=x")),
            composer.Welcome(Facts()),
            composer.AccountExists(Facts()),
            composer.PasswordReset(Facts("https://shop.test/account/reset-password?token=x")),
        };

        foreach (var body in bodies)
        {
            foreach (var part in new[] { body.Html, body.Text, body.Subject })
            {
                Assert.DoesNotContain("we have sent", part, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("we've sent", part, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("we have emailed", part, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("we will email", part, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("check your inbox", part, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// The shop names itself from <see cref="StoreBrand"/>, and the client company is never in a
    /// shopper's inbox.
    /// </summary>
    /// <remarks>
    /// <c>BrandNameConventionTests</c> fails the build if the shop's name is written out in production
    /// source, so this asserts the <i>output</i> — that the name is actually there, which that convention
    /// test cannot see. The client company's name belongs on an invoice (doc 08 §3.8's
    /// <c>Store.LegalName</c> seam) and not here.
    /// </remarks>
    [Fact]
    public void Every_body_names_the_shop_and_not_the_client_company()
    {
        var composer = Composer();

        var bodies = new[]
        {
            composer.EmailConfirmation(Facts("https://shop.test/account/confirm-email?token=x")),
            composer.Welcome(Facts()),
            composer.AccountExists(Facts()),
            composer.PasswordReset(Facts("https://shop.test/account/reset-password?token=x")),
        };

        foreach (var body in bodies)
        {
            Assert.Contains(StoreBrand.Name, body.Html, StringComparison.Ordinal);
            Assert.Contains(StoreBrand.Name, body.Text, StringComparison.Ordinal);

            foreach (var part in new[] { body.Html, body.Text, body.Subject })
            {
                Assert.DoesNotContain("Spencer", part, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Pvt", part, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Both parts of every message are written, and the plain-text part is really text.
    /// </summary>
    /// <remarks>
    /// <b>The text part is what reaches a text-only client, a screen reader set to plain text, and every
    /// spam filter that scores a message with no alternative</b> — <c>EmailBody</c>'s own note. A body
    /// built by stripping tags from the HTML reads like stripped HTML, so the absence of tags is asserted
    /// rather than assumed.
    /// </remarks>
    [Fact]
    public void Both_parts_are_written_and_the_text_part_has_no_markup()
    {
        var composer = Composer();

        var bodies = new[]
        {
            composer.EmailConfirmation(Facts("https://shop.test/account/confirm-email?token=x")),
            composer.Welcome(Facts()),
            composer.AccountExists(Facts()),
            composer.PasswordReset(Facts("https://shop.test/account/reset-password?token=x")),
        };

        foreach (var body in bodies)
        {
            Assert.False(string.IsNullOrWhiteSpace(body.Subject));
            Assert.Contains("<html", body.Html, StringComparison.Ordinal);
            Assert.DoesNotContain("<p", body.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("<a ", body.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("</", body.Text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The confirmation says what confirming is <i>for</i>, which is the one thing a customer cannot
    /// guess.
    /// </summary>
    /// <remarks>
    /// Doc 11 §7 G-24 lets an unconfirmed customer sign in, shop and check out, so "confirm or you cannot
    /// use your account" would be false. What confirmation actually buys is doc 08 §3.1 D5: the orders
    /// they placed as a guest with this address become theirs. A customer told that has a reason to click.
    /// </remarks>
    [Fact]
    public void The_confirmation_says_what_confirming_is_for()
    {
        var composed = Composer().EmailConfirmation(Facts("https://shop.test/account/confirm-email?token=abc"));

        Assert.Contains("guest", composed.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("order", composed.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://shop.test/account/confirm-email?token=abc", composed.Text, StringComparison.Ordinal);
        Assert.Contains("https://shop.test/account/confirm-email?token=abc", composed.Html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reset says what will happen to the customer's other sessions, because that is the surprising
    /// part.
    /// </summary>
    [Fact]
    public void The_reset_says_that_other_sessions_will_end()
    {
        var composed = Composer().PasswordReset(Facts("https://shop.test/account/reset-password?token=abc"));

        Assert.Contains("signs you out", composed.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("did not ask", composed.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The two token-bearing messages refuse to compose without a link, rather than sending one with its
    /// instructions missing.
    /// </summary>
    /// <remarks>
    /// <b>This is the behaviour that makes the unset-origin case safe.</b> <c>AccountEmailOutbox</c>
    /// checks the link first and queues nothing; the throw here is the backstop, so a future caller that
    /// forgets cannot silently send a reset email with no reset in it.
    /// </remarks>
    [Fact]
    public void A_message_that_is_nothing_but_a_link_refuses_to_compose_without_one()
    {
        var composer = Composer();

        Assert.Throws<InvalidOperationException>(() => composer.EmailConfirmation(Facts(link: null)));
        Assert.Throws<InvalidOperationException>(() => composer.PasswordReset(Facts(link: null)));
    }

    /// <summary>
    /// With no configured origin there is no link to build, and the two tokenless messages still compose.
    /// </summary>
    /// <remarks>
    /// <c>EmailOptions.StorefrontBaseUrl</c>: "an unset value costs the links and nothing else". For
    /// <c>Welcome</c> and <c>AccountExists</c> that is exactly true — they lose a button and keep their
    /// meaning. For the other two it costs the whole message, which is why those are not queued at all.
    /// </remarks>
    [Fact]
    public void With_no_origin_there_is_no_link_and_the_tokenless_messages_still_work()
    {
        var composer = Composer(origin: null);

        Assert.Null(composer.ConfirmEmailUrl("abc"));
        Assert.Null(composer.ResetPasswordUrl("abc"));
        Assert.Null(composer.SignInUrl);
        Assert.Null(composer.ForgotPasswordUrl);

        var welcome = composer.Welcome(Facts());
        var exists = composer.AccountExists(Facts());

        Assert.DoesNotContain("http", welcome.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", exists.Text, StringComparison.OrdinalIgnoreCase);

        // And they still say something useful rather than being a shell.
        Assert.Contains("sign in", welcome.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already", exists.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The token is escaped into the query string, so an odd character cannot break the link.</summary>
    /// <remarks>
    /// The verifier is URL-safe base64 by construction (<c>CustomerAccountTokens</c>), so this can only
    /// matter if that ever changes — which is the point of asserting it here rather than relying on the
    /// other type's guarantee.
    /// </remarks>
    [Fact]
    public void The_token_is_escaped_into_the_link()
    {
        var composer = Composer();

        Assert.Equal(
            "https://shop.test/account/confirm-email?token=a%2Bb%2Fc%3D",
            composer.ConfirmEmailUrl("a+b/c="));
    }

    /// <summary>A trailing slash on the configured origin does not produce a double slash.</summary>
    [Fact]
    public void A_trailing_slash_on_the_origin_is_not_doubled()
    {
        var composer = Composer("https://shop.test/");

        Assert.Equal("https://shop.test/account/sign-in", composer.SignInUrl);
    }

    /// <summary>
    /// The validity window is stated in the body, in the singular as well as the plural.
    /// </summary>
    /// <remarks>
    /// <b>"within the next 1 hours" is the sort of thing a shopper notices and a developer never
    /// does</b>, because the default is 2 and nobody runs the other branch. The number comes from the
    /// options the token was actually minted with, so the sentence cannot drift from the token.
    /// </remarks>
    [Theory]
    [InlineData(1, "within the next hour")]
    [InlineData(2, "within the next 2 hours")]
    [InlineData(168, "within the next 168 hours")]
    public void The_body_states_the_window_it_was_minted_with(int hours, string expected)
    {
        var composed = Composer().PasswordReset(
            Facts("https://shop.test/account/reset-password?token=x", hours));

        Assert.Contains(expected, composed.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The greeting degrades rather than guessing, and the <c>To</c> name is never invented.
    /// </summary>
    [Fact]
    public void The_greeting_degrades_rather_than_inventing_a_name()
    {
        var noName = new AccountEmailAddressee("buyer@example.com", string.Empty, null);

        Assert.Equal("Hello,", noName.Greeting);
        Assert.Null(noName.DisplayName);

        var firstOnly = new AccountEmailAddressee("buyer@example.com", "Nimal", null);

        Assert.Equal("Hello Nimal,", firstOnly.Greeting);
        Assert.Equal("Nimal", firstOnly.DisplayName);
        Assert.Equal("Nimal Perera", new AccountEmailAddressee("b@c.d", "Nimal", "Perera").DisplayName);
    }

    /// <summary>
    /// Every subject fits <c>ops.EmailOutbox.Subject</c>, which is <c>nvarchar(200)</c> (doc 08 §3.8).
    /// </summary>
    /// <remarks>
    /// <b>#126's defect in the other direction.</b> A subject longer than the column would throw at the
    /// insert, inside the business transaction that was writing the user row — so a registration would
    /// fail with a database error because a sentence was too long. The width is read from the constant
    /// that owns the column.
    /// </remarks>
    [Fact]
    public void Every_subject_fits_its_column()
    {
        var composer = Composer();

        var subjects = new[]
        {
            composer.EmailConfirmation(Facts("https://shop.test/x?token=y")).Subject,
            composer.Welcome(Facts()).Subject,
            composer.AccountExists(Facts()).Subject,
            composer.PasswordReset(Facts("https://shop.test/x?token=y")).Subject,
        };

        foreach (var subject in subjects)
        {
            Assert.True(
                subject.Length <= SpencerDenim.Domain.Ops.EmailOutboxMessage.SubjectMaxLength,
                $"\"{subject}\" is {subject.Length} characters; the column is "
                + $"{SpencerDenim.Domain.Ops.EmailOutboxMessage.SubjectMaxLength}.");
        }
    }

    /// <summary>
    /// The recipient's own name is HTML-escaped into the body.
    /// </summary>
    /// <remarks>
    /// <b>A name is the one free-text value in these messages that a stranger controls.</b> Registration
    /// takes a first name from an anonymous form; an unescaped one would put a shopper's markup into a
    /// message the shop sends. <c>EmailBody.Escape</c> does it, and this is the test that says the
    /// composer uses it.
    /// </remarks>
    [Fact]
    public void A_name_with_markup_in_it_is_escaped()
    {
        var composer = Composer();
        var facts = new AccountEmailFacts(
            new AccountEmailAddressee("buyer@example.com", "<script>alert(1)</script>", null),
            null,
            Now);

        var composed = composer.Welcome(facts);

        Assert.DoesNotContain("<script>", composed.Html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", composed.Html, StringComparison.Ordinal);
    }
}
