using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
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
/// <para>
/// <b>A request that is not a 400 explains itself.</b> The eight missing-field cases once answered 500 in a
/// full-assembly run beside a second suite, then passed 174 times in a row, and the only thing the failure said
/// was <c>Expected 400, Actual 500</c>. So every assertion here goes through <see cref="Probe"/>, which keeps
/// what the host threw and everything it logged at Error and puts both, with the response body, in the failure
/// message. The next intermittent failure names its own cause.
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
        await using var probe = new Probe(environment);

        foreach (var payload in new[] { "{bad", "{\"email\":", "\"just a string\"" })
        {
            var body = await probe.PostExpectingBadRequestAsync(route, payload);

            Assert.Equal("malformed_json", body.GetProperty("code").GetString());
            Assert.Equal(400, body.GetProperty("status").GetInt32());
        }
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task A_field_of_the_wrong_json_type_is_a_400_problem_naming_the_field(string environment)
    {
        await using var probe = new Probe(environment);

        var body = await probe.PostExpectingBadRequestAsync(
            "/api/admin/auth/login", "{\"email\":123,\"password\":\"x\"}");

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
        await using var probe = new Probe(environment);

        using var content = new StringContent("{bad", Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await probe.Client.PostAsync(
            "/api/admin/auth/login", content, TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        probe.ExpectBadRequest(response.StatusCode, raw);
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
        await using var probe = new Probe(environment);

        var body = await probe.PostExpectingBadRequestAsync("/api/admin/auth/login", payload);

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
        await using var probe = new Probe("Production");

        var body = await probe.PostExpectingBadRequestAsync(route, payload);

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
        await using var probe = new Probe("Production");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/cart/items")
        {
            Content = new StringContent("{bad", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", accept);

        using var response = await probe.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        probe.ExpectBadRequest(response.StatusCode, raw);
        Assert.Contains("malformed_json", raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// The API in memory against a database that refuses connections, plus the two things a failing request leaves
    /// behind: what the pipeline threw and what the host logged at Error.
    /// </summary>
    private sealed class Probe : IAsyncDisposable
    {
        private readonly ApiFactory _factory;

        public Probe(string environment)
        {
            Evidence = new Evidence();
            _factory = new ApiFactory(
                ApiFactory.UnreachableConnectionString,
                environment,
                configureServices: services =>
                {
                    // Added after the application's own registrations, so this handler runs after the
                    // bad-request handler and sees only what that one declined: the exceptions that become a 500.
                    // It always returns false, so it changes nothing about the answer.
                    services.AddSingleton<IExceptionHandler>(Evidence);

                    // Serilog replaces the logging providers (`AddSerilog` without `writeToProviders`) and reads
                    // its sinks from the container, so a sink is the only way to see the host's own log lines.
                    // An `ILoggerProvider` here would be accepted, never called, and look like a quiet host.
                    services.AddSingleton<ILogEventSink>(Evidence);
                });
            Client = _factory.CreateClient();
        }

        public HttpClient Client { get; }

        private Evidence Evidence { get; }

        public async Task<JsonElement> PostExpectingBadRequestAsync(string route, string payload)
        {
            using var content = new StringContent(payload, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await Client.PostAsync(route, content, TestContext.Current.CancellationToken);
            var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            ExpectBadRequest(response.StatusCode, raw);

            return JsonDocument.Parse(raw).RootElement.Clone();
        }

        public void ExpectBadRequest(HttpStatusCode status, string responseBody) =>
            Assert.True(
                status == HttpStatusCode.BadRequest,
                $"Expected 400 but the API answered {(int)status} {status}.{Environment.NewLine}"
                + $"Response body: {responseBody}{Environment.NewLine}"
                + $"What the host saw: {Evidence.Describe()}");

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _factory.DisposeAsync();
        }
    }

    /// <summary>Everything a request threw and everything the host logged at Error, in order.</summary>
    private sealed class Evidence : IExceptionHandler, ILogEventSink
    {
        private readonly ConcurrentQueue<string> _seen = new();

        public ValueTask<bool> TryHandleAsync(
            HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            _seen.Enqueue($"thrown in the request: {exception}");

            return ValueTask.FromResult(false);
        }

        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Level < LogEventLevel.Error)
            {
                return;
            }

            var source = logEvent.Properties.TryGetValue("SourceContext", out var context)
                ? context.ToString().Trim('"')
                : "unknown source";
            var exception = logEvent.Exception is null ? string.Empty : Environment.NewLine + logEvent.Exception;

            _seen.Enqueue(
                $"logged at {logEvent.Level} by {source}: {logEvent.RenderMessage(CultureInfo.InvariantCulture)}{exception}");
        }

        public string Describe() =>
            _seen.IsEmpty
                ? "nothing was thrown in the request and nothing was logged at Error"
                : Environment.NewLine + string.Join(Environment.NewLine + "---" + Environment.NewLine, _seen);
    }
}
