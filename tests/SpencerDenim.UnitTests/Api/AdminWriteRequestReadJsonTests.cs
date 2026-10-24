using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Http;

namespace SpencerDenim.UnitTests.Api;

/// <summary>
/// Issue <b>#147</b>: <see cref="AdminWriteRequest.ReadJsonAsync{TBody}"/> caught only <c>JsonException</c> and
/// <c>BadHttpRequestException</c>, so a body the framework could not even decode — a charset it does not know, or
/// bytes that are not valid in the charset the caller declared — escaped as another exception type and answered
/// <b>500</b> on every admin write that reads its body this way. The caller did nothing the server should
/// apologise for, so the answer is a 4xx problem with a machine-readable <c>code</c>, like every other unreadable body.
/// <para>
/// These call the reader directly. The bug is in which exceptions it recognises, which does not depend on routing,
/// authentication or a database, so the test needs none of them and cannot be made flaky by them.
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
    /// The refusal must not be wider than the fault: every charset the framework does read has to keep reading,
    /// including a different case and no space after the semicolon.
    /// </summary>
    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/json; charset=UTF-8")]
    [InlineData("application/json;charset=utf-8")]
    [InlineData("application/problem+json; charset=utf-8")]
    public async Task Every_charset_the_framework_reads_still_reads(string contentType)
    {
        var (body, problem) = await ReadAsync(contentType, ValidUtf8);

        Assert.Null(problem);
        Assert.Equal("linen", body!.Name);
    }

    /// <summary>
    /// Bytes that are not valid in the declared charset. The issue also names these as a 500; measured on the
    /// reader they already answer 400 <c>malformed_json</c>, because <c>System.Text.Json</c> reports them as a
    /// <c>JsonException</c>. They are kept here so that stays true.
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
