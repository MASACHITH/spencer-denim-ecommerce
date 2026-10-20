using System.Text;
using System.Text.Json;
using SpencerDenim.Application.Privacy;

namespace SpencerDenim.UnitTests.Privacy;

/// <summary>
/// Issue <b>#201</b>: what a log may print for a request path. The table of spellings is
/// <c>tests/Shared/log-path-vectors.json</c>, which the storefront's <c>log-path.spec.ts</c> runs through its
/// TypeScript port and which was checked against the gateway's <c>$sd_log_path</c> map, so the three
/// implementations of one rule cannot drift apart without a test going red.
/// </summary>
public sealed class RequestPathRedactionTests
{
    private const string OrderToken = "7d1e5c0a-4b3f-4a8e-9c2d-1f6b8a3e5d90";

    public static TheoryData<string, string, string> Vectors
    {
        get
        {
            var data = new TheoryData<string, string, string>();

            foreach (var row in ReadVectors())
            {
                data.Add(row.Name, row.Path, row.Expected);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void A_path_is_logged_as_the_table_says(string name, string path, string expected) =>
        Assert.True(
            expected == RequestPathRedaction.ForLog(path),
            $"{name}: {path.ReplaceLineEndings("\\n")} should be logged as {expected} but was logged as {RequestPathRedaction.ForLog(path)}");

    [Fact]
    public void The_table_is_not_a_stub()
    {
        var rows = ReadVectors();

        Assert.True(rows.Count >= 100, $"Only {rows.Count} rows.");

        // It holds the cases that matter, so a trimmed table is noticed.
        foreach (var path in new[]
        {
            "/order/" + OrderToken,
            "/api/orders/" + OrderToken + "/payment-slip",
            "/api/payments/paypal/return/" + OrderToken,
            "/api/admin/orders/bank-transfers/" + OrderToken + "/approve",
            "/api/admin/orders/" + OrderToken,
            "/account/reset-password?token=CfDJ8AbcDef&email=jane%40example.com",
        })
        {
            Assert.Contains(rows, row => row.Path == path);
        }
    }

    [Fact]
    public void Nothing_the_table_redacts_leaks_the_token_it_carries()
    {
        foreach (var row in ReadVectors().Where(row => row.Path.Contains(OrderToken, StringComparison.OrdinalIgnoreCase)))
        {
            var logged = RequestPathRedaction.ForLog(row.Path);

            Assert.DoesNotContain("7d1e5c0a", logged, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("1f6b8a3e5d90", logged, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Redacting_twice_changes_nothing()
    {
        foreach (var row in ReadVectors())
        {
            Assert.Equal(row.Expected, RequestPathRedaction.ForLog(row.Expected));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_path_is_the_root(string? path) => Assert.Equal("/", RequestPathRedaction.ForLog(path));

    /// <summary>
    /// The property that matters, over spellings nobody wrote down: whatever comes in, every segment that comes
    /// out is a safe slug, the redaction mark, or a fixed word of a credential route (the PayPal action keeps the
    /// case it came in), and no control character survives. A fixed seed, so a failure reproduces.
    /// </summary>
    [Fact]
    public void Whatever_the_input_every_segment_of_the_output_is_safe()
    {
        var random = new Random(201);
        var alphabet = "abcdefghijklmnopqrstuvwxyz0123456789-ABCDEF/;%._~@: \n\r\t?#é".ToCharArray();
        string[] chunks = [OrderToken, OrderToken.ToUpperInvariant(), "api", "orders", "order", "admin", "bank-transfers", "payments", "paypal", "start", "RETURN", "cancel", "12345678", "ab-cd", "women", "dresses"];

        for (var attempt = 0; attempt < 6000; attempt++)
        {
            var builder = new StringBuilder();
            var parts = random.Next(0, 9);

            for (var part = 0; part < parts; part++)
            {
                builder.Append(random.Next(0, 6) == 0 ? "//" : "/");
                builder.Append(random.Next(0, 3) == 0
                    ? chunks[random.Next(chunks.Length)]
                    : new string(Enumerable.Range(0, random.Next(0, 25)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray()));
            }

            var input = builder.ToString();
            var logged = RequestPathRedaction.ForLog(input);

            Assert.DoesNotContain(logged, character => char.IsControl(character));
            Assert.True(logged.StartsWith('/'), $"{input} -> {logged}");

            foreach (var segment in logged.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var allowed = segment == RequestPathRedaction.Redacted
                    || RequestPathRedaction.IsSafeLogSegment(segment)
                    || segment.Equals("start", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("return", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("cancel", StringComparison.OrdinalIgnoreCase);

                Assert.True(allowed, $"{input.ReplaceLineEndings("\\n")} -> {logged}: '{segment}' is none of safe, '*' or a route word");
            }

            // Whatever a path held after the first segment that is not safe, it is not in the output.
            Assert.DoesNotContain(OrderToken, logged, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("women", true)]
    [InlineData("linen-shirt-12", true)]
    [InlineData("123456", true)]
    [InlineData("1234567", false)]
    [InlineData("12-34-567", false)]
    [InlineData("abcdefghijklmno", true)]
    [InlineData("abcdefghijklmnop", false)]
    [InlineData("ab12cd34ef56gh78", false)]
    [InlineData("Women", false)]
    [InlineData("", false)]
    [InlineData("women\n", false)]
    [InlineData("wo men", false)]
    [InlineData("7d1e5c0a-4b3f4a8e-9c2d-1f6b8a3e5d90", false)]
    [InlineData("7d1e5c0a-4b3f-4a8e-9c2d-1f6b8a3e5d90", false)]
    public void A_segment_is_safe_when_it_is_a_short_slug_with_no_token_shape(string segment, bool expected) =>
        Assert.Equal(expected, RequestPathRedaction.IsSafeLogSegment(segment));

    /// <summary>
    /// The named routes are the ones in the gateway's map, in the same order; the parity test compares the
    /// patterns with the file, this fixes what they are here.
    /// </summary>
    [Fact]
    public void The_credential_routes_are_the_five_the_gateway_names()
    {
        Assert.Equal(
            [
                "/order/*",
                "/api/orders/*",
                "/api/payments/paypal/${sd_a}/*",
                "/api/admin/orders/bank-transfers/*",
                "/api/admin/orders/*",
            ],
            RequestPathRedaction.CredentialRoutes.Select(route => route.Redacted));
    }

    private static List<VectorRow> ReadVectors()
    {
        var path = Path.Combine(FindRepositoryRoot(), "tests", "Shared", "log-path-vectors.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Select(row => new VectorRow(
                row.GetProperty("name").GetString()!,
                row.GetProperty("path").GetString()!,
                row.GetProperty("expected").GetString()!))
            .ToList();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SpencerDenim.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate SpencerDenim.sln above the test output directory.");
    }

    private sealed record VectorRow(string Name, string Path, string Expected);
}
