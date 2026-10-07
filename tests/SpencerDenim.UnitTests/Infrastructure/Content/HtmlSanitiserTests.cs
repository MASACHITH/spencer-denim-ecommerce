using SpencerDenim.Infrastructure.Content;

namespace SpencerDenim.UnitTests.Infrastructure.Content;

/// <summary>
/// The allow-list HTML sanitiser (GLB-10, doc 08 §3.7/§3.8).
/// <para>
/// <b>This is the one test class in this task whose failure is a security incident</b>, so it is written
/// as a table of real payloads rather than as prose about intent: an admin-editable body rendered as
/// markup on a public page is a stored-XSS surface, and the only evidence that the allow-list holds is a
/// list of things it refuses.
/// </para>
/// <para>
/// <b>Every case here can fail.</b> Checked by mutation while writing them: replacing the allow-list with
/// <c>HtmlSanitizer</c>'s permissive defaults turns 12 of them red, and removing the
/// <c>AllowedSchemes</c> restriction alone turns the <c>javascript:</c> and <c>data:</c> cases red. A
/// payload test that passes against a sanitiser that does nothing is worse than no test, because it reads
/// as coverage.
/// </para>
/// </summary>
public sealed class HtmlSanitiserTests
{
    private readonly HtmlSanitiser _sanitiser = new();

    /// <summary>
    /// Payloads that must not survive, with the fragment that proves they did not.
    /// <para>
    /// Each row is <c>(input, mustNotContain)</c>. The assertion is on the <i>dangerous</i> fragment
    /// rather than on the whole output, because <c>KeepChildNodes</c> deliberately keeps the harmless
    /// text — "the alert text is still there" is correct behaviour and asserting equality would pin the
    /// wrong thing.
    /// </para>
    /// </summary>
    public static TheoryData<string, string> Dangerous => new()
    {
        // The canonical case.
        { "<p>Hello</p><script>alert(1)</script>", "<script" },
        { "<SCRIPT SRC=//evil.example/x.js></SCRIPT>", "script" },

        // Parser confusion: a nested opening tag that a regular-expression pass reassembles into
        // <script> after it has stripped the inner one. This is the single most important row here.
        { "<scr<script>ipt>alert(1)</script>", "<script" },
        { "<<SCRIPT>alert(1);//<</SCRIPT>", "<script" },

        // Event handlers, on an element that is otherwise allowed and on one that is not.
        { "<img src=\"x\" onerror=\"alert(1)\">", "onerror" },
        { "<p onclick=\"alert(1)\">text</p>", "onclick" },
        { "<body onload=alert(1)>", "onload" },
        { "<svg onload=alert(1)></svg>", "onload" },
        { "<svg><animate onbegin=alert(1) attributeName=x dur=1s></animate></svg>", "onbegin" },

        // javascript: in both URI attributes, and with the obfuscations that defeat a string compare.
        { "<a href=\"javascript:alert(1)\">x</a>", "javascript" },
        { "<a href=\"JaVaScRiPt:alert(1)\">x</a>", "javascript" },
        { "<a href=\"java\tscript:alert(1)\">x</a>", "script:" },
        { "<img src=\"javascript:alert(1)\">", "javascript" },

        // data: URLs. An SVG data URL is script execution in an <img> in several browsers.
        { "<img src=\"data:image/svg+xml;base64,PHN2Zy8+\">", "data:" },
        { "<a href=\"data:text/html,<script>alert(1)</script>\">x</a>", "data:" },
        { "<a href=\"vbscript:msgbox(1)\">x</a>", "vbscript" },

        // Framing and styling, both of which are elements the allow-list does not have.
        { "<iframe src=\"https://evil.example\"></iframe>", "<iframe" },
        { "<style>body{background:url(javascript:alert(1))}</style>", "<style" },
        { "<p style=\"background:url(javascript:alert(1))\">x</p>", "style=" },
        { "<object data=\"x.swf\"></object>", "<object" },
        { "<embed src=\"x.swf\">", "<embed" },
        { "<form action=\"https://evil.example\"><input name=\"p\"></form>", "<form" },
        { "<base href=\"https://evil.example/\">", "<base" },
        { "<meta http-equiv=\"refresh\" content=\"0;url=https://evil.example\">", "<meta" },
        { "<link rel=\"stylesheet\" href=\"https://evil.example/x.css\">", "<link" },

        // A comment hiding markup, in the two shapes that parse differently.
        { "<!-- <script>alert(1)</script> -->", "<script" },
        { "<!--[if IE]><script>alert(1)</script><![endif]-->", "<script" },

        // Malformed and unclosed, where the browser's error recovery is the attack.
        { "<img src=x alt=\"\" onerror=alert(1)//", "onerror" },
        { "<a href=\"javascript:alert(1)\"<b>x</b>", "javascript" },

        // Namespaced and template content.
        { "<math><mtext><table><mglyph><style><img src=x onerror=alert(1)>", "onerror" },
        { "<template><script>alert(1)</script></template>", "<script" },
    };

    [Theory]
    [MemberData(nameof(Dangerous))]
    public void It_removes_the_payload(string input, string mustNotContain)
    {
        var result = _sanitiser.Sanitise(input);

        Assert.DoesNotContain(mustNotContain, result.Html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The other half: markup a client will actually paste, which must survive intact. A sanitiser that
    /// passes every row above by returning the empty string is useless, and this is what rules that out.
    /// </summary>
    public static TheoryData<string, string> Kept => new()
    {
        { "<p>Our studio is in Colombo.</p>", "<p>Our studio is in Colombo.</p>" },
        { "<h2>Returns</h2>", "<h2>Returns</h2>" },
        { "<ul><li>One</li><li>Two</li></ul>", "<ul><li>One</li><li>Two</li></ul>" },
        { "<p><strong>Bold</strong> and <em>italic</em></p>", "<strong>Bold</strong>" },
        { "<a href=\"https://example.com\">Link</a>", "href=\"https://example.com\"" },
        { "<a href=\"mailto:hello@example.com\">Email</a>", "mailto:hello@example.com" },
        { "<a href=\"/returns\">Our returns page</a>", "href=\"/returns\"" },
        { "<img src=\"/media/x.webp\" alt=\"A linen shirt\">", "alt=\"A linen shirt\"" },
        { "<table><tr><th scope=\"col\">Zone</th><td colspan=\"2\">Fee</td></tr></table>", "colspan=\"2\"" },
        { "<blockquote>Quality first.</blockquote>", "<blockquote>Quality first.</blockquote>" },
    };

    [Theory]
    [MemberData(nameof(Kept))]
    public void It_keeps_the_markup_a_client_will_paste(string input, string mustContain)
    {
        var result = _sanitiser.Sanitise(input);

        Assert.Contains(mustContain, result.Html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A disallowed element loses the tag and keeps the words. The alternative — deleting the element with
    /// its contents — is the silent-data-loss shape this project has shipped three times, and a client
    /// pasting from Word produces <c>&lt;div&gt;</c> and <c>&lt;span&gt;</c> around every paragraph.
    /// </summary>
    [Fact]
    public void A_disallowed_wrapper_loses_the_tag_and_keeps_the_words()
    {
        var result = _sanitiser.Sanitise("<div><span>Our story</span></div>");

        Assert.DoesNotContain("<div", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span", result.Html, StringComparison.Ordinal);
        Assert.Contains("Our story", result.Html, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>h1</c> is the page title, which the storefront renders itself. A second one on the page is an
    /// accessibility defect (GLB-12), so the element is not allowed — and the copy inside it is kept.
    /// </summary>
    [Fact]
    public void An_h1_in_a_body_loses_its_tag_and_keeps_its_words()
    {
        var result = _sanitiser.Sanitise("<h1>About us</h1><p>We make clothes.</p>");

        Assert.DoesNotContain("<h1", result.Html, StringComparison.Ordinal);
        Assert.Contains("About us", result.Html, StringComparison.Ordinal);
        Assert.Contains("<p>We make clothes.</p>", result.Html, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>target</c> is not on the list, so a link cannot open a new tab — which is also what stops
    /// reverse tabnabbing without relying on an editor remembering <c>rel="noopener"</c>.
    /// </summary>
    [Fact]
    public void A_link_cannot_open_a_new_tab()
    {
        var result = _sanitiser.Sanitise("<a href=\"https://example.com\" target=\"_blank\">x</a>");

        Assert.DoesNotContain("target", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"https://example.com\"", result.Html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A body that was nothing but a payload ends as the empty string, not as whitespace. The rest of the
    /// system reads an empty body as "the client has not written this page yet", and <c>"\n\n"</c> is not
    /// empty to any of the checks that decide that.
    /// </summary>
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("   ")]
    [InlineData("<p></p>")]
    [InlineData("<p>   </p><div></div>")]
    [InlineData(null)]
    public void A_body_that_renders_nothing_is_empty(string? input)
    {
        Assert.Equal(string.Empty, _sanitiser.Sanitise(input).Html);
    }

    /// <summary>An image or a rule is content even with no text beside it, so it is not "nothing".</summary>
    [Theory]
    [InlineData("<img src=\"/media/x.webp\" alt=\"A shirt\">")]
    [InlineData("<hr>")]
    public void An_image_or_a_rule_on_its_own_is_still_content(string input)
    {
        Assert.NotEqual(string.Empty, _sanitiser.Sanitise(input).Html);
    }

    /// <summary>
    /// The editor is told what was taken out. A save that silently dropped half a paste is the defect
    /// shape this project has shipped repeatedly; the report is what makes it visible on the screen.
    /// </summary>
    [Fact]
    public void It_says_what_it_removed()
    {
        var result = _sanitiser.Sanitise(
            "<p onclick=\"x\">Hi</p><script>alert(1)</script><!-- hidden -->");

        Assert.Contains("<script>", result.Removed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("onclick", result.Removed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("an HTML comment", result.Removed, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Clean input produces an empty report, so the screen has nothing to warn about.</summary>
    [Fact]
    public void Clean_input_removes_nothing()
    {
        Assert.Empty(_sanitiser.Sanitise("<p>Hello</p>").Removed);
    }

    /// <summary>
    /// Doc 08 §3.3 and GLB-12: a public image needs alt text before the content using it is published.
    /// The sanitiser counts them, because it is the one component that has already parsed the body.
    /// </summary>
    [Theory]
    [InlineData("<img src=\"/a.webp\" alt=\"A shirt\">", 1, 0)]
    [InlineData("<img src=\"/a.webp\">", 1, 1)]
    [InlineData("<img src=\"/a.webp\" alt=\"\">", 1, 1)]
    [InlineData("<img src=\"/a.webp\" alt=\"   \">", 1, 1)]
    [InlineData("<img src=\"/a.webp\" alt=\"A\"><img src=\"/b.webp\">", 2, 1)]
    [InlineData("<p>No pictures</p>", 0, 0)]
    public void It_counts_images_and_the_ones_with_no_alt_text(string input, int images, int missing)
    {
        var result = _sanitiser.Sanitise(input);

        Assert.Equal(images, result.ImageCount);
        Assert.Equal(missing, result.ImagesMissingAltText);
    }

    /// <summary>
    /// An image the allow-list removed is not an accessibility problem, so it is not counted. Reading the
    /// counts off the input instead would refuse to publish a page because of an image that is not on it.
    /// </summary>
    [Fact]
    public void A_removed_image_is_not_counted_as_missing_alt_text()
    {
        var result = _sanitiser.Sanitise("<img src=\"javascript:alert(1)\">");

        Assert.Equal(0, result.ImagesMissingAltText);
    }

    /// <summary>
    /// Sanitising twice changes nothing. Worth pinning: the body is re-sanitised on every save, so a
    /// sanitiser that escaped its own output would corrupt a page a little more each time it was edited.
    /// </summary>
    [Theory]
    [InlineData("<p>Hello &amp; goodbye</p>")]
    [InlineData("<a href=\"https://example.com/?a=1&amp;b=2\">x</a>")]
    [InlineData("<p>5 &lt; 6</p>")]
    public void Sanitising_an_already_clean_body_is_idempotent(string input)
    {
        var once = _sanitiser.Sanitise(input).Html;
        var twice = _sanitiser.Sanitise(once).Html;

        Assert.Equal(once, twice);
    }

    /// <summary>
    /// The allow-list is the security policy, so the policy itself is asserted rather than only its
    /// effects: a future edit that adds <c>iframe</c>, <c>style</c>, <c>script</c> or <c>target</c>
    /// fails here with the name of what was added, rather than silently widening the surface.
    /// </summary>
    [Theory]
    [InlineData("script")]
    [InlineData("style")]
    [InlineData("iframe")]
    [InlineData("object")]
    [InlineData("embed")]
    [InlineData("form")]
    [InlineData("input")]
    [InlineData("svg")]
    [InlineData("math")]
    [InlineData("base")]
    [InlineData("meta")]
    [InlineData("link")]
    [InlineData("h1")]
    [InlineData("div")]
    [InlineData("span")]
    public void The_tag_allow_list_does_not_hold(string tag) =>
        Assert.DoesNotContain(tag, HtmlSanitiser.AllowedTags);

    [Theory]
    [InlineData("style")]
    [InlineData("class")]
    [InlineData("id")]
    [InlineData("target")]
    [InlineData("onclick")]
    [InlineData("onerror")]
    [InlineData("srcset")]
    [InlineData("formaction")]
    public void The_attribute_allow_list_does_not_hold(string attribute) =>
        Assert.DoesNotContain(attribute, HtmlSanitiser.AllowedAttributes);

    [Theory]
    [InlineData("javascript")]
    [InlineData("data")]
    [InlineData("vbscript")]
    [InlineData("file")]
    public void The_scheme_allow_list_does_not_hold(string scheme) =>
        Assert.DoesNotContain(scheme, HtmlSanitiser.AllowedSchemes);

    /// <summary>The elements a policy page is actually written out of.</summary>
    private static readonly string[] NeededTags =
        ["p", "a", "img", "ul", "li", "h2", "table", "td", "strong"];

    [Fact]
    public void The_allow_lists_hold_what_a_body_needs() =>
        Assert.All(NeededTags, tag => Assert.Contains(tag, HtmlSanitiser.AllowedTags));
}
