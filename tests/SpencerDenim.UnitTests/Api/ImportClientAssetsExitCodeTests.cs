using SpencerDenim.Api.Cli;
using SpencerDenim.Application.Media.ClientAssets;

namespace SpencerDenim.UnitTests.Api;

/// <summary>
/// What the import tells a script, as opposed to what it tells a person.
/// <para>
/// The report's prose is for the operator; the exit code is for the deployment step that runs this
/// unattended, and the two have to agree. <b>0 means "the drop landed"</b> — so a run that read a file,
/// understood it and then deliberately did not apply it must not say 0, which is what it used to say.
/// </para>
/// </summary>
public sealed class ImportClientAssetsExitCodeTests
{
    [Fact]
    public void A_clean_run_says_the_drop_landed() =>
        Assert.Equal(0, ImportClientAssetsCommand.ExitCodeFor(Report()));

    [Fact]
    public void A_failed_run_is_two() =>
        Assert.Equal(2, ImportClientAssetsCommand.ExitCodeFor(Report(failures: ["the hero is not H.264"])));

    /// <summary>Not 0: `-ne 0` still catches the real failures, and 3 is testable on its own.</summary>
    [Fact]
    public void A_run_with_files_deliberately_not_applied_is_three() =>
        Assert.Equal(3, ImportClientAssetsCommand.ExitCodeFor(Report(entries: [Entry(ClientAssetOutcome.Conflict)])));

    /// <summary>
    /// A file that shares another's picture is <b>not</b> in that: its content reached the library and
    /// its slot points at it, so nothing was withheld.
    /// </summary>
    [Fact]
    public void A_run_whose_only_oddity_is_one_picture_in_two_files_is_still_zero() =>
        Assert.Equal(0, ImportClientAssetsCommand.ExitCodeFor(Report(entries: [Entry(ClientAssetOutcome.SamePicture)])));

    [Fact]
    public void A_cancelled_run_is_four_even_though_it_also_failed() =>
        Assert.Equal(
            4,
            ImportClientAssetsCommand.ExitCodeFor(Report(failures: ["cancelled"], cancelled: true)));

    /// <summary>Cancellation outranks a conflict: the headline is that the operator stopped it.</summary>
    [Fact]
    public void Cancellation_outranks_a_conflict() =>
        Assert.Equal(
            4,
            ImportClientAssetsCommand.ExitCodeFor(
                Report(entries: [Entry(ClientAssetOutcome.Conflict)], failures: ["cancelled"], cancelled: true)));

    private static ClientAssetImportReport Report(
        IReadOnlyList<ClientAssetEntry>? entries = null,
        IReadOnlyList<string>? failures = null,
        bool cancelled = false) =>
        new(entries ?? [], failures ?? [], [], cancelled);

    private static ClientAssetEntry Entry(ClientAssetOutcome outcome) =>
        new(ClientAssetFolder.Products, "a-garment-1.jpg", outcome, "detail");
}
