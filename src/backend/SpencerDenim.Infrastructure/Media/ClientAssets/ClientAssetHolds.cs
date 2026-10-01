using SpencerDenim.Domain.Content;

namespace SpencerDenim.Infrastructure.Media.ClientAssets;

/// <summary>A picture that does not fit the slot its filename claims, and the section it keeps off.</summary>
/// <param name="Section">The homepage block the import will not switch on while this picture fills it.</param>
/// <param name="Reason">
/// What is wrong with it, in words the operator and the client can both act on. It is printed in the
/// import report and nowhere else, so it has to say what would have to change.
/// </param>
public sealed record HeldBackArtwork(HomeSectionType Section, string Reason);

/// <summary>
/// The pictures the import will not switch a homepage section on for, by the SHA-256 of the delivered
/// bytes.
/// <para>
/// <b>Why this exists at all.</b> <c>EnableIfReady</c> can tell whether a section has artwork; it cannot
/// tell whether the artwork is of the right thing. Two of the files in the first delivery are not: the
/// hero video is portrait where the frame is 16:9, and the activewear banner is a flat-lay of linen and
/// denim. Switching either on would put a cropped face or a mislabelled tile on the front page.
/// </para>
/// <para>
/// <b>Why it is keyed on the bytes.</b> A judgement about a particular photograph has to expire by
/// itself the day a different photograph arrives. A hold keyed on the section type, or an operator flag
/// somebody has to remember to pass, outlives the problem and quietly keeps a finished block off the
/// front page — which is the same class of mistake in the other direction. A checksum releases itself:
/// a replacement has different bytes, so it is simply not in this list.
/// </para>
/// <para>
/// The files are still imported and still attached to their items, so the client can see them in ADM-17
/// and switch the section on from there if they disagree. What is withheld is only the automatic
/// switch-on.
/// </para>
/// </summary>
public sealed class ClientAssetHolds(IReadOnlyDictionary<string, HeldBackArtwork> byChecksum)
{
    /// <summary>The holds that apply to the delivery of 30 September 2026.</summary>
    public static ClientAssetHolds Default { get; } = new(
        new Dictionary<string, HeldBackArtwork>(StringComparer.OrdinalIgnoreCase)
        {
            ["159e9dd19ecfba45849fa8bdcc745eed6d87e6f911ed2efd18124f80f737c534"] = new(
                HomeSectionType.HeroVideo,
                "the delivered hero.mp4 is 360x640 portrait and the hero frame is 16:9 from 768 px, so "
                + "object-fit: cover crops it to a band — at 1920 the visible strip is the model's chin, "
                + "neck and chest, with her face cut off above the mouth and the headline over a neck. "
                + "The video and its poster are in the library; the section switches itself on once a "
                + "16:9 export at least 1920 px wide, with a poster cut from that same take, is "
                + "imported."),

            ["35b95fa722740429bcf824372297efdb27d284f6940f13c2f7f63136736c99a1"] = new(
                HomeSectionType.ActivewearFeature,
                "the delivered activewear.jpg is a flat-lay of a white linen co-ord and a denim co-ord — "
                + "nothing athletic — and the slot's heading, standfirst, button and link all say "
                + "activewear. A tile whose picture and its link disagree is worse than no tile, and "
                + "re-labelling the slot is the client's editorial decision, not this import's. The "
                + "picture is in the library; the section switches itself on once an activewear "
                + "photograph is imported under this name."),
        });

    /// <summary>The hold for a stored file's checksum, or <see langword="null"/> when there is none.</summary>
    public HeldBackArtwork? For(string? checksum) =>
        checksum is null ? null : byChecksum.GetValueOrDefault(checksum);
}
