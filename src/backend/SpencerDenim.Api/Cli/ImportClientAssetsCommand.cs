using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Media.ClientAssets;

namespace SpencerDenim.Api.Cli;

/// <summary>
/// <c>dotnet run --project src/backend/SpencerDenim.Api --no-launch-profile -- import-client-assets --from &lt;dir&gt; [--dry-run] [--replace]</c>
/// <para>
/// <b>Why a verb on the API and not a seeder or a tool project.</b> The import needs the whole
/// composition root: the configured media volumes with their startup validation, the real
/// <c>IMediaService</c>, the database connection, the logging. A seeding flag would tie it to
/// <c>Seed__*</c>, whose meaning is "the shape a development database starts in" — the opposite of the
/// client's own photographs, which have to be importable against a production database that is never
/// seeded. A separate tool project would have to rebuild that composition root and would then be a
/// second place where media settings are wired, free to drift from the one the API uses. A verb on the
/// API reuses the exact process the shop runs on and adds no project, no package and no second wiring.
/// </para>
/// <para>
/// It never starts Kestrel, so it binds no port and collides with nothing.
/// </para>
/// </summary>
internal static class ImportClientAssetsCommand
{
    public const string Verb = "import-client-assets";

    /// <summary>Whether these process arguments ask for the import rather than for the web host.</summary>
    public static bool Matches(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Verb, StringComparison.Ordinal);

    /// <summary>
    /// Runs the import and returns the process exit code.
    /// <list type="table">
    /// <item><term>0</term><description>the drop landed.</description></item>
    /// <item><term>1</term><description>the command line was wrong.</description></item>
    /// <item><term>2</term><description>the import failed and was rolled back.</description></item>
    /// <item><term>3</term><description>
    /// it completed, but files were deliberately not applied — a slot already held a picture somebody
    /// chose. <b>Not 0</b>, because to every script that will ever call this 0 means "the drop landed",
    /// and it did not. <c>-ne 0</c> still catches the real failures; anyone who treats conflicts as
    /// expected can test for 3.
    /// </description></item>
    /// <item><term>4</term><description>the operator interrupted it; everything was rolled back.</description></item>
    /// </list>
    /// </summary>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);

        string? source = null;
        var dryRun = false;
        var replace = false;

        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--from" when index + 1 < args.Length:
                    source = args[++index];
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--replace":
                    replace = true;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument '{args[index]}'.");
                    Usage();
                    return 1;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.Error.WriteLine("--from is required.");
            Usage();
            return 1;
        }

        await using var scope = services.CreateAsyncScope();
        var importer = scope.ServiceProvider.GetRequiredService<IClientAssetImporter>();

        var report = await importer.ImportAsync(
            new ClientAssetImportRequest(Path.GetFullPath(source), dryRun, replace),
            cancellationToken);

        Print(report, dryRun);
        return ExitCodeFor(report);
    }

    /// <summary>
    /// The exit code a report deserves. Its own method because it is the machine-readable half of the
    /// report — the half a deployment script reads — and a rule nothing tests is a rule that drifts.
    /// </summary>
    internal static int ExitCodeFor(ClientAssetImportReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.Cancelled)
        {
            return 4;
        }

        if (!report.Succeeded)
        {
            return 2;
        }

        // A SamePicture file is not counted here: its content did reach the library and its slot does
        // point at it. A Conflict is a file that was read, understood and then deliberately not applied.
        return report.Conflicts.Count > 0 ? 3 : 0;
    }

    private static void Print(ClientAssetImportReport report, bool dryRun)
    {
        Console.WriteLine();
        Console.WriteLine(dryRun ? "Client asset import — DRY RUN, nothing was stored" : "Client asset import");
        Console.WriteLine(new string('=', 72));

        foreach (var group in report.Entries.GroupBy(entry => entry.Folder).OrderBy(group => group.Key))
        {
            Console.WriteLine();
            Console.WriteLine(group.Key.ToString().ToUpperInvariant());

            foreach (var entry in group.OrderBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"  [{entry.Outcome,-14}] {entry.FileName}  ->  {entry.Detail}"));
            }
        }

        var same = report.SamePictures;
        if (same.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("ONE PICTURE, TWO FILES — the library stores it once");
            foreach (var entry in same)
            {
                Console.WriteLine($"  {entry.FileName}");
                Console.WriteLine($"      {entry.Detail}");
            }
        }

        var conflicts = report.Conflicts;
        if (conflicts.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("NOT APPLIED — the slot already holds a picture somebody chose");
            foreach (var entry in conflicts)
            {
                Console.WriteLine($"  {entry.FileName}");
                Console.WriteLine($"      {entry.Detail}");
            }

            Console.WriteLine();
            Console.WriteLine(
                "  Re-run with --replace to overwrite them. A slot holding one of the demo placeholders");
            Console.WriteLine(
                "  is filled without it; this list is only slots holding a real picture.");
        }

        var renames = report.InferredRenames;
        if (renames.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("INFERRED RENAMES (every one of them, so they can be checked)");
            foreach (var entry in renames)
            {
                Console.WriteLine($"  {entry.FileName}");
                Console.WriteLine($"      the name says '{entry.RepairedFrom}'  ->  read as '{entry.Detail}'");
            }
        }

        if (report.Notes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("NOTES");
            foreach (var note in report.Notes)
            {
                Console.WriteLine($"  - {note}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{report.Count(ClientAssetOutcome.Imported)} imported, "
                + $"{report.Count(ClientAssetOutcome.Replaced)} replaced, "
                + $"{report.Count(ClientAssetOutcome.AlreadyPresent)} already present, "
                + $"{report.Count(ClientAssetOutcome.SamePicture)} the same picture as another file, "
                + $"{report.Count(ClientAssetOutcome.Conflict)} not applied, "
                + $"{report.Count(ClientAssetOutcome.NotInContract)} not in the contract, "
                + $"{report.Count(ClientAssetOutcome.Refused)} refused, "
                + $"{report.Count(ClientAssetOutcome.Unmappable)} unmappable."));

        if (report.Succeeded)
        {
            return;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine(
            report.Cancelled
                ? "CANCELLED — the whole run was rolled back and the files it had written were removed"
                : "FAILED — the whole run was rolled back and nothing was written");
        foreach (var failure in report.Failures)
        {
            Console.Error.WriteLine($"  - {failure}");
        }
    }

    private static void Usage()
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"usage: {Verb} --from <directory> [--dry-run] [--replace]");
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            "  <directory> holds the client's drop: logo/, products/, departments/, home/, social/.");
        Console.Error.WriteLine(
            "  --dry-run resolves every filename and reports what would happen, storing nothing.");
        Console.Error.WriteLine(
            "  --replace overwrites a slot that already holds a picture somebody chose, which is what");
        Console.Error.WriteLine(
            "            a corrected photograph from the client needs. Without it those files are");
        Console.Error.WriteLine(
            "            listed and left alone; a slot holding a demo placeholder is filled either way.");
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            "  exit 0 the drop landed - 1 bad usage - 2 failed and rolled back -");
        Console.Error.WriteLine(
            "       3 completed with files not applied - 4 cancelled and rolled back");
    }
}
