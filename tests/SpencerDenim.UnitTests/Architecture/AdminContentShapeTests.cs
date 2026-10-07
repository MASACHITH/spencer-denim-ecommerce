using System.Reflection;
using System.Text.RegularExpressions;
using SpencerDenim.Api.Endpoints.Admin;
using SpencerDenim.Application.Content.Admin.Contracts;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// Pins the content-page and enquiry payloads against the hand-written TypeScript copies in
/// <c>projects/admin/src/app/core/content/admin-content.model.ts</c> (ADM-17, GLB-10).
/// <para>
/// <b>Why this file exists, in one sentence from W1-12's post-mortem:</b> "every category save silently
/// wiped the client's images, and 1,571 passing tests missed it, because every fixture set those columns
/// to <c>null</c>, so <c>null → null</c> round-tripped vacuously." The cause was a front-end model that
/// had never listed three columns. These screens have the same hand-written copy, so the pairing is
/// pinned here rather than left to review — the arrangement <c>AdminSettingsShapeTests</c> established
/// for ADM-22 and <c>AdminProductEnumContractTests</c> for ADM-10.
/// </para>
/// <para>
/// It needs no database and no browser: it is a file read and some reflection.
/// </para>
/// </summary>
public sealed class AdminContentShapeTests
{
    private const string ModelFile = "src/frontend/projects/admin/src/app/core/content/admin-content.model.ts";

    /// <summary>A property on a TypeScript interface: <c>readonly slug: string;</c>.</summary>
    private static readonly Regex Property = new(
        @"readonly\s+(?<name>\w+)\??:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A member of a string-literal union: <c>| 'Replied'</c> or <c>= 'New'</c>.</summary>
    private static readonly Regex UnionMember = new(
        @"'(?<name>\w+)'",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The list payload's properties are exactly what the screen's interface declares.
    /// <para>
    /// Both directions: a property added to the DTO and not the interface is a column the screen will
    /// never show, and one added to the interface and not the DTO is a field that silently reads
    /// <c>undefined</c> — which for <c>canBeDeleted</c> would mean offering a Remove button on a page
    /// that cannot be removed.
    /// </para>
    /// </summary>
    [Fact]
    public void The_page_list_payload_has_exactly_the_properties_the_screen_reads() =>
        AssertPairs<AdminContentPageListItemDto>("AdminContentPageListItem");

    /// <summary>
    /// The editor's payload is the list's plus the three fields only the editor needs, which is what
    /// <c>AdminContentPage extends AdminContentPageListItem</c> says on the TypeScript side.
    /// </summary>
    [Fact]
    public void The_page_payload_adds_exactly_the_body_and_the_two_seo_fields()
    {
        var extra = Names<AdminContentPageDto>()
            .Except(Names<AdminContentPageListItemDto>(), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["BodyHtml", "SeoDescription", "SeoTitle"], extra);

        var declared = PropertiesOf("AdminContentPage");
        Assert.Equal(["bodyHtml", "seoDescription", "seoTitle"], declared);
    }

    [Fact]
    public void The_enquiry_list_payload_has_exactly_the_properties_the_screen_reads() =>
        AssertPairs<AdminContactSubmissionListItemDto>("AdminContactEnquiryListItem");

    [Fact]
    public void The_enquiry_payload_has_exactly_the_properties_the_screen_reads() =>
        AssertPairs<AdminContactSubmissionDto>("AdminContactEnquiry");

    [Fact]
    public void The_inbox_page_payload_has_exactly_the_properties_the_screen_reads() =>
        AssertPairs<AdminContactSubmissionsDto>("AdminContactEnquiries");

    /// <summary>
    /// <b>The list payload carries no message body, and that is doc 11 P8 rather than a size
    /// optimisation.</b> A customer may paste bank account details into it, and a list is the payload
    /// most likely to be cached, screenshotted into a support ticket, or widened into a CSV export
    /// later. The absence is asserted because the natural "improvement" is to add it.
    /// </summary>
    [Fact]
    public void The_enquiry_list_payload_carries_a_preview_and_not_the_message()
    {
        var names = Names<AdminContactSubmissionListItemDto>();

        Assert.Contains("Preview", names);
        Assert.DoesNotContain("Message", names);
        Assert.DoesNotContain("Phone", names);
        Assert.DoesNotContain("IpAddress", names);
    }

    /// <summary>
    /// The status enum travels as its <b>name</b> (<c>JsonStringEnumConverter</c> is registered globally),
    /// so the TypeScript union has to be the member names rather than numbers. This is ADM-10's own
    /// defect, which shipped <c>export type ProductMediaRole = 1 | 2 | 3;</c> against an API answering
    /// <c>"Gallery"</c>.
    /// </summary>
    [Fact]
    public void The_client_declares_the_enquiry_statuses_the_enum_has()
    {
        var declared = UnionMembers("ContactEnquiryStatus");

        Assert.Equal(
            Enum.GetNames<ContactSubmissionStatus>().Order(StringComparer.Ordinal),
            declared);
    }

    /// <summary>
    /// <c>New</c> is deliberately absent from the settable list: "unread it" is not one of doc 11 P15's
    /// transitions, and the status would then say nobody had looked at an enquiry somebody had. Asserted
    /// because the obvious tidy-up is to make the two lists one.
    /// </summary>
    [Fact]
    public void The_client_offers_three_settable_statuses_and_not_New()
    {
        var model = ReadRepositoryFile(ModelFile);
        var settable = ArrayLiteral(model, "SETTABLE_ENQUIRY_STATUSES");
        var all = ArrayLiteral(model, "ENQUIRY_STATUSES");

        Assert.Equal(["Read", "Replied", "Spam"], settable.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("New", settable);

        Assert.Equal(
            Enum.GetNames<ContactSubmissionStatus>().Order(StringComparer.Ordinal),
            all.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The write request's fields are exactly the page's editable ones, plus the concurrency token.
    /// <para>
    /// This is the pairing W1-12 lost. A field added to the command and not the request would make every
    /// save clear it; a field added to the request and not the command would make the screen silently
    /// ineffective.
    /// </para>
    /// </summary>
    [Fact]
    public void The_page_request_carries_one_field_per_editable_column_plus_the_token()
    {
        var fields = typeof(AdminContentPageRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["BodyHtml", "RowVersion", "SeoDescription", "SeoTitle", "Slug", "Title"],
            fields);

        // And the client's own values type carries the same set, less the token, which it passes
        // separately.
        Assert.Equal(
            ["bodyHtml", "seoDescription", "seoTitle", "slug", "title"],
            PropertiesOf("ContentPageValues"));
    }

    /// <summary>
    /// <b>No request on these screens can name a <c>systemKey</c>.</b> One that could would allow a
    /// second <c>privacy</c> page, or a page a client created claiming the protection against deletion
    /// that doc 11 P13 gives the built-in six.
    /// </summary>
    [Fact]
    public void No_request_lets_a_caller_name_a_system_key()
    {
        foreach (var type in new[]
                 {
                     typeof(AdminContentPageRequest),
                     typeof(SetContentPagePublishedRequest),
                     typeof(SetContactEnquiryStatusRequest),
                     typeof(DeleteContactEnquiryRequest),
                 })
        {
            Assert.DoesNotContain(
                type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name),
                name => name.Contains("SystemKey", StringComparison.OrdinalIgnoreCase));
        }

        Assert.DoesNotContain("systemKey", PropertiesOf("ContentPageValues"));
    }

    private static void AssertPairs<TDto>(string interfaceName)
    {
        var expected = Names<TDto>()
            .Select(Camel)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, PropertiesOf(interfaceName));
    }

    private static List<string> Names<TDto>() =>
        typeof(TDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>The API camel-cases property names (<c>JsonSerializerDefaults.Web</c>).</summary>
    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>Every property of one TypeScript interface, in order, by reading the file.</summary>
    private static List<string> PropertiesOf(string interfaceName)
    {
        var body = BlockOf(ReadRepositoryFile(ModelFile), interfaceName);

        return Property
            .Matches(body)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Every member of one TypeScript string-literal union type.</summary>
    private static List<string> UnionMembers(string typeName)
    {
        var text = ReadRepositoryFile(ModelFile);
        var start = text.IndexOf($"export type {typeName}", StringComparison.Ordinal);

        Assert.True(start >= 0, $"Could not find `export type {typeName}` in {ModelFile}.");

        var end = text.IndexOf(';', start);
        Assert.True(end > start, $"`export type {typeName}` has no terminator.");

        return UnionMember
            .Matches(text[start..end])
            .Select(match => match.Groups["name"].Value)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The string literals in one exported array constant.
    /// <para>
    /// The opening bracket is found after the <c>=</c>, not after the name: these constants carry a type
    /// annotation (<c>: readonly ContactEnquiryStatus[]</c>) whose own <c>[]</c> comes first, so the
    /// first version read an empty array and the test passed by asserting nothing against nothing.
    /// </para>
    /// </summary>
    private static List<string> ArrayLiteral(string text, string constantName)
    {
        var start = text.IndexOf($"export const {constantName}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find `export const {constantName}` in {ModelFile}.");

        var equals = text.IndexOf('=', start);
        Assert.True(equals > start, $"`{constantName}` has no initialiser.");

        var open = text.IndexOf('[', equals);
        var close = text.IndexOf(']', open);
        Assert.True(open > 0 && close > open, $"`{constantName}` is not an array literal.");

        return UnionMember
            .Matches(text[open..close])
            .Select(match => match.Groups["name"].Value)
            .ToList();
    }

    /// <summary>
    /// The body of one TypeScript interface, read by balancing braces — so a nested object type inside
    /// it does not end it early.
    /// </summary>
    /// <param name="text">The whole file.</param>
    /// <param name="interfaceName">
    /// The interface's exact name.
    /// <para>
    /// <b>Matched as a whole word, which the first version did not do.</b>
    /// <c>IndexOf("interface AdminContentPage")</c> found <c>AdminContentPageListItem</c>, which is
    /// declared earlier and of which the wanted name is a prefix — so two tests read the wrong
    /// interface's properties and failed for a reason that had nothing to do with the payload. The
    /// `AdminContactEnquiry` family has the same trap three ways over.
    /// </para>
    /// </param>
    private static string BlockOf(string text, string interfaceName)
    {
        var declaration = new Regex(
            $@"interface\s+{Regex.Escape(interfaceName)}\b",
            RegexOptions.CultureInvariant);

        var match = declaration.Match(text);
        Assert.True(match.Success, $"Could not find `interface {interfaceName}` in {ModelFile}.");

        var start = match.Index;
        var open = text.IndexOf('{', start);
        Assert.True(open > 0, $"`interface {interfaceName}` has no body.");

        var depth = 0;
        for (var index = open; index < text.Length; index++)
        {
            if (text[index] == '{')
            {
                depth++;
            }
            else if (text[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[(open + 1)..index];
                }
            }
        }

        Assert.Fail($"`interface {interfaceName}` has an unbalanced body.");

        return string.Empty;
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var path = Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(path), $"Expected to find {relativePath} at {path}.");

        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "frontend")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root above the test output directory.");
    }
}
