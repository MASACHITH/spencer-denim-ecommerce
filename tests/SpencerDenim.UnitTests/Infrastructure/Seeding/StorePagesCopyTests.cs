using System.Text.RegularExpressions;
using SpencerDenim.Domain.Branding;
using SpencerDenim.Domain.Content;
using SpencerDenim.Infrastructure.Persistence.Seeding;

namespace SpencerDenim.UnitTests.Infrastructure.Seeding;

/// <summary>
/// Issue #252: the About us, Privacy policy and Terms and conditions drafts, checked for the failures a drafted
/// page can have without anything crashing: a placeholder left in, a claim nobody can back, a contact detail
/// written into copy where the Contact page is the one place that holds it, a shape the page cannot carry.
/// <para>
/// What these tests <b>cannot</b> check is whether a sentence is true. That is the independent tester's job and the
/// reason the #252 journal entry keeps a table tying every sentence to the code or the document that supports it.
/// These guard the cheap, mechanical half so the tester can spend the time on the other half.
/// </para>
/// </summary>
public sealed partial class StorePagesCopyTests
{
    private static readonly string[] PageKeys =
        [ContentPageKeys.About, ContentPageKeys.Privacy, ContentPageKeys.Terms];

    public static TheoryData<string> Pages => new() { ContentPageKeys.About, ContentPageKeys.Privacy, ContentPageKeys.Terms };

    private static string BodyOf(string key) => PolicyPageContent.Bodies[key];

    /// <summary>The words a reader would read: tags removed, entities left alone (the copy has none).</summary>
    private static string TextOf(string key) =>
        TagPattern().Replace(BodyOf(key), " ");

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\b\w+\b")]
    private static partial Regex WordPattern();

    // ---- length and shape -----------------------------------------------------------------------------------

    /// <summary>
    /// Each page is as long as the issue asked for (about 250 to 350 words for About, 600 to 900 for the other two),
    /// with a margin of about a tenth either side for the way words are counted. Privacy is allowed more: it was cut
    /// from over a thousand words and then grew again when review found data the first draft left out (PayPal's
    /// notifications, the account's phone number and district, the references PayPal receives), each of which a
    /// privacy policy should say. A page that has quietly doubled or collapsed is what this catches.
    /// </summary>
    [Theory]
    [InlineData(ContentPageKeys.About, 225, 385)]
    [InlineData(ContentPageKeys.Privacy, 600, 1120)]
    [InlineData(ContentPageKeys.Terms, 540, 990)]
    public void A_page_is_the_length_it_was_asked_to_be(string key, int minimum, int maximum)
    {
        var words = WordPattern().Count(TextOf(key));

        Assert.InRange(words, minimum, maximum);
    }

    /// <summary>
    /// The page component draws the title as the one <c>h1</c>, so a body must not carry a second, and must lead with
    /// a paragraph, not a heading, so the page opens with sentences rather than a heading under a heading.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void A_page_has_no_h1_and_opens_with_a_paragraph(string key)
    {
        var body = BodyOf(key);

        Assert.DoesNotContain("<h1", body, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("<p>", body, StringComparison.Ordinal);
        Assert.Contains("<h2>", body, StringComparison.Ordinal);
    }

    /// <summary>Headings are not skipped: an <c>h3</c> would need an <c>h2</c> above it, and these use only <c>h2</c>.</summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void A_page_uses_only_h2_below_the_title(string key)
    {
        Assert.DoesNotMatch(new Regex("<h[3-6]", RegexOptions.IgnoreCase), BodyOf(key));
    }

    /// <summary>
    /// No attribute and no link at all: a hard-coded link breaks when staff rename a slug (the footer reads the live
    /// list for that reason), so the copy names the page and says it is linked at the foot of every page, as the
    /// Shipping and Returns copy does.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void A_page_carries_no_attribute_and_no_hard_coded_link(string key)
    {
        var body = BodyOf(key);

        Assert.DoesNotContain("<a ", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex(@"<[a-z0-9]+\s+[a-z]", RegexOptions.IgnoreCase), body);
        Assert.DoesNotContain("http://", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("www.", body, StringComparison.OrdinalIgnoreCase);
    }

    // ---- nothing left unfinished ----------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Pages))]
    public void A_page_has_no_placeholder_left_in_it(string key)
    {
        var text = TextOf(key);

        foreach (var marker in new[] { "TODO", "TBD", "TBC", "lorem", "ipsum", "XXX", "FIXME", "[", "]", "{", "}", "<<", ">>" })
        {
            Assert.DoesNotContain(marker, BodyOf(key), StringComparison.OrdinalIgnoreCase);
        }

        // Template tokens are the other kind of leftover: a page that still says {{something}} or %s.
        Assert.DoesNotMatch(new Regex(@"\{\{|\}\}|%[sd]|\$\{"), text);
    }

    /// <summary>
    /// The shop is named by the one constant, so the copy reads the same name everywhere the constant is read, and
    /// the name is actually there (an interpolation that came out empty would otherwise pass every other test).
    /// <c>BrandNameConventionTests</c> is what stops anyone spelling it out in source.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void A_page_names_the_shop_through_the_constant_and_the_company_once_in_full(string key)
    {
        var body = BodyOf(key);

        Assert.Contains(StoreBrand.Name, body, StringComparison.Ordinal);
        Assert.Contains("Spencer Denim Industries Pvt Ltd", body, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(body, Regex.Escape("Spencer Denim Industries Pvt Ltd")));
    }

    // ---- nothing invented -----------------------------------------------------------------------------------

    /// <summary>
    /// The phrases an invented story or invented credential would use. The brief for these pages was no founding
    /// year, award, staff, number or address, because none of those is in the repository or in the client's
    /// documents. A block-list is weak, and honest about it: it catches the careless case, and the tester's fact
    /// table catches the rest.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void A_page_makes_none_of_the_claims_nobody_has_confirmed(string key)
    {
        var body = BodyOf(key);

        foreach (var phrase in new[]
        {
            "founded", "established in", "since 20", "years of experience", "award", "family-owned",
            "family-run", "family business", "handcrafted", "best price", "number one", "best in", "world-class",
            "award-winning", "guarantee", "100%", "free delivery", "free shipping", "free returns",
        })
        {
            Assert.DoesNotContain(phrase, body, StringComparison.OrdinalIgnoreCase);
        }

        // "Est. 2024" as the logo prints it: a year the client has not confirmed. A bare substring "est." would also
        // match the end of "request." and "latest.", so this is a pattern: the abbreviation, then a year.
        Assert.DoesNotMatch(new Regex(@"\bEst\.?\s*\d{4}", RegexOptions.IgnoreCase), TextOf(key));
    }

    /// <summary>
    /// No telephone number and no email address: the Contact page and the <c>Contact.*</c> settings are where those
    /// live, and a number typed into copy would contradict them the first time the client changed it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void A_page_writes_out_no_phone_number_email_address_or_street(string key)
    {
        var text = TextOf(key);

        Assert.DoesNotMatch(new Regex(@"\+?\d[\d\s\-()]{7,}\d"), text);
        Assert.DoesNotMatch(new Regex(@"[\w.+-]+@[\w-]+\.[\w.]+"), text);
        Assert.DoesNotMatch(new Regex(@"\b(Road|Street|Mawatha|Lane|Avenue|Place)\b"), text);
        Assert.DoesNotContain("@", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// No figure that the owner can change in Settings or that lives in a table: a delivery fee, an exchange rate, a
    /// payment deadline, a buffer percentage, a retention period other than the standard one the policy names. Those
    /// numbers are stated once, where they are edited, so these pages cannot go stale when the owner changes them.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void A_page_quotes_no_fee_or_rate_the_owner_can_change(string key)
    {
        var text = TextOf(key);

        Assert.DoesNotMatch(new Regex(@"\bRs\.?\s*\d|\bLKR\s*\d|\bUSD\s*\d|\$\s*\d"), text);
        Assert.DoesNotMatch(new Regex(@"\b\d+(\.\d+)?\s*%"), text);
    }

    /// <summary>
    /// About and Terms quote no duration at all: the payment deadlines (48 hours for a bank transfer, 60 minutes for
    /// PayPal) are settings, so those pages say "the deadline shown on your order page". The Privacy policy is the
    /// one page that states durations, and it states exactly the ones the code fixes, pinned in
    /// <see cref="The_privacy_policy_states_the_cookie_lifetimes_the_code_sets"/>.
    /// </summary>
    [Theory]
    [InlineData(ContentPageKeys.About)]
    [InlineData(ContentPageKeys.Terms)]
    public void About_and_terms_quote_no_duration(string key)
    {
        Assert.DoesNotMatch(
            new Regex(@"\b\d+\s*(hours?|minutes?|days?|weeks?|months?|years?)\b", RegexOptions.IgnoreCase),
            TextOf(key));
    }

    // ---- the specific promises -------------------------------------------------------------------------------

    /// <summary>
    /// The numbers that DO appear are the ones the code fixes and the policy cannot get wrong by editing a setting:
    /// the Act's number and year, the cookies' lifetimes and the visit session's idle time. Pinned here so a change
    /// to a cookie's lifetime in code shows up as a failing test that names the policy sentence to update.
    /// </summary>
    [Fact]
    public void The_privacy_policy_states_the_cookie_lifetimes_the_code_sets()
    {
        var body = BodyOf(ContentPageKeys.Privacy);

        Assert.Contains("lasts 30 days from your last visit", body, StringComparison.Ordinal);
        Assert.Contains("for up to 30 days", body, StringComparison.Ordinal);
        Assert.Contains("expires after 30 minutes", body, StringComparison.Ordinal);
        Assert.Contains("replaced after 30 minutes", body, StringComparison.Ordinal);

        Assert.Equal(TimeSpan.FromDays(30), SpencerDenim.Api.Http.CartCookie.Lifetime);
        Assert.Equal(TimeSpan.FromMinutes(30), SpencerDenim.Api.Http.VisitSessionCookie.IdleLifetime);
        Assert.Equal(60 * 24 * 30, new SpencerDenim.Infrastructure.Identity.Customers.CustomerSecurityOptions().CookieLifetimeMinutes);
    }

    [Fact]
    public void The_privacy_policy_names_the_act_and_every_cookie_the_shopper_receives()
    {
        var body = BodyOf(ContentPageKeys.Privacy);

        Assert.Contains("Personal Data Protection Act, No. 9 of 2022", body, StringComparison.Ordinal);

        foreach (var cookie in new[] { "sd_cart", "sd_customer_auth", "sd_vs" })
        {
            Assert.Contains($"<strong>{cookie}</strong>", body, StringComparison.Ordinal);
        }

        // And only those: an admin or antiforgery cookie is for staff and is not a shopper's.
        Assert.DoesNotContain("sd_admin", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_privacy_policy_gives_no_response_time_and_promises_no_automatic_deletion_beyond_what_is_built()
    {
        var text = TextOf(ContentPageKeys.Privacy);

        // The owner and a lawyer decide how fast a request is answered; the policy promises no deadline, so it does
        // not say "within the time the Act requires" (nobody here can say what that is, or that the shop meets it).
        // The only day counts anywhere in the policy are the three that code fixes or defaults (two cookie lifetimes
        // and the 90-day standard for visit records), so removing exactly those leaves no number of days at all. The
        // backups sentence carries no number: the backup script does not prune (deploy/scripts/backup-db.sh), so the
        // real retention is the host's and is on the owner's list.
        var withoutTheKnownDurations = text
            .Replace("30 days from your last visit", string.Empty, StringComparison.Ordinal)
            .Replace("up to 30 days", string.Empty, StringComparison.Ordinal)
            .Replace("90 days as standard", string.Empty, StringComparison.Ordinal);

        Assert.DoesNotMatch(new Regex(@"\b\d+\s*(working\s+)?days?\b", RegexOptions.IgnoreCase), withoutTheKnownDurations);
        Assert.DoesNotContain("within the time", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("35 days", text, StringComparison.Ordinal);
        Assert.Contains(
            "Backups are kept for a limited time, and data deleted from the shop can remain in them until they expire.",
            text,
            StringComparison.Ordinal);

        // The one retention job that exists is the page-view purge: it is named, and nothing else is. In particular
        // the order and enquiry IP address purges that docs/11 describes are NOT built, so none is promised.
        Assert.Contains("Visit records are removed automatically after a set period (90 days as standard)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("deleted automatically", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anonymised automatically", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Terms say which payment is for which delivery, the way issue #253 makes it true: PayPal for every order and
    /// for every overseas one, bank transfer only possibly and only for Sri Lanka.
    /// </summary>
    [Fact]
    public void The_terms_say_paypal_for_everyone_and_bank_transfer_for_sri_lanka_only()
    {
        var text = TextOf(ContentPageKeys.Terms);

        Assert.Contains("PayPal is available for every order and is the way to pay for a delivery outside Sri Lanka", text, StringComparison.Ordinal);
        Assert.Contains("For a delivery within Sri Lanka we may also offer payment by bank transfer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("cash on delivery", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The terms name the US dollar charge honestly and do not state the buffer's size, which is a setting.</summary>
    [Fact]
    public void The_terms_describe_the_dollar_conversion_without_quoting_the_buffer()
    {
        var text = TextOf(ContentPageKeys.Terms);

        Assert.Contains("PayPal takes payments in US dollars", text, StringComparison.Ordinal);
        Assert.Contains("a small allowance for currency movement", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\b3\s*(%|per\s*cent)", RegexOptions.IgnoreCase), text);
    }

    [Fact]
    public void The_pages_say_nothing_about_refund_deadlines_that_the_returns_page_owns()
    {
        // The Returns page states the 14 days and the 7 working days; copying them here would put one policy in two
        // places, and the owner edits only one of them.
        foreach (var key in PageKeys)
        {
            var text = TextOf(key);

            Assert.DoesNotContain("14 days", text, StringComparison.Ordinal);
            Assert.DoesNotContain("7 working days", text, StringComparison.Ordinal);
            Assert.DoesNotContain("48 hours", text, StringComparison.Ordinal);
        }
    }

    // ---- what the review found missing or untrue -------------------------------------------------------------

    /// <summary>
    /// The Privacy policy says everything the code collects or sends, each in its own words so a later edit that drops
    /// one fails by name: PayPal's notifications (stored as PayPal sent them, payer name and email included), the
    /// account's phone number and default district, the order's IP address, and what PayPal receives (the order total,
    /// the recipient, a reference to the order and the shop's name). Proof for each is in the #252 journal entry.
    /// </summary>
    [Theory]
    [InlineData("PayPal sends us notifications about the payment. We keep each one as PayPal sent it, and it can include your name and email address.")]
    [InlineData("if you add them, your phone number and default delivery district")]
    [InlineData("the IP address the order was placed from")]
    [InlineData("a reference to your order and our shop name")]
    [InlineData("the recipient's name and delivery address")]
    [InlineData("if you upload a bank transfer payment slip, that slip")]
    [InlineData("we record that with the IP address and browser details")]
    [InlineData("To slow down repeated requests, the server also uses the IP address a request comes from")]
    public void The_privacy_policy_says_what_the_code_collects_and_sends(string fragment)
    {
        Assert.Contains(fragment, TextOf(ContentPageKeys.Privacy), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Nothing in the shop implements unsubscribing</b>: the subscriber's <c>UnsubscribeToken</c> is generated and
    /// used nowhere, no endpoint sets <c>UnsubscribedAtUtc</c>, no link exists. So no page may promise a self-service
    /// way out. The Privacy policy says to ask through the Contact page and that staff remove the address (an
    /// operational promise on the owner's list). When the unsubscribe feature ships this test changes with the copy.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void No_page_promises_a_self_service_unsubscribe_the_shop_does_not_have(string key)
    {
        Assert.DoesNotMatch(new Regex("unsubscrib", RegexOptions.IgnoreCase), TextOf(key));
    }

    [Fact]
    public void The_privacy_policy_says_how_to_stop_the_newsletter_and_withdraw_a_consent_today()
    {
        var text = TextOf(ContentPageKeys.Privacy);

        Assert.Contains("to stop receiving the newsletter or to withdraw any other consent, write to us through the Contact page", text, StringComparison.Ordinal);
        Assert.Contains("When you ask us to stop the newsletter, we remove you from the list.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Shipping page draws only the Sri Lankan fee list; overseas fees, times and whether a country is served are
    /// at checkout (<c>shipping-rates.ts</c>, <c>delivery-country.service.ts</c>). The Terms must say that and not that
    /// everything is on the delivery page.
    /// </summary>
    [Fact]
    public void The_terms_put_overseas_fees_times_and_countries_at_checkout_and_only_sri_lankan_ones_on_the_delivery_page()
    {
        var text = TextOf(ContentPageKeys.Terms);

        Assert.Contains("Within Sri Lanka, delivery fees and estimated delivery times are on our delivery page and at checkout.", text, StringComparison.Ordinal);
        Assert.Contains(
            "For an overseas delivery, the fee, the estimated delivery time and whether we deliver to your country are shown at checkout before you pay.",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("the countries we deliver to are shown at checkout and on", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Last updated" is the row's own date and the page keeps no versions, so the Terms must not say that the version
    /// on the page when you order is the one that applies. The seeder stamps <c>UpdatedAtUtc</c> when it fills a page
    /// (integration test), so the date the page shows is the date the copy arrived.
    /// </summary>
    [Fact]
    public void The_terms_do_not_claim_a_version_history_the_page_does_not_keep()
    {
        var text = TextOf(ContentPageKeys.Terms);

        Assert.DoesNotContain("version on this page", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The date at the top of this page shows when they were last changed.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The review form says first name and initial are shown; it does not ask the reviewer to confirm honesty or to
    /// grant permission, so the Terms must not say the reviewer does.
    /// </summary>
    [Fact]
    public void The_terms_describe_the_review_form_as_it_is()
    {
        var text = TextOf(ContentPageKeys.Terms);

        Assert.DoesNotContain("you confirm", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("you allow us", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("the review form tells you that your first name and the initial of your last name are shown with it", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A page title is the client's label and can be renamed, so the copy refers to the delivery and returns pages
    /// generically and never by the seeded titles. (The Shipping and Returns copy of version 1 does name them; it is
    /// already in people's databases and is left as the owner approved it.)
    /// </summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void A_page_never_names_another_page_by_a_title_an_admin_can_rename(string key)
    {
        var text = TextOf(key);

        foreach (var title in new[] { "Delivery and shipping", "Returns and exchanges", "Privacy policy", "Terms and conditions", "Contact us" })
        {
            Assert.DoesNotContain(title, text, StringComparison.Ordinal);
        }
    }

    /// <summary>The shipping page says the payment deadline is in the order email; the order page shows it too. The Terms name both.</summary>
    [Fact]
    public void The_terms_and_the_shipping_page_agree_about_where_the_payment_deadline_is_shown()
    {
        Assert.Contains("the deadline shown on your order page and in your order email", TextOf(ContentPageKeys.Terms), StringComparison.Ordinal);
        Assert.Contains("deadline in your order email", PolicyPageContent.ShippingBody, StringComparison.Ordinal);
    }

    /// <summary>The Returns page lets you cancel before dispatch and says paid orders are refunded; the Terms say the same without promising a button.</summary>
    [Fact]
    public void The_terms_and_the_returns_page_agree_about_cancelling()
    {
        var terms = TextOf(ContentPageKeys.Terms);

        Assert.Contains("You can cancel an unpaid order yourself from your order page.", terms, StringComparison.Ordinal);
        Assert.Contains("To cancel an order you have already paid for, contact us before we dispatch it.", terms, StringComparison.Ordinal);
        Assert.Contains("You can cancel any time before we dispatch your order.", PolicyPageContent.ReturnsBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// A promise the owner cannot keep word for word is softened: "we aim to reply", "we aim to handle your data in
    /// line with the Act". And "linked at the foot of every page" is said at most once on a page.
    /// </summary>
    [Fact]
    public void Promises_the_owner_cannot_guarantee_are_worded_as_aims()
    {
        Assert.Contains("we aim to reply", TextOf(ContentPageKeys.About), StringComparison.Ordinal);
        Assert.DoesNotContain("our team will reply", TextOf(ContentPageKeys.About), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("We aim to handle your personal data in line with", TextOf(ContentPageKeys.Privacy), StringComparison.Ordinal);
        Assert.DoesNotContain("We handle your personal data in line with", TextOf(ContentPageKeys.Privacy), StringComparison.Ordinal);
        Assert.DoesNotContain("Pay securely", TextOf(ContentPageKeys.About), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void The_phrase_linked_at_the_foot_of_every_page_is_said_at_most_once(string key)
    {
        Assert.True(Regex.Count(TextOf(key), "linked at the foot of every page") <= 1, key);
    }

    /// <summary>The rights and the payment/delivery points are lists, not one long sentence.</summary>
    [Fact]
    public void The_longest_sentences_are_lists()
    {
        Assert.Matches(new Regex(@"<p>Under the Personal Data Protection Act you may:</p><ul>(<li>[^<]+</li>){5}</ul>"), BodyOf(ContentPageKeys.Privacy));
        Assert.True(Regex.Count(BodyOf(ContentPageKeys.Terms), "<ul>") >= 4);
    }

    // ---- the copy lint, and the six edits it exists to catch ------------------------------------------------

    private static IReadOnlySet<string> RealCookieNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        SpencerDenim.Api.Http.CartCookie.Name,
        SpencerDenim.Api.Http.VisitSessionCookie.Name,
        SpencerDenim.Infrastructure.Identity.Customers.CustomerAuthenticationDefaults.CookieName,
    };

    [Fact]
    public void The_cookie_names_the_lint_checks_against_are_the_three_the_code_sets()
    {
        Assert.Equal(["sd_cart", "sd_customer_auth", "sd_vs"], RealCookieNames.Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void The_copy_passes_the_lint(string key)
    {
        var violations = StorePagesLint.Check(key, BodyOf(key), RealCookieNames);

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations.Select(v => $"[{v.Rule}] {v.Detail}")));
    }

    /// <summary>
    /// The six edits the #252 tester made to the first version of these tests, every one of which survived all of them.
    /// Each is applied in memory to the real copy and must fire the rule that exists for it. If a rule is ever loosened
    /// or removed, the edit it was written for comes back green here and this test is what says so.
    /// </summary>
    [Theory]
    [InlineData("sell personal data to partners", ContentPageKeys.Privacy, StorePagesLint.ForbiddenPhrase)]
    [InlineData("courier given email and purchase history", ContentPageKeys.Privacy, StorePagesLint.PinnedSentence)]
    [InlineData("trading since 1998", ContentPageKeys.About, StorePagesLint.UnlistedNumber)]
    [InlineData("more than 10,000 happy customers", ContentPageKeys.About, StorePagesLint.UnlistedNumber)]
    [InlineData("a fourth cookie sd_prefs", ContentPageKeys.Privacy, StorePagesLint.CookieName)]
    [InlineData("US spelling organized", ContentPageKeys.About, StorePagesLint.UsSpelling)]
    public void Each_mutation_the_tester_ran_is_caught_by_the_lint_rule_meant_to_catch_it(string mutation, string key, string expectedRule)
    {
        var mutated = Mutate(mutation, BodyOf(key));

        Assert.NotEqual(BodyOf(key), mutated);

        var violations = StorePagesLint.Check(key, mutated, RealCookieNames);

        Assert.Contains(violations, violation => violation.Rule == expectedRule);
    }

    /// <summary>More edits of the same kinds, so a rule that catches only the exact tester wording is not enough.</summary>
    [Theory]
    [InlineData("We do not sell your personal data. We may disclose it where the law requires us to.", "We never sell your personal data to anyone except our partners.", ContentPageKeys.Privacy, StorePagesLint.ForbiddenPhrase)]
    [InlineData("<h2>Get in touch</h2>", "<h2>Get in touch</h2><p>We are the best online fashion shop in Sri Lanka.</p>", ContentPageKeys.About, StorePagesLint.ForbiddenPhrase)]
    [InlineData("<h2>Get in touch</h2>", "<h2>Get in touch</h2><p>Over 5000 orders delivered.</p>", ContentPageKeys.About, StorePagesLint.UnlistedNumber)]
    [InlineData("<h2>Get in touch</h2>", "<h2>Get in touch</h2><p>Satisfaction guaranteed, 100% of the time.</p>", ContentPageKeys.About, StorePagesLint.ForbiddenPhrase)]
    [InlineData("<h2>Get in touch</h2>", "<h2>Get in touch</h2><p>We are always here.</p>", ContentPageKeys.About, StorePagesLint.ForbiddenPhrase)]
    [InlineData("<h2>Get in touch</h2>", "<h2>Get in touch</h2><p>Unsubscribe from any email.</p>", ContentPageKeys.About, StorePagesLint.ForbiddenPhrase)]
    [InlineData("<h2>Contact</h2>", "<h2>Contact</h2><p>Our favorite color is blue.</p>", ContentPageKeys.Terms, StorePagesLint.UsSpelling)]
    [InlineData("<h2>Contact</h2>", "<h2>Contact</h2><p>We recognize your rights.</p>", ContentPageKeys.Terms, StorePagesLint.UsSpelling)]
    [InlineData("<h2>Contact</h2>", "<h2>Contact</h2><p>sd_admin keeps staff signed in.</p>", ContentPageKeys.Terms, StorePagesLint.CookieName)]
    [InlineData("<strong>sd_vs</strong>", "<strong>sd_vsx</strong>", ContentPageKeys.Privacy, StorePagesLint.CookieName)]
    public void Other_edits_of_the_same_kinds_are_caught_too(string find, string replacement, string key, string expectedRule)
    {
        var body = BodyOf(key);

        Assert.Contains(find, body, StringComparison.Ordinal);

        var violations = StorePagesLint.Check(key, body.Replace(find, replacement, StringComparison.Ordinal), RealCookieNames);

        Assert.Contains(violations, violation => violation.Rule == expectedRule);
    }

    /// <summary>The tester's edits, as functions of a body. Each changes the body, or the test above says so.</summary>
    private static string Mutate(string mutation, string body) => mutation switch
    {
        "sell personal data to partners" => body.Replace(
            "We do not sell your personal data.",
            "We may sell your personal data to partners.",
            StringComparison.Ordinal),
        "courier given email and purchase history" => body.Replace(
            "your name, delivery address and telephone number.</li>",
            "your name, email address, purchase history and delivery address.</li>",
            StringComparison.Ordinal),
        "trading since 1998" => body.Replace("<h2>Get in touch</h2>", "<h2>Get in touch</h2><p>We have been trading since 1998.</p>", StringComparison.Ordinal),
        "more than 10,000 happy customers" => body.Replace("<h2>Get in touch</h2>", "<h2>Get in touch</h2><p>More than 10,000 happy customers.</p>", StringComparison.Ordinal),
        "a fourth cookie sd_prefs" => body.Replace(
            "<li><strong>sd_vs</strong>",
            "<li><strong>sd_prefs</strong> remembers your choices.</li><li><strong>sd_vs</strong>",
            StringComparison.Ordinal),
        "US spelling organized" => body.Replace("organised", "organized", StringComparison.Ordinal),
        _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation."),
    };

    // ---- the browser suite draws the same words ---------------------------------------------------------------

    /// <summary>
    /// The Playwright stub serves these three pages from <c>e2e/support/store-pages.json</c>, so the browser tests
    /// (headings, lists, overflow at 390 and 1280 px, axe) draw exactly the words the shop ships rather than a
    /// stand-in that drifts. This is what stops that file drifting: it must equal the seeded bodies, character for
    /// character, and the failure says where the two part.
    /// <para>
    /// To refresh it after editing the copy, write the three <see cref="PolicyPageContent.Bodies"/> values for
    /// <c>about</c>, <c>privacy</c> and <c>terms</c> into that file as JSON strings.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void The_browser_suite_fixture_is_the_seeded_copy_verbatim(string key)
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "frontend", "e2e", "support", "store-pages.json");
        Assert.True(File.Exists(path), $"Expected the browser fixture at {path}.");

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var fixture = document.RootElement.GetProperty(key).GetString();
        var seeded = BodyOf(key);

        Assert.NotNull(fixture);

        var firstDifference = fixture.Zip(seeded, (left, right) => left == right).TakeWhile(same => same).Count();

        Assert.True(
            string.Equals(fixture, seeded, StringComparison.Ordinal),
            $"e2e/support/store-pages.json no longer matches PolicyPageContent.{key} - they part at character "
            + $"{firstDifference} (fixture {fixture.Length} characters, seeded {seeded.Length}). Refresh the fixture.");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SpencerDenim.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate SpencerDenim.sln above the test output directory.");
    }

    // ---- a title is a label; the page component supplies it ---------------------------------------------------

    [Theory]
    [MemberData(nameof(Pages))]
    public void A_page_does_not_hand_write_a_date(string key)
    {
        // "Last updated" is drawn by the page component from the row's own dates.
        Assert.DoesNotMatch(new Regex(@"last\s+updated|\b20\d\d\b", RegexOptions.IgnoreCase), TextOf(key).Replace("No. 9 of 2022", string.Empty));
    }
}
