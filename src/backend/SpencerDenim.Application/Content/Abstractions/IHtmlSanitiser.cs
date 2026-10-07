namespace SpencerDenim.Application.Content.Abstractions;

/// <summary>
/// Doc 08 §3.7/§3.8: "Rich text is cleaned with an allow-list on save; scripts and unsafe HTML are
/// removed." This is that port. The implementation is in Infrastructure, because it is a third-party
/// HTML parser and the Application layer takes no package for it.
/// <para>
/// <b>On save, not on render, and the reason is not performance.</b> A content page's body is read on
/// every request to a public page and written once a month, so sanitising on save is cheaper — but the
/// decisive reason is that the stored value is then the <i>only</i> value, so there is no path by which
/// an unsanitised body can be read back by a second consumer that forgot to clean it. A field that is
/// clean only at one of its readers is clean nowhere.
/// </para>
/// <para>
/// <b>And the storefront still sanitises on render, which is deliberate.</b> It binds the body with
/// Angular's <c>[innerHTML]</c> and never with <c>bypassSecurityTrustHtml</c>, so Angular's own DOM
/// sanitiser runs over it a second time in the browser and during the server render. That costs nothing
/// and it is the layer that still holds if a row is written by a support script, a data fix or a restore
/// from a spreadsheet — none of which comes through this port.
/// </para>
/// </summary>
public interface IHtmlSanitiser
{
    /// <summary>
    /// Cleans <paramref name="html"/> against the allow-list. Never throws on malformed input: an HTML
    /// parser's whole job is to have an opinion about nonsense, and a save that threw would be a 500 on a
    /// field a person typed into.
    /// <para>
    /// <b>It can, however, refuse to look at input at all</b>, and then
    /// <see cref="SanitisedHtml.Rejection"/> says why. That is not the same as "this cleaned to nothing":
    /// it means the markup was too long or too deeply nested to hand to a parser safely, so no claim is
    /// made about its contents. Callers must check it before treating <see cref="SanitisedHtml.Html"/> or
    /// the image counts as an answer.
    /// </para>
    /// </summary>
    SanitisedHtml Sanitise(string? html);
}

/// <summary>What the sanitiser made of a body, and what it had to take out of it.</summary>
/// <param name="Html">
/// The cleaned markup. Empty when the input was null, blank, or consisted only of things the allow-list
/// does not permit — "the client pasted a <c>&lt;script&gt;</c> and nothing else" ends as an empty body
/// rather than as a save that looks like it worked.
/// </param>
/// <param name="Removed">
/// What was removed, as short descriptions a staff member can read — <c>&lt;script&gt;</c>,
/// <c>onerror</c>, <c>href="javascript:…"</c>. Shown back to the editor so a save that quietly dropped
/// half a paste is not silent, which is the failure this project has shipped three times in other
/// shapes. Empty when the input was already clean. Never holds the removed content itself.
/// </param>
/// <param name="ImageCount">How many <c>&lt;img&gt;</c> elements survived.</param>
/// <param name="ImagesMissingAltText">
/// How many of those have no non-blank <c>alt</c>. Doc 08 §3.3 and GLB-12: "public images require alt
/// text before the content that uses them is published", so publishing is refused while this is above
/// zero. A draft may hold them; a published page may not.
/// </param>
/// <param name="Rejection">
/// Why the input was <b>not cleaned at all</b>, or <see langword="null"/> when it was. A sanitiser that
/// only ever returns markup has no way to say "I will not parse this", and it needs one: the input is
/// attacker-chosen markup handed to an HTML parser, and a parser's recursive parts are bounded by the
/// shape of the tree rather than by its size. A refusal here is a <i>validation</i> answer — the caller
/// turns it into a message on the <c>bodyHtml</c> field — and when it is set, <see cref="Html"/> is empty
/// and the counts are zero, because nothing was examined and reporting zero images would be a claim this
/// made no attempt to check.
/// </param>
public sealed record SanitisedHtml(
    string Html,
    IReadOnlyList<string> Removed,
    int ImageCount,
    int ImagesMissingAltText,
    string? Rejection = null)
{
    /// <summary>
    /// <see langword="true"/> when the input was refused before cleaning, so <see cref="Html"/> and the
    /// counts say nothing about it. See <see cref="Rejection"/>.
    /// </summary>
    public bool WasRejected => Rejection is not null;

    /// <summary>The answer for an empty body, which is what all six system pages are seeded with.</summary>
    public static SanitisedHtml Empty { get; } = new(string.Empty, [], 0, 0);
}
