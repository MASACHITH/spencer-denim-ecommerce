namespace SpencerDenim.Application.Media.ClientAssets;

/// <summary>What happened to one file in the drop.</summary>
public enum ClientAssetOutcome
{
    /// <summary>Stored and wired up. A first run.</summary>
    Imported = 1,

    /// <summary>
    /// Already in the library, byte for byte, and already wired to the same place. Nothing was
    /// written. This is what a second run of the same drop produces for every file.
    /// </summary>
    AlreadyPresent,

    /// <summary>
    /// Not part of the naming contract, so not imported — and named in the report, because a file
    /// that is quietly ignored is a file nobody knows is missing.
    /// </summary>
    NotInContract,

    /// <summary>
    /// The pipeline refused it. The reason is the pipeline's own words: a wrong signature, a file too
    /// large, an MP4 that is not H.264. Never worked around.
    /// </summary>
    Refused,

    /// <summary>In <c>products/</c> and not mappable to a product. The run fails.</summary>
    Unmappable,

    /// <summary>
    /// The slot this file belongs in already holds a different picture, and nobody asked for it to be
    /// overwritten. Reported rather than applied: on a seeded database a slot holds a demo placeholder,
    /// which counts as empty, so anything else in there is a choice somebody made — a corrected
    /// photograph from the client, or an admin's own upload, and the importer cannot tell those apart.
    /// The operator decides, with <c>--replace</c>.
    /// </summary>
    Conflict,

    /// <summary>The slot held a different picture and <c>--replace</c> was given, so this one took over.</summary>
    Replaced,
}

/// <summary>One line of the import report.</summary>
/// <param name="Folder">Which folder of the drop it came from.</param>
/// <param name="FileName">The file as it is on disk — doubled extension and all.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Detail">A sentence a person can check: where it went, or why it did not.</param>
/// <param name="RepairedFrom">
/// The name the file actually carries, when the importer had to infer the intended one. Printed on its
/// own line in the report so every inferred rename is visible rather than buried.
/// </param>
public sealed record ClientAssetEntry(
    ClientAssetFolder Folder,
    string FileName,
    ClientAssetOutcome Outcome,
    string Detail,
    string? RepairedFrom = null);

/// <summary>
/// The result of one import run: every file, every section that was switched on, and every reason the
/// run failed.
/// </summary>
/// <param name="Entries">One per file found, in folder then name order.</param>
/// <param name="Failures">
/// Empty on a good run. Anything here means the command exits non-zero: an unmappable product
/// photograph, a refusal from the pipeline, a missing folder.
/// </param>
/// <param name="Notes">
/// Things worth saying out loud that are not failures — a section left switched off because its media
/// is still missing, a product that got a card and no hover.
/// </param>
public sealed record ClientAssetImportReport(
    IReadOnlyList<ClientAssetEntry> Entries,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Notes)
{
    /// <summary>Whether the run may be called a success.</summary>
    public bool Succeeded => Failures.Count == 0;

    /// <summary>How many files in a given state.</summary>
    public int Count(ClientAssetOutcome outcome) => Entries.Count(entry => entry.Outcome == outcome);

    /// <summary>Files whose slot already held a different picture, for the report.</summary>
    public IReadOnlyList<ClientAssetEntry> Conflicts =>
        Entries.Where(entry => entry.Outcome == ClientAssetOutcome.Conflict).ToList();

    /// <summary>Every rename the importer had to infer, for the report.</summary>
    public IReadOnlyList<ClientAssetEntry> InferredRenames =>
        Entries.Where(entry => entry.RepairedFrom is not null).ToList();
}

/// <summary>
/// What to import, and whether to actually write anything.
/// </summary>
/// <param name="SourceDirectory">
/// The drop's root — the folder holding <c>logo/</c>, <c>products/</c>, <c>departments/</c>,
/// <c>home/</c> and <c>social/</c>. It is read, never moved and never written to.
/// </param>
/// <param name="DryRun">
/// Resolve every filename and report what would happen, without storing a byte. This is how the
/// naming can be checked against a real catalogue before anything touches the library.
/// </param>
/// <param name="Replace">
/// Overwrite a slot that already holds a different picture, instead of reporting a conflict.
/// <para>
/// This is the "the client sent a corrected photograph" switch, and it is deliberately not the default.
/// A slot holding a demo placeholder is filled either way, because a placeholder is not a choice; a slot
/// holding anything else was chosen by somebody, and an import that silently reverted an admin's upload
/// every time it ran would be a worse bug than the one it fixed.
/// </para>
/// </param>
public sealed record ClientAssetImportRequest(
    string SourceDirectory,
    bool DryRun = false,
    bool Replace = false);

/// <summary>
/// Imports the client's delivered photographs, video and logo (W1-13).
/// <para>
/// <b>Everything goes through <c>IMediaService</c>.</b> Not one file is written and not one
/// <c>media.MediaAssets</c> row is composed here: the importer hands bytes to the same pipeline an
/// admin upload uses, so every file gets the same signature check, the same EXIF stripping, the same
/// WebP rendition set, the same checksum deduplication and the same usage tracking. An import that
/// built its own rows would be a second, untested way into the library, and the first time the two
/// disagreed the difference would show up on a shopper's screen.
/// </para>
/// <para>
/// <b>It is safe to run again.</b> The pipeline already answers a repeat upload with the existing
/// asset rather than a second copy; this adds the other half — a reference row is written only when
/// it is not already there, and media a person has since changed is never overwritten.
/// </para>
/// </summary>
public interface IClientAssetImporter
{
    Task<ClientAssetImportReport> ImportAsync(
        ClientAssetImportRequest request,
        CancellationToken cancellationToken);
}
