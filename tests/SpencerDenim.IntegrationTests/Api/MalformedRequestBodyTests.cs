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
