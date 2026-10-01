using SpencerDenim.Domain.Content;

namespace SpencerDenim.Infrastructure.Media.ClientAssets;

/// <summary>A picture that does not fit the slot its filename claims, and the section it keeps off.</summary>
/// <param name="Section">
/// The homepage block the import will not switch on while this picture fills it.
/// <para>
/// <b>The hold is about this picture in this slot, not about these bytes everywhere.</b> The same file
/// delivered under a different contract name would fill that other slot and would not be held — which is
/// the intended reading, because the judgement recorded here is "this is not an activewear photograph
/// and the activewear slot says it is", not "nobody may ever use this picture". A reader looking for a
/// quarantine will not find one.
/// </para>
/// </param>
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
/// tell whether the artwork is of the right thing. Two of the files in the first delivery were not: the
/// hero video is portrait where the frame is 16:9, and the activewear banner is a flat-lay of linen and
/// denim. Switching either on would have put a cropped face or a mislabelled tile on the front page.
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
    /// <summary>
    /// The holds in force. <b>Empty as of W1-14</b> — both holds from the 30 September 2026 delivery
    /// were released by a decision, not by a new file, and the mechanism is kept for the next one.
    /// <para>
    /// <b>The hero (HOME-01).</b> Held because a 360x640 portrait video in a 16:9 frame centre-crops to
    /// the model's chest. Released because <c>object-position: center 30%</c> on <c>.hero__video</c>
    /// brings her face into the band at every width from 768 to 1920 — measured by screenshot, pinned by
    /// an e2e test. It is the best this file can do and not a fix: 1920 px of frame from 360 px of source
    /// is a 5.3x upscale, at 1920 no garment is in frame at all, and past about 2,900 px of hero width no
    /// value of <c>object-position</c> fits her face. A 16:9 export at least 1920 px wide, with a poster cut from the same take, is
    /// still owed.
    /// </para>
    /// <para>
    /// <b>The activewear feature (HOME-06).</b> Held because the picture is a flat-lay of a white linen
    /// co-ord and a denim co-ord while the heading, standfirst, button and link all said activewear —
    /// and relabelling the slot was the client's editorial decision, not the import's. The client made
    /// it: the slot is "Linen and denim" now and opens the women's linen and denim listing, so the
    /// picture, the words and the destination agree and there is nothing left to hold (issue #63).
    /// </para>
    /// </summary>
    public static ClientAssetHolds Default { get; } = new(
        new Dictionary<string, HeldBackArtwork>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The hold for a stored file's checksum, or <see langword="null"/> when there is none.</summary>
    public HeldBackArtwork? For(string? checksum) =>
        checksum is null ? null : byChecksum.GetValueOrDefault(checksum);
}
