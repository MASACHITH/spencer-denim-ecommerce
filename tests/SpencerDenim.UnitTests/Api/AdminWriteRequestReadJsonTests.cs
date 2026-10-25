using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Http;

namespace SpencerDenim.UnitTests.Api;

/// <summary>
/// Issue <b>#147</b>: <see cref="AdminWriteRequest.ReadJsonAsync{TBody}"/> answered <b>500</b> on every admin write that
/// reads its body this way, for three kinds of body the caller sent and the server could not read: a <c>charset</c>
/// the runtime has no encoding for (UTF-7 included), text that cannot be decoded in a <see cref="JsonObject"/> (parsed
/// lazily, so the failure surfaced later in the handler), and a property named twice in one. The caller did nothing the
/// server should apologise for, so each is a 4xx problem with a machine-readable <c>code</c>, like every other
/// unreadable body.
/// <para>
/// These call the reader directly. What it recognises does not depend on routing, authentication or a database, so the
/// tests need none of them and cannot be made flaky by them; the integration tests then send the same bodies through
/// real routes.
/// </para>
/// </summary>
public sealed class AdminWriteRequestReadJsonTests
{
    private sealed record Payload(string? Name);

    private static readonly byte[] ValidUtf8 = Encoding.UTF8.GetBytes("{\"name\":\"linen\"}");

    private static async Task<(Payload? Body, IResult? Problem)> ReadAsync(string contentType, byte[] bytes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.ContentType = contentType;
        httpContext.Request.Body = new MemoryStream(bytes);

        return await AdminWriteRequest.ReadJsonAsync<Payload>(
            httpContext.Request, TestContext.Current.CancellationToken);
    }

    private static async Task<(JsonObject? Body, IResult? Problem)> ReadObjectAsync(string contentType, byte[] bytes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.ContentType = contentType;
        httpContext.Request.Body = new MemoryStream(bytes);

        return await AdminWriteRequest.ReadJsonAsync<JsonObject>(
            httpContext.Request, TestContext.Current.CancellationToken);
    }

    private static ProblemHttpResult AssertProblem(IResult? problem, int expectedStatus, params string[] allowedCodes)
    {
        var result = Assert.IsType<ProblemHttpResult>(problem);
        Assert.Equal(expectedStatus, result.StatusCode);
        Assert.NotNull(result.ProblemDetails.Extensions["code"]);
        Assert.Contains((string)result.ProblemDetails.Extensions["code"]!, allowedCodes);
        return result;
    }

    [Fact]
    public async Task A_valid_utf8_body_still_reads()
    {
        var (body, problem) = await ReadAsync("application/json", ValidUtf8);

        Assert.Null(problem);
        Assert.Equal("linen", body!.Name);
    }

    [Fact]
    public async Task A_valid_utf8_body_with_an_explicit_utf8_charset_still_reads()
    {
        var (body, problem) = await ReadAsync("application/json; charset=utf-8", ValidUtf8);

        Assert.Null(problem);
        Assert.Equal("linen", body!.Name);
    }

    /// <summary>
    /// The charset the caller named is a name the runtime does not know. The framework reports that with an
    /// <c>InvalidOperationException</c>, which was not caught. It is a 415 like the other media-type refusal in
    /// this method, and it is decided from the header alone, so the body is never read.
    /// </summary>
    [Theory]
    [InlineData("application/json; charset=not-a-real-charset")]
    [InlineData("application/json; charset=\"x-nonexistent\"")]
    [InlineData("application/json; charset=utf-99")]
    // UTF-7 is a name the runtime knows and refuses: Encoding.GetEncoding throws NotSupportedException ("Support for
    // UTF-7 is disabled"), a different exception type from an unknown name, and MediaTypeHeaderValue.Encoding lets it
    // out rather than answering null.
    [InlineData("application/json; charset=utf-7")]
    [InlineData("application/json; charset=UTF-7")]
    [InlineData("application/json; charset=unicode-1-1-utf-7")]
    [InlineData("application/json; charset=csunicode11utf7")]
    // RFC 9110 allows a quoted parameter value, but the framework does not unquote it: "utf-8" with its quotes is
    // looked up as an encoding name, so it answered 500 before this and cannot be read now. Nothing that sends
    // JSON from a browser or from Angular quotes it, so it is refused cleanly rather than handled.
    [InlineData("application/json; charset=\"utf-8\"")]
    public async Task A_charset_the_runtime_does_not_know_is_a_415_problem(string contentType)
    {
        var (body, problem) = await ReadAsync(contentType, ValidUtf8);

        Assert.Null(body);
        AssertProblem(problem, StatusCodes.Status415UnsupportedMediaType, "unsupported_charset");
    }

    /// <summary>
    /// The refusal must not be wider than the fault: every charset the framework does read has to keep reading. The
    /// body is encoded <i>in</i> the charset it declares, so this proves the request is read and not just let past
    /// the header check (a refusal of <c>iso-8859-1</c> or <c>utf-16BE</c> would otherwise go unnoticed).
    /// </summary>
    [Theory]
    [InlineData("application/json", "utf-8")]
    [InlineData("application/json; charset=utf-8", "utf-8")]
    [InlineData("application/json; charset=UTF-8", "utf-8")]
    [InlineData("application/json;charset=utf-8", "utf-8")]
    [InlineData("application/problem+json; charset=utf-8", "utf-8")]
    [InlineData("application/json; charset=utf-16", "utf-16")]
    [InlineData("application/json; charset=utf-16BE", "utf-16BE")]
    [InlineData("application/json; charset=utf-32", "utf-32")]
    [InlineData("application/json; charset=utf-32BE", "utf-32BE")]
    [InlineData("application/json; charset=iso-8859-1", "iso-8859-1")]
    [InlineData("application/json; charset=us-ascii", "us-ascii")]
    [InlineData("application/json; charset=latin1", "iso-8859-1")]
    public async Task Every_charset_the_framework_reads_still_reads(string contentType, string encodingName)
    {
        var bytes = Encoding.GetEncoding(encodingName).GetBytes("{\"name\":\"linen\"}");

        var (body, problem) = await ReadAsync(contentType, bytes);

        Assert.Null(problem);
        Assert.Equal("linen", body!.Name);
    }

    /// <summary>
    /// Bytes that are not valid in the declared charset, in a body bound to a <b>record</b> whose property they sit in.
    /// Those already answer 400 <c>malformed_json</c>, because the serializer decodes each string it binds while it
    /// builds the record and reports a failure as a <c>JsonException</c>; they are kept here so that stays true. (A
    /// string in a property the record does not bind is skipped without being decoded, and is not a failure.) A body
    /// bound to a <see cref="JsonObject"/> does not fail at that point: see the tests below.
    /// </summary>
    [Theory]
    [InlineData("application/json", new byte[] { 0x7B, 0x22, 0x6E, 0x61, 0x6D, 0x65, 0x22, 0x3A, 0x22, 0xFF, 0xFE, 0x22, 0x7D })]
    [InlineData("application/json; charset=utf-8", new byte[] { 0x7B, 0x22, 0x6E, 0x61, 0x6D, 0x65, 0x22, 0x3A, 0x22, 0xC3, 0x28, 0x22, 0x7D })]
    [InlineData("application/json; charset=utf-8", new byte[] { 0xFF, 0xFE, 0xFD })]
    [InlineData("application/json; charset=utf-16", new byte[] { 0x7B, 0x22, 0x6E, 0x61, 0x6D, 0x65, 0x22, 0x3A, 0x22, 0x6C, 0x22, 0x7D })]
    [InlineData("application/json; charset=utf-16", new byte[] { 0x7B })]
    [InlineData("application/json; charset=utf-32", new byte[] { 0x7B, 0x00, 0x00 })]
    public async Task Bytes_that_are_not_valid_in_the_declared_charset_are_a_400_problem(string contentType, byte[] bytes)
    {
        var (_, problem) = await ReadAsync(contentType, bytes);

        AssertProblem(problem, StatusCodes.Status400BadRequest, "malformed_json");
    }

    /// <summary>
    /// Issue #147's second trigger, on the call sites that matter: most callers (30 of the 44 call sites when last
    /// counted, by <c>grep -rn "ReadJsonAsync&lt;JsonObject&gt;" src</c>) read a
    /// <see cref="JsonObject"/>, which is parsed <b>lazily</b>. Text that cannot be decoded does not fail at the read;
    /// it failed later, with an <c>InvalidOperationException</c>, the first time a handler asked for a string (a 500),
    /// where a record body fails inside the deserializer and is already a 400. Two shapes: bytes that are not UTF-8,
    /// and an escape that spells half a surrogate pair, which is plain ASCII on the wire and only invalid once it is
    /// turned into a string. Keys count as much as values, and so does nesting.
    /// </summary>
    [Theory]
    [MemberData(nameof(UndecodableObjectBodies))]
    public async Task A_json_object_body_with_text_that_cannot_be_decoded_is_a_400_problem(string label, byte[] bytes)
    {
        var (body, problem) = await ReadObjectAsync("application/json", bytes);

        Assert.True(problem is not null, $"'{label}' was handed to the handler as a body; it would fail there as a 500.");
        Assert.Null(body);
        AssertProblem(problem, StatusCodes.Status400BadRequest, "malformed_json");
    }

    public static TheoryData<string, byte[]> UndecodableObjectBodies() => new()
    {
        { "invalid UTF-8 in a value", Join("{\"code\":\"", [0xFF, 0xFE], "\"}") },
        { "invalid UTF-8 in a key", Join("{\"", [0xFF], "\":\"a\"}") },
        { "a truncated UTF-8 sequence in a value", Join("{\"code\":\"", [0xC3], "\"}") },
        { "an escaped lone high surrogate in a value", Ascii("{\"code\":\"\\ud800\"}") },
        { "an escaped lone high surrogate in a key", Ascii("{\"\\ud800\":\"a\"}") },
        { "an escaped lone low surrogate in a value", Ascii("{\"code\":\"\\udc00\"}") },
        { "invalid UTF-8 inside an array inside an object", Join("{\"a\":[{\"b\":\"", [0xFF], "\"}]}") },
        { "an escaped lone surrogate two levels down", Ascii("{\"a\":{\"b\":[\"\\ud800\"]}}") },
    };

    /// <summary>
    /// The other way a lazily parsed body fails later: a property named twice. The first time anything enumerates the
    /// object it builds its dictionary, and that throws <see cref="ArgumentException"/> ("an item with the same key
    /// has already been added"), which was a 500 on every <see cref="JsonObject"/> route (found by the round-2 tester).
    /// Two spellings of one name are still one name: they differ only in letter case (the serializer matches property
    /// names case-insensitively, as the handlers do when they look a property up), or only in a JSON escape of one
    /// letter, which is the third case below.
    /// </summary>
    [Theory]
    [InlineData("{\"Code\":\"a\",\"code\":\"b\"}")]
    [InlineData("{\"CODE\":\"a\",\"code\":\"b\"}")]
    [InlineData("{\"code\":\"a\",\"code\":\"b\"}")]
    [InlineData("{\"zzz\":1,\"zzz\":2}")]
    [InlineData("{\"code\":\"a\",\"c\\u006fde\":\"b\"}")]
    [InlineData("{\"a\":{\"b\":1,\"b\":2}}")]
    [InlineData("{\"a\":[{\"b\":1,\"b\":2}]}")]
    public async Task A_json_object_body_that_repeats_a_property_name_is_a_400_problem(string json)
    {
        var (body, problem) = await ReadObjectAsync("application/json", Encoding.UTF8.GetBytes(json));

        Assert.True(problem is not null, $"'{json}' was handed to the handler as a body; it would fail there as a 500.");
        Assert.Null(body);
        var result = AssertProblem(problem, StatusCodes.Status400BadRequest, "malformed_json");
        Assert.Contains("repeats a property name", result.ProblemDetails.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The check must not be wider than the fault either: a body that is fine still reaches the handler, with the
    /// value it sent. An escaped surrogate <i>pair</i> is one character and is fine, as are numbers, nulls,
    /// booleans, empty strings and an object with no properties.
    /// </summary>
    [Fact]
    public async Task A_valid_json_object_body_still_reaches_the_handler()
    {
        const string Json =
            "{\"name\":\"linen\",\"n\":5,\"on\":true,\"none\":null,\"empty\":\"\","
            + "\"emoji\":\"\ud83d\ude00\",\"escaped\":\"\\ud83d\\ude00\",\"tags\":[\"a\",\"b\"],"
            + "\"nested\":{\"k\":\"v\",\"deeper\":[{\"x\":\"y\"}]}}";

        var (body, problem) = await ReadObjectAsync("application/json", Encoding.UTF8.GetBytes(Json));

        Assert.Null(problem);
        Assert.Equal("linen", body!["name"]!.GetValue<string>());
        Assert.Equal(5, body["n"]!.GetValue<int>());
        Assert.Equal("\ud83d\ude00", body["emoji"]!.GetValue<string>());
        Assert.Equal("\ud83d\ude00", body["escaped"]!.GetValue<string>());
        Assert.Equal("y", body["nested"]!["deeper"]![0]!["x"]!.GetValue<string>());

        var (empty, emptyProblem) = await ReadObjectAsync("application/json", Encoding.UTF8.GetBytes("{}"));
        Assert.Null(emptyProblem);
        Assert.Empty(empty!);
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Join(string before, byte[] bad, string after) =>
        [.. Encoding.ASCII.GetBytes(before), .. bad, .. Encoding.ASCII.GetBytes(after)];

    /// <summary>
    /// A UTF-16 body that is genuinely UTF-16 is one the framework transcodes and reads, so the fix is not
    /// "refuse everything that is not UTF-8".
    /// </summary>
    [Fact]
    public async Task A_genuine_utf16_body_still_reads()
    {
        var utf16 = Encoding.Unicode.GetBytes("{\"name\":\"linen\"}");

        var (body, problem) = await ReadAsync("application/json; charset=utf-16", utf16);

        Assert.Null(problem);
        Assert.Equal("linen", body!.Name);
    }
}
