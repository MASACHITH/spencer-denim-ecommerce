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
    /// with a margin of about a tenth either side for the way words are counted. A page that has quietly grown or
    /// collapsed is the thing this catches: the Privacy draft was first written at over a thousand words and was cut
    /// back, and this is what keeps it cut.
    /// </summary>
    [Theory]
    [InlineData(ContentPageKeys.About, 225, 385)]
    [InlineData(ContentPageKeys.Privacy, 540, 990)]
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

        // The owner and a lawyer set the response time; the policy points at the Act's own. The only day counts
        // anywhere in the policy are the four that code or a design document fixes (two cookie lifetimes, the 90-day
        // standard for visit records, and the 35 days deleted data can stay in backups, which doc 08 section B2 asks
        // the policy to say), so removing exactly those leaves no number of days at all.
        var withoutTheKnownDurations = text
            .Replace("30 days from your last visit", string.Empty, StringComparison.Ordinal)
            .Replace("up to 30 days", string.Empty, StringComparison.Ordinal)
            .Replace("90 days as standard", string.Empty, StringComparison.Ordinal)
            .Replace("up to 35 days", string.Empty, StringComparison.Ordinal);

        Assert.DoesNotMatch(new Regex(@"\b\d+\s*(working\s+)?days?\b", RegexOptions.IgnoreCase), withoutTheKnownDurations);
        Assert.Contains("within the time the Act requires", text, StringComparison.Ordinal);
        Assert.Contains("Deleted data may remain in our backups for up to 35 days", text, StringComparison.Ordinal);

        // The one retention job that exists is the page-view purge: it is named, and nothing else is.
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
