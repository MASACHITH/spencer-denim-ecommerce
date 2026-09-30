namespace SpencerDenim.Domain.Media;

/// <summary>
/// The size ceilings that <c>CK_MediaAssets_SizeBytes</c> enforces in the database (doc 08 §3.3:
/// "CK: video ≤ 50 MB; private ≤ 5 MB"), repeated here so an upload is refused with a sentence a
/// person can act on rather than with a constraint violation.
/// <para>
/// These are <b>ceilings</b>, not settings. Configuration (<c>Media__MaxImageBytes</c> and friends)
/// may lower them; nothing may raise them, because the check constraint would then reject the row
/// after the file had already been written.
/// </para>
/// </summary>
public static class MediaLimits
{
    /// <summary>50 MB (doc 03 §3b, assumption A2). Also the video ceiling in the check constraint.</summary>
    public const long MaxVideoBytes = 52_428_800;

    /// <summary>5 MB: a customer's bank slip (doc 08 §3.3 guest upload rules, N10).</summary>
    public const long MaxPrivateBytes = 5_242_880;
}

/// <summary>Retention rules for media files (doc 08 §3.3 and §8).</summary>
public static class MediaRetention
{
    /// <summary>
    /// Doc 08 §3.3: "Delete is a soft delete, allowed only with no usages; files are purged after 30
    /// days." The soft delete happens in <c>IMediaService.DeleteAsync</c>; removing the bytes is the
    /// purge job's work.
    /// </summary>
    /// <remarks>
    /// <b>TODO (W3-08, "Background jobs"):</b> a <c>BackgroundService</c> — doc 03 §2 puts retention
    /// purges there, because SQL Server Express has no Agent — that every night
    /// <list type="number">
    /// <item>deletes the original, the rendition folder and any poster of every asset whose
    /// <c>DeletedAtUtc</c> is more than <see cref="PurgeAfterDays"/> days old, then hard-deletes the
    /// row with a set-based <c>ExecuteDelete</c> (doc 08 N12);</item>
    /// <item>deletes the files and rows of assets left in <c>Status = Processing</c> for more than
    /// <see cref="AbandonedProcessingHours"/> hours, which is what a crash mid-upload leaves behind;</item>
    /// <item>regenerates the renditions of any asset whose <c>RenditionSetVersion</c> is below
    /// <see cref="MediaRenditions.CurrentSetVersion"/>.</item>
    /// </list>
    /// No scheduler exists in the solution yet, so nothing here is wired up; this milestone owns the
    /// soft delete and the guard in front of it, and leaves the constants the job will read.
    /// </remarks>
    public const int PurgeAfterDays = 30;

    /// <summary>
    /// How long an upload may sit in <c>Status = Processing</c> before the purge job treats it as
    /// abandoned. Generous: a 50 MB video on a slow connection is minutes, not hours.
    /// </summary>
    public const int AbandonedProcessingHours = 6;
}
