using System.Reflection;
using System.Text.RegularExpressions;
using SpencerDenim.Application.Admin.Customers.Contracts;
using SpencerDenim.Application.Marketing.Newsletter.Admin;
using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.UnitTests.Architecture;

/// <summary>
/// Pins the customer (ADM-15) and newsletter (ADM-21) payloads against their hand-written TypeScript copies,
/// in both directions — the arrangement <c>AdminContentShapeTests</c> established after W1-12, where a client
/// model that had never listed three columns wiped every category image past 1,571 green tests.
/// <para>
/// The Playwright stub is a third copy; <see cref="The_stub_s_rows_carry_exactly_the_contract_s_fields"/>
/// keeps it honest, because a stub more permissive than the server hides a defect rather than catching one.
/// </para>
/// </summary>
public sealed class AdminCustomerShapeTests
{
    private const string CustomerModel = "src/frontend/projects/admin/src/app/core/customers/admin-customer.model.ts";
    private const string NewsletterModel = "src/frontend/projects/admin/src/app/core/newsletter/newsletter.model.ts";
    private const string Stub = "src/frontend/e2e/support/admin-customer-stub.mjs";

    private static readonly Regex Property = new(@"readonly\s+(?<name>\w+)\??:", RegexOptions.CultureInvariant);

    [Theory]
    [InlineData(typeof(AdminCustomerListPage), "AdminCustomerListPage")]
    [InlineData(typeof(AdminDistrictOption), "AdminDistrictOption")]
    [InlineData(typeof(AdminCountryOption), "AdminCountryOption")]
    [InlineData(typeof(AdminCustomerListRow), "AdminCustomerListRow")]
    [InlineData(typeof(AdminCustomerDetailDto), "AdminCustomerDetail")]
    [InlineData(typeof(AdminCustomerAddressDto), "AdminCustomerAddress")]
    [InlineData(typeof(AdminCustomerOrderPage), "AdminCustomerOrderPage")]
    [InlineData(typeof(AdminCustomerOrderRow), "AdminCustomerOrderRow")]
    public void A_customer_payload_has_exactly_the_properties_the_screen_reads(Type dto, string tsInterface) =>
        Assert.Equal(Names(dto), PropertiesOf(CustomerModel, tsInterface));

    [Theory]
    [InlineData(typeof(NewsletterSubscriberPage), "NewsletterSubscriberPage")]
    [InlineData(typeof(NewsletterSubscriberRow), "NewsletterSubscriberRow")]
    public void A_newsletter_payload_has_exactly_the_properties_the_screen_reads(Type dto, string tsInterface) =>
        Assert.Equal(Names(dto), PropertiesOf(NewsletterModel, tsInterface));

    [Fact]
    public void The_client_declares_the_statuses_the_enum_has()
    {
        var text = ReadRepositoryFile(NewsletterModel);
        var start = text.IndexOf("export type NewsletterSubscriberStatus", StringComparison.Ordinal);
        Assert.True(start >= 0);

        var declared = Regex.Matches(text[start..text.IndexOf(';', start)], "'(\\w+)'")
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal);

        Assert.Equal(Enum.GetNames<NewsletterSubscriberStatus>().Order(StringComparer.Ordinal), declared);
    }

    /// <summary>
    /// The list row carries no IP address, and the contract says why: it is proof of consent, purged at 90
    /// days, and nobody running a mailing list needs it. The absence is asserted because adding it is the
    /// natural "improvement".
    /// </summary>
    [Fact]
    public void The_subscriber_row_carries_no_ip_address() =>
        Assert.DoesNotContain(Names(typeof(NewsletterSubscriberRow)), name => name.Contains("ip", StringComparison.OrdinalIgnoreCase));

    /// <summary>The stub's list row and subscriber row builders name exactly the contract's fields.</summary>
    [Fact]
    public void The_stub_s_rows_carry_exactly_the_contract_s_fields()
    {
        var stub = ReadRepositoryFile(Stub);

        Assert.Equal(Names(typeof(AdminCustomerListRow)), ObjectKeys(Block(stub, "function listRow(row)")));
        Assert.Equal(
            Names(typeof(NewsletterSubscriberRow)),
            Regex.Match(stub, @"return \{ (?<keys>subscriberId[^}]*) \};").Groups["keys"].Value
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Order(StringComparer.Ordinal)
                .ToList());
    }

    private static List<string> Names(Type dto) =>
        dto.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => char.ToLowerInvariant(property.Name[0]) + property.Name[1..])
            .Order(StringComparer.Ordinal)
            .ToList();

    private static List<string> PropertiesOf(string file, string interfaceName) =>
        Property.Matches(Block(ReadRepositoryFile(file), $"interface {interfaceName} "))
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static List<string> ObjectKeys(string body) =>
        Regex.Matches(body, @"^\s{4}(\w+)[:,]", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>The braces-balanced body after <paramref name="marker"/>, matched exactly (a trailing space ends the name).</summary>
    private static string Block(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find `{marker}`.");

        var open = text.IndexOf('{', start);
        var depth = 0;

        for (var index = open; index < text.Length; index++)
        {
            depth += text[index] switch { '{' => 1, '}' => -1, _ => 0 };

            if (depth == 0)
            {
                return text[(open + 1)..index];
            }
        }

        throw new InvalidOperationException($"`{marker}` has no closing brace.");
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        throw new FileNotFoundException(relativePath);
    }
}
