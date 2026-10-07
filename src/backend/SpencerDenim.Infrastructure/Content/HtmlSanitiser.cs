using System.Globalization;
using AngleSharp.Dom;
using Ganss.Xss;
using SpencerDenim.Application.Content.Abstractions;

namespace SpencerDenim.Infrastructure.Content;

/// <summary>
/// The allow-list HTML sanitiser of doc 08 §3.7/§3.8 (GLB-10, ADM-17).
/// <para>
/// <b>Its one caller is the content-page body</b> — on save in
/// <c>ContentPageWriteHandlers.CheckBody</c>, and on read in <c>AdminContentReader</c>, which re-reads the
/// stored body only to count images missing alt text. Doc 08 §3.8 also describes a settings value of type
/// <c>Text/HTML</c>, and this is the sanitiser such a value would use, but <b>no settings path calls it
/// today</b>: every setting the catalogue defines is plain text, a URL or a number, each with its own
/// check in <c>SettingFieldRules</c>. Said plainly because the earlier wording here claimed a reach this
/// class does not have, and a security control that is believed to cover a field it never sees is worse
/// than one whose scope is written down.
/// </para>
/// <para>
/// <b>It is an allow-list, in both directions, and that is the only arrangement that is safe.</b> A
/// block-list has to enumerate every dangerous construct — and it is wrong the day a browser gains one.
/// This permits exactly <see cref="AllowedTags"/> and exactly <see cref="AllowedAttributes"/>; everything
/// else is removed whether or not anybody thought of it. The lists below are the whole security policy of
/// this feature, written out, because "the sanitiser handles it" is not a statement anyone can check.
/// </para>
/// <para>
/// <b>What the allow-list permits.</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Elements:</b> <c>p br strong b em i u s sub sup</c> · <c>h2 h3 h4</c> · <c>ul ol li</c> ·
/// <c>blockquote hr</c> · <c>a</c> · <c>img figure figcaption</c> · <c>table thead tbody tfoot tr th td
/// caption</c> · <c>dl dt dd</c>.
/// </description></item>
/// <item><description>
/// <b>Attributes:</b> <c>href title</c> (on a link) · <c>src alt width height</c> (on an image) ·
/// <c>colspan rowspan scope</c> (on a cell) · <c>dir lang</c>. Nothing else — so no <c>style</c>, no
/// <c>class</c>, no <c>id</c>, no <c>data-*</c>, no <c>target</c>, and no event handler, because an
/// attribute that is not on this list is removed without needing to be recognised as dangerous.
/// </description></item>
/// <item><description>
/// <b>URL schemes:</b> <c>http</c>, <c>https</c>, <c>mailto</c>, a site-relative path
/// (<c>/returns</c>), and a <b>protocol-relative</b> one (<c>//cdn.example/x.png</c>), which on an
/// https page is an https URL. Nothing else — so no <c>javascript:</c>, no <c>data:</c>, no
/// <c>vbscript:</c>, no <c>file:</c>.
/// <para>
/// <b>So a body may link to, and load an image from, any host.</b> That is inherent in GLB-10 — a
/// client pastes a link and a picture — and it is stated here rather than left to be discovered,
/// because the residue is real: whoever can edit a page can make every visitor's browser issue a
/// request to a third party, which leaks the visitor's address and user-agent to it. Closing that means
/// an image-host allow-list, which the map embed has (<c>SettingFieldRules.MapEmbedHosts</c>) because
/// it is <i>framed</i> rather than linked, and which is a decision about the shop rather than about
/// this class. <c>HtmlSanitiserTests.It_accepts_a_link_or_an_image_on_any_http_host</c> pins the
/// acceptance so it cannot be narrowed by accident, and the W2-11 journal records it for the owner.
/// </para>
/// </description></item>
/// <item><description>
/// <b>CSS:</b> none at all. <c>AllowedCssProperties</c> is emptied and <c>&lt;style&gt;</c> is not an
/// allowed element, so a body cannot carry a stylesheet, an <c>@import</c>, or a property whose value is
/// a URL.
/// </description></item>
/// <item><description>
/// <b>Comments</b> are removed, so markup hidden inside one cannot be revived by a later edit that
/// changes how the surrounding text is parsed.
/// </description></item>
/// </list>
/// <para>
/// <b>Three decisions inside that list that are easy to get wrong.</b>
/// </para>
/// <list type="number">
/// <item><description>
/// <b><c>h1</c> is not allowed.</b> The page's single <c>h1</c> is its title, rendered by the storefront
/// outside the body; a second one is an accessibility defect (GLB-12) rather than a security one, and the
/// cheapest place to prevent it is here. A pasted <c>&lt;h1&gt;</c> is kept as text inside its parent
/// rather than thrown away, so no copy is lost.
/// </description></item>
/// <item><description>
/// <b><c>target</c> is not allowed</b>, so every link opens in the same tab. It is not on the list
/// because <c>target="_blank"</c> without <c>rel="noopener"</c> is reverse tabnabbing, and the fix —
/// allowing <c>rel</c> as well and hoping an editor types it — is worse than not offering the attribute.
/// </description></item>
/// <item><description>
/// <b><c>iframe</c> is not allowed</b>, including for a map. GLB-10 asks for a map on the Contact page
/// and it is <i>not</i> page body: it is the setting <c>Contact.MapEmbedUrl</c>, whose host is checked
/// against a short allow-list of its own (<c>SettingFieldRules.TryNormaliseMapEmbedUrl</c>). An editor
/// who could paste an arbitrary <c>&lt;iframe&gt;</c> into a body could frame anything on the shop's own
/// origin.
/// </description></item>
/// </list>
/// <para>
/// <b>Why a library and not a hand-written pass.</b> The attacks this has to survive are mostly parser
/// confusion — <c>&lt;scr&lt;script&gt;ipt&gt;</c>, an unclosed attribute, <c>&lt;svg/onload=…&gt;</c>, a
/// comment that ends early in one parsing mode and not another — so the only correct shape is "parse the
/// document the way a browser would, then walk the resulting tree against the allow-list".
/// <c>HtmlSanitizer</c> is AngleSharp's HTML5 parser plus that walk, is MIT, and is the library this
/// problem has. A regular-expression pass over the same input is the documented wrong answer.
/// </para>
/// <para>
/// <b>It is registered as a singleton and that is safe only because of how it is used here.</b> Ganss's
/// <c>HtmlSanitizer</c> is not documented as thread-safe for <i>configuration</i>, and its removal
/// notifications are instance-level events. So this type never shares one: it holds the configuration and
/// builds a fresh <c>HtmlSanitizer</c> per call, with the per-call event handlers that collect
/// <see cref="SanitisedHtml.Removed"/>. A body is sanitised on a save, a handful of times per day, so the
/// allocation is irrelevant and the alternative is a race on a security control.
/// </para>
/// </summary>
public sealed class HtmlSanitiser : IHtmlSanitiser
{
    /// <summary>Every element a body may contain. See the type remarks for why each group is here.</summary>
    public static IReadOnlySet<string> AllowedTags { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Text and inline emphasis.
        "p", "br", "strong", "b", "em", "i", "u", "s", "sub", "sup",

        // Headings below the page's own h1.
        "h2", "h3", "h4",

        // Lists.
        "ul", "ol", "li", "dl", "dt", "dd",

        // Blocks.
        "blockquote", "hr",

        // Links.
        "a",

        // Images, with the caption markup that makes one accessible.
        "img", "figure", "figcaption",

        // Tables — a shipping page is mostly a table of zones and fees.
        "table", "caption", "thead", "tbody", "tfoot", "tr", "th", "td",
    };

    /// <summary>
    /// Every attribute a body may carry. Ganss's allow-list is global rather than per element, so
    /// <c>href</c> is permitted anywhere it is meaningful and ignored everywhere it is not — which is a
    /// property of HTML, not a hole: <c>&lt;p href="…"&gt;</c> does nothing in any browser.
    /// </summary>
    public static IReadOnlySet<string> AllowedAttributes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "href", "title", "src", "alt", "width", "height", "colspan", "rowspan", "scope", "dir", "lang",
    };

    /// <summary>
    /// The only URL schemes a <c>href</c> or <c>src</c> may use. A scheme-relative or site-relative path
    /// is also accepted — that is <c>Ganss</c>'s <c>AllowDataAttributes</c>-independent handling of a
    /// relative URI, and a relative path cannot name a scheme at all, which is the property that matters.
    /// </summary>
    public static IReadOnlySet<string> AllowedSchemes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "mailto",
    };

    /// <summary>
    /// Disallowed elements whose <b>contents are not copy</b>, so they are removed with everything inside
    /// them rather than unwrapped.
    /// <para>
    /// <b>This is not the security control and must not be read as one.</b> The allow-list above is: every
    /// one of these elements is already absent from <see cref="AllowedTags"/> and so is removed whatever
    /// this list says. What this list changes is only <i>what happens to the characters inside</i>.
    /// </para>
    /// <para>
    /// <b>It exists because <c>KeepChildNodes</c> is a single global switch and the right answer differs
    /// by element.</b> For <c>&lt;div&gt;</c>, <c>&lt;span&gt;</c> and <c>&lt;h1&gt;</c> the contents are
    /// the client's sentences, and deleting them is the silent-data-loss defect this project has shipped
    /// three times. For <c>&lt;script&gt;</c> the "contents" are source code: unwrapping it put
    /// <c>alert(1)</c> on a public page as visible text — safe, and still a visible defect on a page a
    /// client published. For <c>&lt;math&gt;</c> and <c>&lt;svg&gt;</c> it is worse than cosmetic: those
    /// switch the parser into foreign-content mode, which is the engine of every mXSS payload, and
    /// unwrapping their children hands the serialiser a tree that was never HTML — measured, on
    /// <c>&lt;math&gt;&lt;mtext&gt;&lt;table&gt;&lt;mglyph&gt;&lt;style&gt;&lt;img src=x
    /// onerror=alert(1)&gt;</c>, which survived with its handler intact until this list existed. So the
    /// handler on <c>RemovingTag</c> empties these elements before the sanitiser decides what to keep,
    /// which leaves nothing for <c>KeepChildNodes</c> to preserve.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> ContentsAreNotCopy = new(StringComparer.OrdinalIgnoreCase)
    {
        // Raw-text and escapable-raw-text elements: what is inside them is code, not text.
        "script", "style", "template", "noscript", "xmp", "noframes", "noembed", "textarea", "title",

        // Foreign content, which changes how the parser reads everything inside it.
        "svg", "math",

        // Embedded content and framing.
        "iframe", "frame", "frameset", "object", "embed", "applet", "canvas", "picture", "source",
        "track", "audio", "video",

        // Form controls whose inner text is a value or a label for something that is being removed.
        "select", "option", "optgroup", "datalist",

        // Document structure that a paste can carry in.
        "head", "html",
    };

    public SanitisedHtml Sanitise(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return SanitisedHtml.Empty;
        }

        // Before the parser, not after it. See MaxNestingDepth for what this is defending and why a check
        // on the length could never have been it.
        if (Refuse(html) is { } refusal)
        {
            return new SanitisedHtml(string.Empty, [], 0, 0, refusal);
        }

        var removed = new List<string>();
        var sanitiser = Build(removed);

        var cleaned = sanitiser.Sanitize(html).Trim();

        if (Inspect(cleaned) is not { } inspection)
        {
            return new SanitisedHtml(string.Empty, [], 0, 0, TooDeeplyNested());
        }

        // A body of only whitespace or only empty tags reads as "nothing", and an empty string is what the
        // rest of the system already means by that (an unpublished shell). Without this, a paste that was
        // entirely script left a body of "\n\n" that is not empty to any `string.IsNullOrEmpty` check.
        return new SanitisedHtml(
            inspection.RendersNothing ? string.Empty : cleaned,
            Distinct(removed),
            inspection.Images,
            inspection.ImagesMissingAlt);
    }

    /// <summary>
    /// A fresh sanitiser, configured from the lists above, with the handlers that report what it took out.
    /// </summary>
    private static HtmlSanitizer Build(List<string> removed)
    {
        var sanitiser = new HtmlSanitizer(new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(AllowedTags, StringComparer.OrdinalIgnoreCase),
            AllowedAttributes = new HashSet<string>(AllowedAttributes, StringComparer.OrdinalIgnoreCase),
            AllowedSchemes = new HashSet<string>(AllowedSchemes, StringComparer.OrdinalIgnoreCase),

            // No CSS at all, so there is no property whose value could be a URL. There is also no at-rule
            // to filter and no class to filter, because `style` is not an allowed element and `style` and
            // `class` are not allowed attributes: CSS cannot reach the sanitiser's CSS stage in the first
            // place. This empties the property set anyway, so the guarantee does not depend on remembering
            // that argument.
            AllowedCssProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            UriAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "href", "src" },
        })
        {
            // `false`, so `data-anything` is not a blanket exception to the attribute allow-list.
            AllowDataAttributes = false,

            // Keep the *text* of a disallowed element rather than deleting it with its contents. A client
            // who pastes <h1>Our story</h1><div>…</div> keeps both sentences; they simply lose the h1 and
            // the div. The alternative silently eats copy, which is the shape of defect this project has
            // shipped three times, and it buys nothing: the danger is in the element and its attributes,
            // not in the characters a person typed.
            KeepChildNodes = true,
        };

        sanitiser.RemovingTag += (_, e) =>
        {
            var name = e.Tag.NodeName.ToLowerInvariant();
            removed.Add($"<{name}>");

            if (!ContentsAreNotCopy.Contains(name))
            {
                return;
            }

            // Emptied *here*, inside the event the library raises before it acts: its removal step keeps
            // the children only `if (KeepChildNodes && tag.HasChildNodes)`, so an element with none left
            // is removed outright. See ContentsAreNotCopy for why the answer differs by element.
            while (e.Tag.FirstChild is { } child)
            {
                e.Tag.RemoveChild(child);
            }
        };

        sanitiser.RemovingAttribute += (_, e) => removed.Add(e.Attribute.Name.ToLowerInvariant());
        sanitiser.RemovingStyle += (_, e) => removed.Add($"style: {e.Style.Name.ToLowerInvariant()}");
        sanitiser.RemovingComment += (_, _) => removed.Add("an HTML comment");
        sanitiser.RemovingCssClass += (_, e) => removed.Add($"class: {e.CssClass}");

        return sanitiser;
    }

    /// <summary>
    /// The longest chain of nested elements a body may hold, and the <b>only</b> bound that stops a
    /// content-page save from terminating the API process.
    /// <para>
    /// <b>The failure this exists for.</b> Walking a DOM is naturally recursive, and this class walked one
    /// through three convenient calls that each recurse once per level — <c>TextContent</c> and two
    /// <c>QuerySelectorAll</c>s. A body of <c>"&lt;b&gt;"</c> repeated 8,500 times is <b>25,501
    /// characters</b>, well inside the 200,000 a page may hold, and it overflowed the stack. A stack
    /// overflow in .NET cannot be caught: the runtime terminates the process, so every other request in
    /// flight — every shopper mid-checkout — dies with it. Measured: 8,000 levels cleaned in 556 ms, 8,500
    /// killed the process, while the library's own <c>Sanitize</c> survived 60,000.
    /// </para>
    /// <para>
    /// <b>Why a length limit is not the fix</b>, and why the length check in
    /// <c>ContentPageWriteHandlers.CheckBody</c> could never have caught this: 25 KB is an ordinary page.
    /// The cost of walking a tree is set by its <i>shape</i>, not its size, and three characters buy a
    /// level. Any bound that works has to be a bound on depth.
    /// </para>
    /// <para>
    /// <b>Why it is a read-path fix too.</b> <c>AdminContentReader</c> re-sanitises the stored body on
    /// every content list and every detail read, to count images missing alt text. So a row written
    /// <i>outside</i> this API — the support script, data fix or restore that the port's own remarks call
    /// out — would have terminated the process on every attempt to open the screen a staff member would
    /// use to repair it, for everyone, the owner included. This guard covers that path because it lives
    /// inside the sanitiser rather than beside one of its callers.
    /// </para>
    /// <para>
    /// <b>Why 200.</b> A deep page is a table of shipping zones inside a list inside a blockquote: six or
    /// seven levels, and the allow-list permits nothing that nests arbitrarily except lists. Two hundred is
    /// an order of magnitude above the deepest plausible document and nearly two below the level that
    /// crashed, so it is chosen to be obviously safe at both ends rather than to be tight.
    /// </para>
    /// </summary>
    public const int MaxNestingDepth = 200;

    /// <summary>
    /// The longest input this will hand to a parser at all, in characters of markup <b>before</b>
    /// cleaning.
    /// <para>
    /// Twice <c>ContentPage.BodyHtmlMaxLength</c>, and deliberately looser than it: the stored value is the
    /// cleaned one, so a paste that arrives as 300 KB of editor markup and cleans to 8 KB of text is
    /// something that fits and has to be allowed. This is not that check — it is the point past which
    /// refusing to look is cheaper than parsing, so an unbounded request body cannot become unbounded
    /// parser work. The limit a staff member actually meets is still the one on the cleaned length.
    /// </para>
    /// </summary>
    public const int MaxInputLength = 400_000;

    /// <summary>
    /// Why this input will not be parsed, or <see langword="null"/> when it will be. Purely lexical: it
    /// reads characters and never builds a tree, because its whole job is to run <i>before</i> anything
    /// does.
    /// </summary>
    private static string? Refuse(string html)
    {
        if (html.Length > MaxInputLength)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"That is {html.Length:N0} characters of markup, which is more than this editor will read "
                + $"in one go ({MaxInputLength:N0}). Paste the page in sections.");
        }

        return MeasureNesting(html) > MaxNestingDepth ? TooDeeplyNested() : null;
    }

    private static string TooDeeplyNested() =>
        "That markup nests tags more than "
        + MaxNestingDepth.ToString(CultureInfo.InvariantCulture)
        + " levels deep, which no page needs and this editor will not open. It is nearly always a paste "
        + "whose tags were left open: paste it as plain text and format it here.";

    /// <summary>
    /// Elements that never hold anything, so a start tag for one does not deepen the tree whether or not
    /// it is closed.
    /// </summary>
    private static readonly HashSet<string> NeverHoldAnything = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "keygen", "link", "meta", "param",
        "source", "track", "wbr",
    };

    /// <summary>
    /// Elements that a start tag of the same name closes rather than nests inside — a second
    /// <c>&lt;p&gt;</c> ends the first, as every browser does. Modelled here so that an export full of
    /// unclosed paragraphs or list items is not mistaken for a deep tree and refused.
    /// </summary>
    private static readonly HashSet<string> ClosedByAnotherOfItsKind = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "li", "dt", "dd", "tr", "td", "th", "thead", "tbody", "tfoot", "option", "optgroup",
    };

    /// <summary>
    /// A conservative reading of how deeply <paramref name="html"/> nests elements.
    /// <para>
    /// <b>It does not have to be exact, and it is not.</b> It is a filter in front of the parser, and
    /// <see cref="Inspect"/> measures the real depth of the real tree afterwards and refuses there too —
    /// so an input this reads as shallower than the parser builds it is still refused, one step later and
    /// before anything recursive touches it. What this must not do is read a <i>legitimate</i> page as
    /// deep, which is why the implied end tags above are modelled: without them, a page of three hundred
    /// unclosed <c>&lt;p&gt;</c> tags — which older editors produce — would count as three hundred deep.
    /// </para>
    /// </summary>
    private static int MeasureNesting(string html)
    {
        var open = new List<string>(capacity: 32);
        var deepest = 0;
        var cursor = 0;

        while (cursor < html.Length)
        {
            var at = html.IndexOf('<', cursor);
            if (at < 0)
            {
                break;
            }

            cursor = at + 1;
            if (cursor >= html.Length)
            {
                break;
            }

            var isEndTag = html[cursor] == '/';
            if (isEndTag)
            {
                cursor++;
            }

            var nameStart = cursor;
            while (cursor < html.Length && char.IsAsciiLetterOrDigit(html[cursor]))
            {
                cursor++;
            }

            if (cursor == nameStart)
            {
                // A "<" that starts no tag: a comment, a doctype, or a stray character in text.
                continue;
            }

            var name = html[nameStart..cursor].ToLowerInvariant();

            var closeBracket = html.IndexOf('>', cursor);
            var selfClosing = closeBracket > nameStart && html[closeBracket - 1] == '/';
            cursor = closeBracket < 0 ? html.Length : closeBracket + 1;

            if (isEndTag)
            {
                var opened = open.LastIndexOf(name);
                if (opened >= 0)
                {
                    open.RemoveRange(opened, open.Count - opened);
                }

                continue;
            }

            if (selfClosing || NeverHoldAnything.Contains(name))
            {
                continue;
            }

            if (open.Count > 0
                && string.Equals(open[^1], name, StringComparison.Ordinal)
                && ClosedByAnotherOfItsKind.Contains(name))
            {
                open.RemoveAt(open.Count - 1);
            }

            open.Add(name);

            if (open.Count <= deepest)
            {
                continue;
            }

            deepest = open.Count;
            if (deepest > MaxNestingDepth)
            {
                // Nothing a longer scan could learn changes the answer.
                return deepest;
            }
        }

        return deepest;
    }

    /// <summary>What a reader of the cleaned body would find in it.</summary>
    /// <param name="RendersNothing">
    /// No text and nothing visual: whitespace, or structural tags with nothing inside them.
    /// </param>
    /// <param name="Images">
    /// How many <c>&lt;img&gt;</c> elements survived <b>with a URL still on them</b>.
    /// </param>
    /// <param name="ImagesMissingAlt">How many of those have no non-blank <c>alt</c>.</param>
    private readonly record struct Inspection(bool RendersNothing, int Images, int ImagesMissingAlt);

    /// <summary>
    /// Reads the counts off the <b>cleaned</b> markup, in one parse and one walk — or
    /// <see langword="null"/> when the cleaned tree is deeper than <see cref="MaxNestingDepth"/>.
    /// <para>
    /// Cleaned rather than input, deliberately, and on two counts. An <c>&lt;img&gt;</c> the allow-list
    /// removed is not on the page, so it is not an accessibility problem; and an <c>&lt;img&gt;</c> whose
    /// <c>src</c> was removed for naming a forbidden scheme is not a <i>public image</i> either — doc 08
    /// §3.3's rule is about images a visitor is served. Counting those would refuse to publish a page over
    /// an image that is not on it, and would tell the editor the wrong thing to fix: the removal report
    /// already says the <c>src</c> went.
    /// </para>
    /// <para>
    /// The parse is AngleSharp's, which is the parser the sanitiser itself used, so the two cannot
    /// disagree about what an element is.
    /// </para>
    /// <para>
    /// <b>The walk is iterative, with its own stack, and that is the point.</b> It used to be
    /// <c>TextContent</c> plus two <c>QuerySelectorAll</c>s, each recursing once per level of the tree,
    /// which is how 25 KB of markup terminated the process. An explicit stack costs one loop and cannot
    /// overflow; the depth limit then bounds the <i>work</i>, and catches a tree the lexical pre-scan read
    /// as shallower than the parser built it.
    /// </para>
    /// </summary>
    private static Inspection? Inspect(string cleanedHtml)
    {
        if (cleanedHtml.Length == 0)
        {
            return new Inspection(true, 0, 0);
        }

        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument("<body>" + cleanedHtml + "</body>");

        if (document.Body is not { } body)
        {
            return new Inspection(true, 0, 0);
        }

        var images = 0;
        var imagesMissingAlt = 0;
        var hasText = false;
        var hasSomethingVisual = false;

        var pending = new Stack<(INode Node, int Depth)>();
        foreach (var child in body.ChildNodes)
        {
            pending.Push((child, 1));
        }

        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();

            if (depth > MaxNestingDepth)
            {
                return null;
            }

            switch (node)
            {
                case IText text:
                    hasText = hasText || !string.IsNullOrWhiteSpace(text.Data);
                    break;

                case IElement element:
                    if (string.Equals(element.LocalName, "img", StringComparison.Ordinal))
                    {
                        if (!string.IsNullOrWhiteSpace(element.GetAttribute("src")))
                        {
                            images++;
                            if (string.IsNullOrWhiteSpace(element.GetAttribute("alt")))
                            {
                                imagesMissingAlt++;
                            }
                        }
                    }
                    else if (element.LocalName is "hr" or "br")
                    {
                        hasSomethingVisual = true;
                    }

                    foreach (var child in element.ChildNodes)
                    {
                        pending.Push((child, depth + 1));
                    }

                    break;
            }
        }

        return new Inspection(!hasText && images == 0 && !hasSomethingVisual, images, imagesMissingAlt);
    }

    /// <summary>
    /// The removal report, de-duplicated and capped. A paste of a whole web page can remove hundreds of
    /// attributes, and "we removed onclick (247 times)" helps nobody more than "we removed onclick" does.
    /// </summary>
    private static List<string> Distinct(List<string> removed) =>
        removed.Count == 0
            ? []
            : removed.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).Take(20).ToList();
}
