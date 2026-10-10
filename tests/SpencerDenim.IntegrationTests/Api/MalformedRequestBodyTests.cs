using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api;

/// <summary>
/// Issue <b>#47</b>: <c>POST /api/admin/auth/login</c> with a body of <c>{bad</c> — or <c>{"email":123}</c>, a
/// field of the wrong JSON type — answered <b>500</b>. Minimal APIs threw on a bad body in Development and the
/// exception handler turned that into a 500, while every other environment short-circuited to a bodyless 400.
/// <para>
/// These run in three environments because the environment was the defect: the same request must answer the
/// same <b>400 problem document with a <c>code</c></b> in Development, in the test host and in Production. The
/// routes are a mix of the ones the issue names and public ones that bind a body the same way, so the fix is
/// shown to be the pipeline's rather than one endpoint's. Binding fails before any handler runs, so no SQL
/// Server is needed.
/// </para>
/// </summary>
public sealed class MalformedRequestBodyTests
{
    public static TheoryData<string, string> Routes => new()
    {
        { "Testing", "/api/admin/auth/login" },
        { "Development", "/api/admin/auth/login" },
        { "Production", "/api/admin/auth/login" },
        { "Development", "/api/admin/auth/login/two-factor" },
        { "Production", "/api/admin/auth/login/two-factor" },
        { "Development", "/api/content/newsletter" },
        { "Production", "/api/content/newsletter" },
        { "Development", "/api/cart/items" },
        { "Production", "/api/cart/items" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Broken_json_is_a_400_problem_not_a_500(string environment, string route)
    {
        await using var factory = new ApiFactory(ApiFactory.UnreachableConnectionString, environment);
        using var client = factory.CreateClient();

        foreach (var payload in new[] { "{bad", "{\"email\":", "\"just a string\"" })
        {
            var (status, body) = await PostRawAsync(client, route, payload);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("malformed_json", body.GetProperty("code").GetString());
            Assert.Equal(400, body.GetProperty("status").GetInt32());
        }
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task A_field_of_the_wrong_json_type_is_a_400_problem_naming_the_field(string environment)
    {
        await using var factory = new ApiFactory(ApiFactory.UnreachableConnectionString, environment);
        using var client = factory.CreateClient();

        var (status, body) = await PostRawAsync(client, "/api/admin/auth/login", "{\"email\":123,\"password\":\"x\"}");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("malformed_json", body.GetProperty("code").GetString());
        Assert.Contains("$.email", body.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The answer describes the input, never the server: no stack trace, no exception type, and in Development
    /// none of the developer exception page's output either.
    /// </summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task The_problem_carries_no_stack_trace_or_exception_detail(string environment)
    {
        await using var factory = new ApiFactory(ApiFactory.UnreachableConnectionString, environment);
        using var client = factory.CreateClient();

        using var content = new StringContent("{bad", Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.PostAsync(
            "/api/admin/auth/login", content, TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith("application/problem+json", response.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("BadHttpRequestException", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("SpencerDenim.", raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// What issue #47 is really about (owner's diagnosis, 2026-10-02): a <b>well-formed</b> object with a property
    /// omitted. <c>System.Text.Json</c> ignores nullable annotations, so the omitted property bound null and a
    /// <c>ThrowIfNull</c> inside the authenticator answered 500. Every shape that leaves the email or the password
    /// out must be a 400 that names the field, in every environment, and never reach the authenticator.
    /// </summary>
    [Theory]
    [InlineData("Testing", "{}", "email,password")]
    [InlineData("Development", "{}", "email,password")]
    [InlineData("Production", "{}", "email,password")]
    [InlineData("Production", "{\"email\":\"a@b.c\"}", "password")]
    [InlineData("Production", "{\"password\":\"x\"}", "email")]
    [InlineData("Production", "{\"email\":null,\"password\":null}", "email,password")]
    [InlineData("Development", "{\"email\":\"a@b.c\"}", "password")]
    [InlineData("Development", "{\"password\":\"x\"}", "email")]
    public async Task A_sign_in_with_a_field_left_out_is_a_400_naming_the_field(
        string environment, string payload, string expectedFields)
    {
        await using var factory = new ApiFactory(ApiFactory.UnreachableConnectionString, environment);
        using var client = factory.CreateClient();

        var (status, body) = await PostRawAsync(client, "/api/admin/auth/login", payload);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("missing_field", body.GetProperty("code").GetString());
        var errors = body.GetProperty("errors");
        foreach (var field in expectedFields.Split(','))
        {
            Assert.True(errors.TryGetProperty(field, out _), $"expected an error for '{field}'");
        }

        Assert.Equal(expectedFields.Split(',').Length, errors.EnumerateObject().Count());
    }

    /// <summary>
    /// The detail is fixed wording plus the JSON path the reader stopped at. The framework's and
    /// <c>System.Text.Json</c>'s own messages name internal CLR types, which tells a stranger how the server is built.
    /// </summary>
    [Theory]
    [InlineData("/api/admin/auth/login", "[]")]
    [InlineData("/api/admin/auth/login", "\"x\"")]
    [InlineData("/api/admin/auth/login", "{\"email\":5}")]
    [InlineData("/api/cart/items", "{\"variantId\":\"abc\"}")]
    [InlineData("/api/content/newsletter", "")]
    [InlineData("/api/content/newsletter", "null")]
    public async Task The_detail_names_no_internal_type_or_framework_wording(string route, string payload)
    {
        await using var factory = new ApiFactory(ApiFactory.UnreachableConnectionString, "Production");
        using var client = factory.CreateClient();

        var (status, body) = await PostRawAsync(client, route, payload);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var text = body.ToString();
        Assert.DoesNotContain("SpencerDenim.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Implicit body", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Did you mean", text, StringComparison.Ordinal);
        Assert.DoesNotContain("converted to", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Real browsers send <c>Accept: */*</c>, but a probe or a proxy may not. With no problem writer matching the
    /// header the answer used to be a bodyless 400 and an error-level "unhandled exception" log line.
    /// </summary>
    [Theory]
    [InlineData("text/html")]
    [InlineData("application/xml")]
    public async Task A_caller_who_does_not_accept_json_still_gets_the_problem_document(string accept)
    {
        await using var factory = new ApiFactory(ApiFactory.UnreachableConnectionString, "Production");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/cart/items")
        {
            Content = new StringContent("{bad", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", accept);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("malformed_json", raw, StringComparison.Ordinal);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostRawAsync(
        HttpClient client, string route, string payload)
    {
        using var content = new StringContent(payload, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await client.PostAsync(route, content, TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return (response.StatusCode, JsonDocument.Parse(json).RootElement.Clone());
    }
}
