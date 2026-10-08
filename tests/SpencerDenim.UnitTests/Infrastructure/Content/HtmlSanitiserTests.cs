using SpencerDenim.Infrastructure.Content;
using SpencerDenim.UnitTests.Support;

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

        // **Vectors aimed at KeepChildNodes specifically**, which is the one permissive thing in this
        // configuration: a disallowed element is unwrapped rather than deleted, so an attribute value
        // that closes its own element early could in principle have its contents promoted into the
        // document. Each of these relies on the parser re-reading a quoted string as markup.
        { "<noscript><p title=\"</noscript><img src=x onerror=alert(1)>\">", "onerror" },
        { "<noembed><img src=x onerror=alert(1)></noembed>", "onerror" },
        { "<noframes><img src=x onerror=alert(1)></noframes>", "onerror" },
        { "<xmp><img src=x onerror=alert(1)></xmp>", "onerror" },
        { "<textarea><img src=x onerror=alert(1)></textarea>", "onerror" },
        { "<title><img src=x onerror=alert(1)></title>", "onerror" },
        { "<svg><style><img src=x onerror=alert(1)></style></svg>", "onerror" },
        { "<form><div></div><input name=x onfocus=alert(1) autofocus></form>", "onfocus" },
        { "<select><option><img src=x onerror=alert(1)></option></select>", "onerror" },

        // A `style` attribute on an element that *is* allowed, with a URL in it: the CSS stage is never
        // reached, because the attribute is not on the allow-list at all.
        { "<p style=\"background-image:url('javascript:alert(1)')\">x</p>", "javascript" },
        { "<a href=\"#\" style=\"behavior:url(evil.htc)\">x</a>", "behavior" },
    };

    /// <summary>
    /// <b>What the allow-list deliberately accepts, and the cost of accepting it.</b>
    /// <para>
    /// These two were written as payloads and are not ones. They are kept as a <i>test of the
    /// acceptance</i> rather than deleted, because the acceptance is a decision somebody should be able
    /// to find: a content page may link to, and load an image from, any <c>http(s)</c> host. That is
    /// inherent in GLB-10 — a client pastes a link and a picture — and it is what the scheme allow-list
    /// exists to permit.
    /// </para>
    /// <para>
    /// <b>The residue is real and is not closed here.</b> Whoever can edit a content page can make every
    /// visitor's browser issue a request to a third party, which leaks the visitor's address and
    /// user-agent to it. Closing that means an image-host allow-list — which the map embed has, because
    /// it is framed rather than linked — and that is a decision about the shop rather than about the
    /// sanitiser: the media library is the supported way to put a picture on a page, and an allow-list
    /// would also refuse the CDN a client may legitimately use. Recorded in the W2-11 journal for the
    /// owner.
    /// </para>
    /// </summary>
    [Theory]
    // A protocol-relative URL is an https URL on an https page, so it is accepted like any other.
    [InlineData("<img src=\"//cdn.example/x.png\" alt=\"A shirt\">", "cdn.example")]
    [InlineData("<a href=\"https://cdn.example/brochure.pdf\">Our brochure</a>", "cdn.example")]
    // A doubled scheme is not an attack, it is a typo: the host becomes "https", which resolves to
    // nothing. Kept so nobody adds a rule against a string that was never dangerous.
    [InlineData("<a href=\"https://https://evil.example\">x</a>", "href=")]
    public void It_accepts_a_link_or_an_image_on_any_http_host(string input, string mustContain)
    {
        var result = _sanitiser.Sanitise(input);

        Assert.Contains(mustContain, result.Html, StringComparison.OrdinalIgnoreCase);
    }

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

    /// <summary>
    /// <b>A CDN's <c>&lt;picture&gt;</c> keeps its image.</b>
    /// <para>
    /// This is the likeliest real paste in the whole feature — every modern editor and every image CDN
    /// emits <c>&lt;picture&gt;&lt;source …&gt;&lt;img …&gt;&lt;/picture&gt;</c> — and
    /// <c>&lt;picture&gt;</c> was on the list of elements whose contents are deleted rather than
    /// unwrapped, so the image inside it disappeared and the editor was told only that a
    /// <c>&lt;picture&gt;</c> had been removed. Silent loss of the thing they were trying to add.
    /// Raised by the W2-11 tester.
    /// </para>
    /// </summary>
    [Fact]
    public void A_picture_element_keeps_the_image_inside_it()
    {
        var result = _sanitiser.Sanitise(
            """
            <figure>
              <picture>
                <source srcset="/media/hero.avif" type="image/avif">
                <source srcset="/media/hero.webp" type="image/webp">
                <img src="/media/hero.jpg" alt="Our studio in Colombo" width="1200" height="800">
              </picture>
              <figcaption>Our studio</figcaption>
            </figure>
            """);

        Assert.Contains("/media/hero.jpg", result.Html);
        Assert.Contains("Our studio in Colombo", result.Html);
        Assert.Equal(1, result.ImageCount);
        Assert.Equal(0, result.ImagesMissingAltText);

        // The wrapper and the alternatives still go, with the attribute that was smuggling a second URL.
        Assert.DoesNotContain("<picture", result.Html);
        Assert.DoesNotContain("<source", result.Html);
        Assert.DoesNotContain("srcset", result.Html);
    }

    /// <summary>
    /// The removal report says when it has stopped listing, rather than ending at twenty and reading as
    /// the whole answer — an editor who fixed the twenty things on it would otherwise believe they were
    /// done.
    /// </summary>
    [Fact]
    public void A_removal_report_longer_than_the_cap_says_how_much_it_is_not_showing()
    {
        // Thirty distinct disallowed attributes on one element, so the report has thirty kinds of thing
        // to name and room for twenty.
        var attributes = string.Join(
            ' ',
            Enumerable.Range(0, 30).Select(index => $"onx{index}=\"alert({index})\""));

        var result = _sanitiser.Sanitise($"<p {attributes}>Copy.</p>");

        Assert.Equal(21, result.Removed.Count);
        Assert.Contains("and 10 more kinds of thing not allowed in a page", result.Removed);
    }

    // ---------------------------------------------------------------------------------------------
    // Nesting depth. These are availability tests rather than allow-list tests, and they are in this
    // class because the thing being bounded is this class's parse.
    //
    // THE FAILURE THEY GUARD. A body of "<b>" repeated 8,500 times is 25,501 characters, inside the
    // 200,000 a page may hold and inside any length check that could be written. Walking the resulting
    // tree recursed once per level and overflowed the stack, and a stack overflow in .NET cannot be
    // caught: the runtime terminates the process, taking every other request in flight with it. Measured
    // before the fix: 8,000 levels cleaned in 556 ms, 8,500 killed the test host outright. Reported by
    // the W2-11 tester as Major 1.
    //
    // WHY THESE TESTS CAN FAIL, AND WHAT FAILURE LOOKS LIKE. Raising MaxNestingDepth past about 8,000
    // does not turn these red — it crashes the whole test run, which is the honest signal and the reason
    // the numbers below straddle the old threshold rather than sitting under it. Removing the guard
    // while keeping the iterative walk turns the two refusal tests red and leaves the run alive;
    // restoring TextContent in place of the walk and keeping the guard leaves everything green, which is
    // why both halves of the fix are tested separately below.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The tester's reproduction shape, which is also the cheapest: opening tags only. Three characters a
    /// level, so 8,500 levels is 25,501 characters — the whole point of the finding.
    /// </summary>
    private static string Nested(string tag, int levels) =>
        string.Concat(Enumerable.Repeat($"<{tag}>", levels)) + "x";

    [Theory]
    [InlineData(8_500)]
    [InlineData(60_000)]
    public void Markup_nested_past_the_limit_is_refused_rather_than_parsed(int levels)
    {
        var result = _sanitiser.Sanitise(Nested("b", levels));

        Assert.True(result.WasRejected);
        Assert.Contains("levels deep", result.Rejection);

        // Nothing is claimed about input that was never read.
        Assert.Equal(string.Empty, result.Html);
        Assert.Equal(0, result.ImageCount);
        Assert.Equal(0, result.ImagesMissingAltText);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void Well_formed_markup_nested_past_the_limit_is_refused_as_well()
    {
        // Every tag properly closed, so the refusal is not an artefact of counting unclosed ones: a tree
        // this deep is the problem whether or not the markup is tidy.
        var payload = string.Concat(Enumerable.Repeat("<b>", 8_500))
            + "x"
            + string.Concat(Enumerable.Repeat("</b>", 8_500));

        var result = _sanitiser.Sanitise(payload);

        Assert.True(result.WasRejected);

        // The point of the fix is that this line runs at all. Before it, the process was gone by here —
        // the tester proved a try/catch around the call does not get control.
        Assert.Contains("levels deep", result.Rejection);
    }

    /// <summary>
    /// <b>A trailing slash does not make an HTML element self-closing, and reading it as one was a hole
    /// in the pre-scan.</b>
    /// <para>
    /// <c>&lt;b/&gt;</c> opens a <c>b</c> in every browser — the slash is ignored for any element that
    /// is not void — so counting it as self-closing meant four characters a level with nothing counted
    /// at all. Found by re-reading the scan rather than by a failing test, which is why this one exists.
    /// </para>
    /// </summary>
    [Fact]
    public void A_trailing_slash_does_not_hide_the_nesting()
    {
        var payload = string.Concat(Enumerable.Repeat("<b/>", 9_000));

        Assert.True(_sanitiser.Sanitise(payload).WasRejected);
    }

    /// <summary>
    /// The same thing written as an attribute whose value is a slash, which is how a scan that looked
    /// only at the character before the <c>&gt;</c> could be fooled deliberately.
    /// </summary>
    [Fact]
    public void An_attribute_ending_in_a_slash_does_not_hide_the_nesting()
    {
        var payload = string.Concat(Enumerable.Repeat("<b x=/>", 9_000));

        Assert.True(_sanitiser.Sanitise(payload).WasRejected);
    }

    /// <summary>
    /// <b>An HTML element inside <c>&lt;svg&gt;</c> ends the foreign-content exception, because it ends
    /// the parser's.</b>
    /// <para>
    /// The exception honouring a trailing slash inside <c>&lt;svg&gt;</c> opened the same hole it was
    /// added to close. <c>&lt;svg&gt;&lt;div/&gt;</c> is not foreign content: the <c>div</c> breaks the
    /// parser out to HTML, the slash stops meaning anything, and the elements nest for real — while the
    /// scan still had <c>svg</c> on its stack and went on skipping. Found by the W2-11 tester, who
    /// measured it against the real <c>Sanitise</c>: <b>396 KB of <c>&lt;svg&gt;&lt;div/&gt;…</c> was
    /// accepted after 41 seconds</b> of one core, read as one level deep and built 66,000 deep, where
    /// the same payload without the five-character <c>&lt;svg&gt;</c> prefix is refused in 3 ms.
    /// </para>
    /// <para>
    /// <b>Each case asserts the cost as well as the verdict</b>, and that is the point: before the fix
    /// the first of these was refused too — forty-four seconds later. A verdict-only test would have
    /// passed against the defect.
    /// </para>
    /// <para>
    /// <b>The cost is asserted against the control rather than against a clock (#134).</b> It used to be
    /// <c>clock.Elapsed &lt; 5s</c>, which was the wrong instrument twice over: it fails when the runner
    /// is slow, and it would pass the 41-second defect outright on a machine nine times faster. What the
    /// defect actually was is a <i>ratio</i> — a five-character prefix turning a 3 ms refusal into 41
    /// seconds, about 13,000x — so the assertion is now the same payload with the prefix against the
    /// same payload without it, both measured in this process. That is machine-independent: a slow
    /// runner makes both halves slow together.
    /// </para>
    /// <para>
    /// <b>Measured</b> on the developer machine, Release, three repeats of all five rows: the ratio is
    /// <b>0.98–1.01x</b>. The prefix is free, because the scan pops out of foreign content and does the
    /// identical work. The bound of 10 is therefore about 10x clear of every reading taken and still some
    /// 1,300x inside the defect it exists to catch. <c>TimingShapeTests</c> proves this bound trips, at
    /// this setting, on work that is quadratic in the same input.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("svg", "div", 66_000)]
    [InlineData("svg", "b", 90_000)]
    [InlineData("svg", "span", 50_000)]
    [InlineData("math", "div", 40_000)]
    [InlineData("math", "b", 40_000)]
    public void An_html_element_inside_foreign_content_ends_the_self_closing_exception(
        string foreign,
        string breakout,
        int levels)
    {
        var run = string.Concat(Enumerable.Repeat($"<{breakout}/>", levels));
        var prefixed = $"<{foreign}>" + run;

        Assert.True(prefixed.Length <= HtmlSanitiser.MaxInputLength, "the payload must reach the scan");

        var result = _sanitiser.Sanitise(prefixed);

        Assert.True(result.WasRejected);
        Assert.Contains("levels deep", result.Rejection);

        // The control has to be refused too, or the comparison below is between a refusal and an accept
        // and the ratio means nothing. This is the three-millisecond half of the original measurement.
        Assert.True(
            _sanitiser.Sanitise(run).WasRejected,
            $"<{breakout}/> x {levels:N0} with no prefix must be refused, or it is not a control");

        TimingShape.AssertWithin(
            10,
            () => _sanitiser.Sanitise(run),
            () => _sanitiser.Sanitise(prefixed),
            $"<{foreign}> must not change what <{breakout}/> x {levels:N0} costs. It is refused either "
            + "way, but the refusal has to come from the lexical scan rather than from the parse: this "
            + "payload cost 41 seconds of CPU against the control's 3 ms before "
            + "BreaksOutOfForeignContent existed, and the admin content list re-sanitises every stored "
            + "body on every load.");
    }

    /// <summary>
    /// <c>&lt;math&gt;&lt;p/&gt;</c> × 40,000 is accepted, and that is <b>correct</b> rather than a gap.
    /// <para>
    /// <c>p</c> breaks out of foreign content and then closes the previous <c>p</c>, so forty thousand of
    /// them are forty thousand siblings — one level deep, which is exactly what a browser builds. Written
    /// down because it was in the first version of the theory above as an expected refusal, and the test
    /// failing is what made me check: the refusal would have been wrong, and asserting it would have
    /// pinned a false positive against every real page that pastes unclosed paragraphs.
    /// </para>
    /// <para>
    /// The cost still matters here, because "flat" has to mean cheap. It used to be asserted as
    /// <c>clock.Elapsed &lt; 5s</c>, and <b>this is the assertion #134 was filed about</b>: it began
    /// failing the backend job consistently at 6.7 s and 5.5 s on a GitHub hosted runner against 1–2 s
    /// here, on branches touching no sanitiser file.
    /// </para>
    /// <para>
    /// <b>The old text said these forty thousand siblings are "linear work". They are not — I measured
    /// it, and that is the more interesting half of #134.</b> Doubling the run multiplies the cost by
    /// <b>3.29x</b> (10k→20k), <b>3.56x</b> (20k→40k) and <b>3.80x</b> (40k→80k), converging on 4,
    /// which is quadratic. Subtracting the ~65 ms fixed cost per call puts the variable part at
    /// 4.0–4.6x. Absolute readings, Release, minimum of nine samples: 71 ms, 234 ms, 835 ms, 3,171 ms.
    /// So the CI failure was never purely a slow machine: at 40,000 the quadratic cost is already most of
    /// the way to a five-second budget, and a slower runner only had to finish the job.
    /// </para>
    /// <para>
    /// That cost is in the <i>accept</i> path — parse, clean and serialise a wide flat tree — not in
    /// the pre-scan, and it is the same shape as a residual <see cref="HtmlSanitiser"/> already records
    /// and the owner already accepted (8.8 seconds at the 400 KB cap, W2-11 journal, "Three residuals").
    /// <b>Nothing here is a fix for it</b>; it is noted on #134 rather than quietly absorbed.
    /// </para>
    /// <para>
    /// <b>So this asserts the shape rather than the seconds, at a bound the measurement supports:</b>
    /// doubling 10,000 to 20,000 may cost no more than 6x. Linear work comes in at 2x and a bound of 2
    /// would be red on main, so claiming linearity here would be a convenient lie. What the bound still
    /// catches is a step up to cubic or worse — the shape a genuinely deep tree has, and the shape the
    /// 41-second defect had. <c>TimingShapeTests</c> proves the bound trips at this setting on work that
    /// grows as the fourth power, and does not trip on linear or quadratic work, so it is a bound that
    /// can fail rather than a round number.
    /// </para>
    /// <para>
    /// <b>Measured</b> at exactly this setting, Release: idle, six readings, <b>3.13–3.60x</b>; with all
    /// eight cores saturated by spin loops, sixteen readings, <b>2.73–4.23x</b> (fifteen of them at or
    /// below 3.88x). Saturation roughly doubled the absolute times and moved the ratio by a tenth, which
    /// is the whole claim being made for a ratio over a clock. <b>So the real margin is 1.4x at the worst
    /// reading taken, and that is thin — say so rather than discover it in CI.</b> Two things keep it
    /// honest instead of raising the number: the bound sits between the quadratic this path measures (4)
    /// and the cubic it must still refuse (8), so there is nowhere looser to go that still discriminates;
    /// and the ~65 ms fixed cost per call deflates a 10,000-to-20,000 reading, so a faster parser would
    /// push these readings up toward 4.0–4.6x rather than down. <b>If this ever does go red, the fix is
    /// the quadratic accept path, not a bigger bound</b> — raising it is what #134 is a complaint about.
    /// </para>
    /// </summary>
    [Fact]
    public void A_breakout_element_that_closes_its_own_kind_is_flat_and_cheap()
    {
        // The verdict is what this test was written for, and it is asserted at every size the cost
        // assertion below measures: if the scan ever started reading a run of <p/> as nested, the payload
        // would be refused and the cost would collapse to an instant refusal — which a ratio on its own
        // would read as an improvement.
        foreach (var levels in new[] { 10_000, 20_000, 40_000 })
        {
            Assert.False(
                _sanitiser.Sanitise(FlatParagraphs(levels)).WasRejected,
                $"<math> + <p/> x {levels:N0} is {levels:N0} siblings one level deep, not a deep tree");
        }

        TimingShape.AssertDoublingWithin(
            6,
            levels =>
            {
                var payload = FlatParagraphs(levels);
                return () => _sanitiser.Sanitise(payload);
            },
            10_000,
            "a flat run of siblings must not cost more steeply than the quadratic the accept path "
            + "already measures. Steeper than that is the signature of a tree being built as deep as the "
            + "run is long, which is what BreaksOutOfForeignContent exists to stop.");
    }

    private static string FlatParagraphs(int levels) =>
        "<math>" + string.Concat(Enumerable.Repeat("<p/>", levels));

    /// <summary>
    /// The control the tester used, and the one that matters most here: the same shape with no
    /// <c>&lt;svg&gt;</c> in front of it was always refused immediately. Keeping both means the test
    /// above is measuring the prefix rather than the machine.
    /// <para>
    /// <b>It is now measured there rather than here (#134).</b> This case used to carry its own
    /// <c>clock.Elapsed &lt; 5s</c>, and that assertion could not fail for any reason worth knowing: the
    /// refusal is purely lexical and costs <b>0.10 ms</b> on this machine, measured, so the bound had a
    /// margin of roughly <b>50,000x</b> and a runner slow enough to trip it would have failed the whole
    /// job elsewhere first. The comparison it was reaching for needs <i>both</i> payloads in one process,
    /// which only the theory above can do — so the theory takes this payload as its baseline, and this
    /// case keeps the verdict, which is what it can prove on its own.
    /// </para>
    /// </summary>
    [Fact]
    public void The_same_payload_without_the_foreign_prefix_is_refused_just_as_fast()
    {
        var payload = string.Concat(Enumerable.Repeat("<div/>", 66_000));

        var result = _sanitiser.Sanitise(payload);

        Assert.True(result.WasRejected);
        Assert.Contains("levels deep", result.Rejection);
    }

    /// <summary>
    /// And the false-positive side of the same rule: inside foreign content a trailing slash <i>is</i>
    /// honoured, so a pasted SVG of three hundred <c>&lt;path/&gt;</c> elements is two levels deep
    /// rather than three hundred. The SVG is removed either way; what matters is that the page it was
    /// pasted into is not refused.
    /// </summary>
    [Fact]
    public void A_pasted_svg_of_self_closing_elements_is_not_a_deep_tree()
    {
        var payload = "<p>Copy.</p><svg>"
            + string.Concat(Enumerable.Repeat("<path d=\"M0 0\"/>", 300))
            + "</svg><p>More copy.</p>";

        var result = _sanitiser.Sanitise(payload);

        Assert.False(result.WasRejected);
        Assert.Contains("More copy.", result.Html);
        Assert.DoesNotContain("<svg", result.Html);
    }

    /// <summary>
    /// And the breakout rule must not swallow the case above. <c>&lt;path&gt;</c>, <c>&lt;circle&gt;</c>
    /// and <c>&lt;g&gt;</c> are not on the standard's breakout list, so a real SVG stays foreign content
    /// and its slashes stay meaningful — which is the whole reason the exception exists, and the thing
    /// that would quietly break if somebody widened <c>BreaksOutOfForeignContent</c> to "anything that
    /// looks like HTML".
    /// </summary>
    [Theory]
    [InlineData("path")]
    [InlineData("circle")]
    [InlineData("rect")]
    [InlineData("g")]
    [InlineData("use")]
    public void A_real_svg_element_does_not_break_out(string element)
    {
        var payload = "<svg>" + string.Concat(Enumerable.Repeat($"<{element}/>", 1_000)) + "</svg>";

        Assert.False(_sanitiser.Sanitise(payload).WasRejected);
    }

    [Fact]
    public void Nesting_through_a_list_is_counted_too()
    {
        // The shape that defeats a naive "a tag of the same name closes the previous one" scan: each <li>
        // is inside a new <ul>, so none of them closes an earlier one and the real tree is 2n deep.
        var payload = string.Concat(Enumerable.Repeat("<ul><li>", 5_000));

        Assert.True(_sanitiser.Sanitise(payload).WasRejected);
    }

    /// <summary>
    /// The other half of the fix, tested on its own: the walk over the cleaned tree has no recursion in
    /// it, so a tree that gets past the lexical pre-scan still cannot overflow.
    /// <para>
    /// <c>&lt;p&gt;</c> is the probe because the pre-scan models the implied end tag — it reads a run of
    /// unclosed paragraphs as one level, which is what a browser builds — so these 9,000 tags reach the
    /// parser. They clean to 9,000 flat paragraphs, which the old <c>TextContent</c> walk survived and a
    /// recursive walk over a deep tree would not; what this pins is that the guard does not <i>rely</i> on
    /// the pre-scan being exact.
    /// </para>
    /// </summary>
    [Fact]
    public void A_run_of_unclosed_paragraphs_reaches_the_parser_and_is_cleaned()
    {
        var payload = string.Concat(Enumerable.Repeat("<p>Paragraph.", 9_000));

        var result = _sanitiser.Sanitise(payload);

        Assert.False(result.WasRejected);
        Assert.Contains("<p>Paragraph.</p>", result.Html);
    }

    /// <summary>
    /// The false-positive side, which matters as much as the refusal: a real policy page is nested five
    /// or six deep, and an export is full of wrappers and unclosed tags. None of it may be refused.
    /// </summary>
    [Theory]
    [InlineData("<div><div><div><div><p>Our story.</p></div></div></div></div>")]
    [InlineData("<blockquote><ul><li><table><tbody><tr><td><strong>14 days</strong></td></tr></tbody></table></li></ul></blockquote>")]
    [InlineData("<p>One<p>Two<p>Three<p>Four")]
    [InlineData("<ul><li>One<li>Two<li>Three</ul>")]
    [InlineData("<table><tr><td>A<td>B<tr><td>C<td>D</table>")]
    [InlineData("<dl><dt>Term<dd>Meaning<dt>Term<dd>Meaning</dl>")]
    public void Ordinary_markup_is_not_mistaken_for_a_deep_tree(string html) =>
        Assert.False(_sanitiser.Sanitise(html).WasRejected);

    [Fact]
    public void An_export_of_three_hundred_unclosed_paragraphs_is_accepted()
    {
        // The specific false positive a plain open-tag counter would produce, and the reason the implied
        // end tags are modelled: older editors emit exactly this.
        var result = _sanitiser.Sanitise(string.Concat(Enumerable.Repeat("<p>A clause of the policy.", 300)));

        Assert.False(result.WasRejected);
        Assert.Equal(300, result.Html.Split("<p>").Length - 1);
    }

    [Fact]
    public void A_deeply_nested_list_that_a_person_could_write_is_accepted()
    {
        var result = _sanitiser.Sanitise(string.Concat(Enumerable.Repeat("<ul><li>", 40)) + "Deep");

        Assert.False(result.WasRejected);
    }

    [Fact]
    public void Markup_longer_than_the_parser_limit_is_refused()
    {
        var result = _sanitiser.Sanitise(new string('a', HtmlSanitiser.MaxInputLength + 1));

        Assert.True(result.WasRejected);
        Assert.Contains("in sections", result.Rejection);
    }

    [Fact]
    public void Markup_at_the_parser_limit_is_still_read()
    {
        // The boundary, from the allowed side: a long paste that cleans down is the case the looser
        // input limit exists for, so it must not be refused here.
        var result = _sanitiser.Sanitise("<p>" + new string('a', HtmlSanitiser.MaxInputLength - 7) + "</p>");

        Assert.False(result.WasRejected);
    }

    [Fact]
    public void An_accepted_body_reports_no_rejection()
    {
        var result = _sanitiser.Sanitise("<p>Returns within 14 days.</p>");

        Assert.False(result.WasRejected);
        Assert.Null(result.Rejection);
    }
}
