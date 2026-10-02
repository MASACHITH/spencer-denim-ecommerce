using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SpencerDenim.UnitTests.Tools;

/// <summary>
/// <c>tools/kids-stand-in-photos.ps1 -Out</c> is the one destructive operation on this branch: it
/// deletes whatever is at that path, recursively, before rebuilding the generated drop. These tests
/// are about what it must refuse to delete.
/// <para>
/// <b>Why this is worth a test project shelling out to PowerShell.</b> <c>client-assets/</c> is
/// untracked in every repository here — <c>git ls-files client-assets</c> returns nothing — so it is
/// the project's only copy of the photographs the client delivered. <c>-ClientAssets</c> and
/// <c>-Out</c> are adjacent parameters, both take a path, and <c>-ClientAssets</c> is the one an
/// operator always types. A single transposition would have taken the delivery, with no prompt and
/// exit 0. Nothing about that is theoretical: it was reproduced against a decoy folder before the
/// guard existed.
/// </para>
/// <para>
/// The surrounding code already holds this line — <c>ClientAssetImporter</c> refuses to overwrite "a
/// picture somebody chose" and exits 3 rather than guess, under its own comment that "a missed file is
/// litter; a deleted file is data loss". A recursive delete on an operator-supplied path with no guard
/// was below the standard of the file it shipped next to.
/// </para>
/// <para>
/// Every path here is under the test's own temporary directory, asserted before the script is run.
/// Nothing in these tests can address the real drop.
/// </para>
/// </summary>
public sealed class KidsStandInDropGuardTests : IDisposable
{
    /// <summary>A file name no generated drop would ever contain, and a body that says why it matters.</summary>
    private const string Decoy = "IRREPLACEABLE-CLIENT-PHOTO.txt";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sd-standin-guard",
        Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The headline case: <c>-Out</c> points at a folder holding somebody's files. The script must
    /// refuse, leave every one of them where it is, and write no stand-in.
    /// </summary>
    [Fact]
    public void A_folder_holding_anything_else_is_refused_and_left_untouched()
    {
        var delivery = GivenADelivery();
        var decoy = Path.Combine(_root, "somebody-elses-folder");
        Directory.CreateDirectory(decoy);
        File.WriteAllText(Path.Combine(decoy, Decoy), "the only copy");
        File.WriteAllText(Path.Combine(decoy, "notes.md"), "notes");

        var run = Run(delivery, decoy);

        Assert.NotEqual(0, run.ExitCode);
        Assert.True(File.Exists(Path.Combine(decoy, Decoy)), "The decoy file was deleted.");
        Assert.True(File.Exists(Path.Combine(decoy, "notes.md")), "The decoy file was deleted.");
        Assert.False(Directory.Exists(Path.Combine(decoy, "products")), "A stand-in drop was built over it.");

        // The refusal has to name the path, or the operator cannot tell which argument was wrong.
        Assert.Contains(Decoy, run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The transposition that motivates all of this: <c>-Out</c> given the delivery's own path. Every
    /// delivered photograph has to still be there.
    /// </summary>
    [Fact]
    public void The_delivery_itself_is_refused()
    {
        var delivery = GivenADelivery();
        var before = Directory.GetFiles(Path.Combine(delivery, "products")).Length;

        var run = Run(delivery, delivery);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Equal(before, Directory.GetFiles(Path.Combine(delivery, "products")).Length);
    }

    /// <summary>A folder that <i>contains</i> the delivery. Deleting it recursively takes the delivery with it.</summary>
    [Fact]
    public void A_folder_containing_the_delivery_is_refused()
    {
        var delivery = GivenADelivery();
        var parent = Directory.GetParent(delivery)!.FullName;

        var run = Run(delivery, parent);

        Assert.NotEqual(0, run.ExitCode);
        Assert.True(Directory.Exists(Path.Combine(delivery, "products")), "The delivery was deleted.");
    }

    /// <summary>A folder inside the delivery, which a recursive delete would empty out of it.</summary>
    [Fact]
    public void A_folder_inside_the_delivery_is_refused()
    {
        var delivery = GivenADelivery();
        var inside = Path.Combine(delivery, "products");

        var run = Run(delivery, inside);

        Assert.NotEqual(0, run.ExitCode);
        Assert.True(Directory.Exists(inside), "The delivery's products folder was deleted.");
        Assert.NotEmpty(Directory.GetFiles(inside));
    }

    /// <summary><c>-Remove</c> deletes too, so it is guarded by the same rule.</summary>
    [Fact]
    public void Remove_refuses_a_folder_that_is_not_a_generated_drop()
    {
        var delivery = GivenADelivery();
        var decoy = Path.Combine(_root, "not-a-drop");
        Directory.CreateDirectory(decoy);
        File.WriteAllText(Path.Combine(decoy, Decoy), "the only copy");

        var run = Run(delivery, decoy, remove: true);

        Assert.NotEqual(0, run.ExitCode);
        Assert.True(File.Exists(Path.Combine(decoy, Decoy)), "-Remove deleted a folder it did not create.");
    }

    /// <summary>
    /// The guard must not block the job. A path that does not exist yet is the ordinary first run.
    /// </summary>
    [Fact]
    public void A_path_that_does_not_exist_yet_is_built()
    {
        var delivery = GivenADelivery();
        var fresh = Path.Combine(_root, "fresh-drop");

        var run = Run(delivery, fresh);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(
            KidsStandInMappingFile.Rows().Count * 2,
            Directory.GetFiles(Path.Combine(fresh, "products")).Length);
    }

    /// <summary>
    /// And a drop this script built before is rebuilt without complaint, because re-running it is the
    /// documented way to use it. A guard that refused its own output would be a guard nobody keeps.
    /// </summary>
    [Fact]
    public void A_drop_this_script_built_before_is_rebuilt()
    {
        var delivery = GivenADelivery();
        var drop = Path.Combine(_root, "regenerated");

        Assert.Equal(0, Run(delivery, drop).ExitCode);
        var second = Run(delivery, drop);

        Assert.Equal(0, second.ExitCode);
        Assert.Equal(
            KidsStandInMappingFile.Rows().Count * 2,
            Directory.GetFiles(Path.Combine(drop, "products")).Length);
    }

    /// <summary>
    /// A generated drop with one stray file in it is still refused. The check is "everything here is
    /// mine", not "something here is mine": a folder somebody else has put a file into is a folder
    /// somebody else is using.
    /// </summary>
    [Fact]
    public void A_generated_drop_with_a_stray_file_in_it_is_refused()
    {
        var delivery = GivenADelivery();
        var drop = Path.Combine(_root, "drop-with-a-stray");

        Assert.Equal(0, Run(delivery, drop).ExitCode);
        File.WriteAllText(Path.Combine(drop, "products", Decoy), "somebody put this here");

        var run = Run(delivery, drop);

        Assert.NotEqual(0, run.ExitCode);
        Assert.True(File.Exists(Path.Combine(drop, "products", Decoy)), "The stray file was deleted.");
    }

    /// <summary>
    /// <b>A relative <c>-Out</c>, from a shell whose location has moved.</b> The shape no other test
    /// here could express, and the one that got past the guard.
    /// <para>
    /// <c>[System.IO.Path]::GetFullPath</c> resolves a relative path against
    /// <c>[Environment]::CurrentDirectory</c>; <c>Test-Path -LiteralPath</c> and
    /// <c>Remove-Item -LiteralPath</c> resolve it against PowerShell's <c>$PWD</c>. <b>`Set-Location`
    /// does not update <c>[Environment]::CurrentDirectory</c></b>, so after one <c>cd</c> the guard
    /// inspected one folder and the delete took another — and because the guard's folder did not exist,
    /// <c>Assert-SafeOutputFolder</c> returned early at its "nothing there yet" check without running
    /// the content check at all.
    /// </para>
    /// <para>
    /// Every other test in this class passes absolute paths for both parameters and sets no working
    /// directory, so <c>GetFullPath</c> is effectively identity and the divergence cannot appear. The
    /// trigger is not exotic: it is the normal state of any interactive session after a single
    /// <c>cd</c>, and a relative <c>-Out</c> is a natural thing to type.
    /// </para>
    /// </summary>
    [Fact]
    public void A_relative_out_is_guarded_as_the_folder_the_delete_will_really_take()
    {
        var delivery = GivenADelivery();

        // Where the shell will be when the script runs. The process starts somewhere else.
        var shellHere = Path.Combine(_root, "shell-moved-here");
        var decoy = Path.Combine(shellHere, "decoy");

        // A folder the content check would refuse three times over: a stray file at the top, a stray
        // file inside products/, and a folder the tool never creates.
        Directory.CreateDirectory(Path.Combine(decoy, "products"));
        Directory.CreateDirectory(Path.Combine(decoy, "logo"));
        File.WriteAllText(Path.Combine(decoy, "products", Decoy), "the only copy");
        File.WriteAllText(Path.Combine(decoy, "SIGNED-PROPOSAL.txt"), "signed");
        File.WriteAllText(Path.Combine(decoy, "logo", "monogram.png"), "artwork");

        var run = RunFromMovedShell(delivery, relativeOut: "decoy", shellLocation: shellHere);

        Assert.NotEqual(0, run.ExitCode);
        Assert.True(File.Exists(Path.Combine(decoy, "products", Decoy)), "The decoy photograph was deleted.");
        Assert.True(File.Exists(Path.Combine(decoy, "SIGNED-PROPOSAL.txt")), "The decoy document was deleted.");
        Assert.True(File.Exists(Path.Combine(decoy, "logo", "monogram.png")), "The decoy artwork was deleted.");
    }

    /// <summary>
    /// And the same shape must still <i>work</i> where it should, which also pins <b>which</b> of the two
    /// interpretations wins: PowerShell's <c>$PWD</c>, the one the delete has always used. A relative
    /// <c>-Out</c> naming a folder that does not exist yet builds the drop beside the shell, not beside
    /// wherever the process happened to start.
    /// </summary>
    [Fact]
    public void A_relative_out_builds_the_drop_beside_the_shell_not_the_process()
    {
        var delivery = GivenADelivery();
        var shellHere = Path.Combine(_root, "shell-moved-for-a-build");
        Directory.CreateDirectory(shellHere);

        var run = RunFromMovedShell(delivery, relativeOut: "fresh", shellLocation: shellHere);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(
            KidsStandInMappingFile.Rows().Count * 2,
            Directory.GetFiles(Path.Combine(shellHere, "fresh", "products")).Length);

        // And nothing was created against the process's own directory, which is where GetFullPath
        // would have put it.
        Assert.False(
            Directory.Exists(Path.Combine(_root, "fresh")),
            "The drop was built against the process directory rather than the shell's location.");
    }

    /// <summary>
    /// A fake delivery: a <c>products/</c> folder carrying every donor's two photographs, under the two
    /// damaged shapes the real batch arrived in, so the script's own filename repairs are exercised too.
    /// The bytes are irrelevant — these tests never import.
    /// </summary>
    private string GivenADelivery()
    {
        var delivery = Path.Combine(_root, "delivery", "client-assets");
        var products = Path.Combine(delivery, "products");
        Directory.CreateDirectory(products);

        foreach (var row in KidsStandInMappingFile.Rows())
        {
            foreach (var index in new[] { 1, 2 })
            {
                var name = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{row.DonorSlug}-{index}.jpg.jpg");
                File.WriteAllText(Path.Combine(products, name), $"fake bytes for {name}");
            }
        }

        return delivery;
    }

    /// <summary>
    /// Runs the script from a shell that has moved: the process starts in the test's own root and the
    /// command does <c>Set-Location</c> first, which is what makes <c>$PWD</c> and
    /// <c>[Environment]::CurrentDirectory</c> disagree. <c>-File</c> cannot express this — it needs a
    /// <c>-Command</c> with two statements in it.
    /// </summary>
    private ScriptRun RunFromMovedShell(string clientAssets, string relativeOut, string shellLocation)
    {
        // The relative path must still land inside the test's own temp tree, whichever resolver wins.
        foreach (var path in new[] { clientAssets, shellLocation, Path.Combine(shellLocation, relativeOut) })
        {
            Assert.StartsWith(_root, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }

        Assert.False(Path.IsPathRooted(relativeOut), "The point of this test is a relative -Out.");

        var script = KidsStandInMappingFile.ScriptPath();
        var command =
            $"Set-Location -LiteralPath '{shellLocation}'; "
            + $"& '{script}' -ClientAssets '{clientAssets}' -Out '{relativeOut}' -NoImport";

        var info = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,

            // Deliberately NOT shellLocation: this is what leaves
            // [Environment]::CurrentDirectory pointing somewhere the shell is not.
            WorkingDirectory = _root,
        };

        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", command })
        {
            info.ArgumentList.Add(argument);
        }

        return Capture(info);
    }

    private sealed record ScriptRun(int ExitCode, string Output);

    private ScriptRun Run(string clientAssets, string output, bool remove = false)
    {
        // Belt and braces: nothing these tests pass may sit outside the test's own temp directory.
        foreach (var path in new[] { clientAssets, output })
        {
            Assert.StartsWith(_root, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }

        var script = KidsStandInMappingFile.ScriptPath();
        var arguments = new List<string>
        {
            "-NoProfile",
            "-NonInteractive",
            "-File",
            script,
            "-ClientAssets",
            clientAssets,
            "-Out",
            output,
        };

        arguments.Add(remove ? "-Remove" : "-NoImport");

        var info = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Capture(info);
    }

    private static ScriptRun Capture(ProcessStartInfo info)
    {
        using var process = Start(info);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);

        return new ScriptRun(process.ExitCode, new StringBuilder(stdout).Append(stderr).ToString());
    }

    /// <summary>
    /// Starts <c>pwsh</c>, or skips with a reason. PowerShell 7 is on every runner this project uses
    /// (<c>ubuntu-latest</c>) and on every developer machine here, so a missing one is worth saying out
    /// loud rather than passing quietly — and worth skipping rather than failing, which is the
    /// complaint already open against #74.
    /// </summary>
    private static Process Start(ProcessStartInfo info)
    {
        try
        {
            return Process.Start(info) ?? throw new InvalidOperationException("pwsh did not start.");
        }
        catch (Exception exception)
        {
            Assert.Skip($"PowerShell 7 (pwsh) is not on PATH, so the script cannot be driven: {exception.Message}");
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
