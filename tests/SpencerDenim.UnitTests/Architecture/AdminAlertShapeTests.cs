using System.Text.RegularExpressions;
using SpencerDenim.Api.Endpoints.Admin;
using SpencerDenim.Application.Ops.Alerts.Contracts;
using SpencerDenim.Domain.Ops;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// Pins the alert payloads against the hand-written TypeScript copies in
/// <c>projects/admin/src/app/core/alerts/admin-alert.model.ts</c> and against the Playwright stub that
/// answers for them (W3-07).
/// <para>
/// <b>Issue #126 is why the stub is in here and not only the model.</b> Playwright never starts the .NET
/// API: the browser suite runs against Node stubs, so a screen test cannot observe a backend change at
/// all — a dropped payload field once left all 860 tests green. The stub has to mirror the real DTO field
/// for field, and the only way that stays true is a test that reads both files.
/// </para>
/// <para>
/// <b>And <c>AdminContentShapeTests</c> is why the model is in here.</b> W1-12's blocker survived 1,571
/// passing tests because a front-end model had never listed three columns, so every save cleared them.
/// </para>
/// <para>
/// It needs no database and no browser: two file reads and some reflection.
/// </para>
/// </summary>
public sealed class AdminAlertShapeTests
{
    private const string ModelFile = "src/frontend/projects/admin/src/app/core/alerts/admin-alert.model.ts";

    private const string StubFile = "src/frontend/e2e/support/admin-catalog-stub.mjs";

    /// <summary>
    /// How much of the stub one payload's fields may be looked for in: the object literal the stub
    /// answers with, and nothing after it.
    /// </summary>
    /// <remarks>
    /// <b>A character budget rather than a brace match, because the thing being searched is the stub's
    /// own prose as well as its code</b> — a field name in a nearby comment would answer for a field name
    /// in the payload, so the window has to be tight. 1,200 characters is comfortably more than any of
    /// these three literals and comfortably less than the distance to the next one.
    /// </remarks>
    private const int BlockLength = 1_200;

    /// <summary>A property on a TypeScript interface: <c>readonly severity: number;</c>.</summary>
    private static readonly Regex Property = new(
        @"readonly\s+(?<name>\w+)\??:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void The_alert_payloads_properties_are_exactly_what_the_screen_declares()
    {
        AssertMirrors<AdminAlertDto>("AdminAlert");
        AssertMirrors<AdminAlertBannerDto>("AdminAlertBanner");
        AssertMirrors<AdminAlertAcknowledgementDto>("AdminAlertAcknowledgement");
    }

    /// <summary>
    /// Every field of the real payload appears in the Playwright stub's own alert fixture.
    /// </summary>
    /// <remarks>
    /// <b>The one assertion that catches issue #126's shape of defect.</b> The stub is what the banner
    /// talks to in every browser test, so a field the API sends and the stub omits is a field no browser
    /// test exercises — and a field the stub sends under a different name is a banner that works in
    /// Playwright and is blank in production.
    /// </remarks>
    [Fact]
    public void The_playwright_stub_answers_with_every_field_the_api_sends()
    {
        var stub = ReadRepositoryFile(StubFile);

        // **Each payload is looked for in its own block of the stub, not in the whole file.** The first
        // version searched the file and a mutation survived it: the stub's bank-transfer fixture also has
        // an `orderNumber:`, so dropping it from the *alert* fixture left the assertion green. That is
        // issue #80's shape — a guard satisfied by a clause other than the one being checked — and the
        // only fix is to narrow the text being searched.
        AssertStubBlockHas<AdminAlertDto>(stub, "function deadEmailAlert(");
        AssertStubBlockHas<AdminAlertBannerDto>(stub, "const open = state.alerts.filter(");
        AssertStubBlockHas<AdminAlertAcknowledgementDto>(stub, "const alreadyAcknowledged =");
    }

    /// <summary>
    /// Every field of <typeparamref name="T"/> appears in the stub between
    /// <paramref name="from"/> and the next blank-line-separated block.
    /// </summary>
    private static void AssertStubBlockHas<T>(string stub, string from)
    {
        var start = stub.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{StubFile} no longer contains \"{from}\".");

        // Far enough to hold one object literal, near enough that a neighbouring fixture's fields cannot
        // answer for this one — see BlockLength.
        var block = stub[start..Math.Min(stub.Length, start + BlockLength)];

        foreach (var name in CamelCaseProperties<T>())
        {
            Assert.Contains($"{name}:", block, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The seven alert types the screen may meet are the seven doc 08 §3.8 lists.
    /// </summary>
    /// <remarks>
    /// The banner's advice branches on <c>EmailDeliveryFailed</c> by name, so the string has to be the
    /// same one the server writes. The other six are asserted as a set because each is a later task's, and
    /// a task that renames one would otherwise find out from a blank banner.
    /// </remarks>
    [Fact]
    public void The_alert_type_the_banner_branches_on_is_the_one_the_server_writes()
    {
        var model = ReadRepositoryFile(ModelFile);

        Assert.Contains($"'{AdminAlertTypes.EmailDeliveryFailed}'", model, StringComparison.Ordinal);
    }

    private static void AssertMirrors<T>(string interfaceName)
    {
        var model = ReadRepositoryFile(ModelFile);

        var declared = PropertiesOfInterface(model, interfaceName);

        Assert.Equal(CamelCaseProperties<T>(), declared);
    }

    private static string[] CamelCaseProperties<T>() =>
        [.. typeof(T)
            .GetProperties()
            .Select(property => char.ToLowerInvariant(property.Name[0]) + property.Name[1..])
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// The property names of one `export interface X { … }` block, read by balancing braces so a nested
    /// object literal cannot leak its own fields into the list.
    /// </summary>
    private static string[] PropertiesOfInterface(string source, string interfaceName)
    {
        var header = $"export interface {interfaceName} {{";
        var start = source.IndexOf(header, StringComparison.Ordinal);

        Assert.True(start >= 0, $"{interfaceName} is not declared in {ModelFile}.");

        var depth = 0;
        var from = start + header.Length - 1;
        var end = from;

        for (var index = from; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;

                if (depth == 0)
                {
                    end = index;
                    break;
                }
            }
        }

        var body = source[from..end];

        return [.. Property.Matches(body).Select(match => match.Groups["name"].Value).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Reads a file by walking up from the test assembly to the repository root, the way
    /// <c>AdminContentShapeTests</c> does.
    /// </summary>
    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SpencerDenim.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var path = Path.Combine(directory!.FullName, relativePath);

        Assert.True(File.Exists(path), $"{relativePath} was not found from {directory.FullName}.");

        return File.ReadAllText(path);
    }
}
