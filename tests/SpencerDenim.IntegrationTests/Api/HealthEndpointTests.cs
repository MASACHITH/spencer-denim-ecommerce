using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api;

public sealed class HealthEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    [Fact]
    public async Task Readiness_is_healthy_against_a_real_sql_server()
    {
        sqlServer.SkipIfUnavailable();

        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());

        // **Two checks, and the second one is the point.** `sqlserver` is `CanConnectAsync()` and knows
        // only whether the server answers; `referencedata` is whether this host actually established doc
        // 08 §9's reference data. A connectable database with no migrations applied passes the first and
        // fails the second, which is the state that used to report `200 Healthy` while every checkout
        // returned 500.
        var checks = body.GetProperty("checks").EnumerateArray()
            .ToDictionary(
                check => check.GetProperty("name").GetString()!,
                check => check.GetProperty("status").GetString(),
                StringComparer.Ordinal);

        Assert.Equal("Healthy", checks["sqlserver"]);
        Assert.Equal("Healthy", checks["referencedata"]);
        Assert.Equal(2, checks.Count);
    }

    [Fact]
    public async Task Readiness_is_unhealthy_when_sql_server_is_unreachable()
    {
        // Retries are bounded by the 5 s health check timeout.
        await using var factory = new ApiFactory(ApiFactory.UnreachableConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Liveness_does_not_depend_on_sql_server()
    {
        await using var factory = new ApiFactory(ApiFactory.UnreachableConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }
}
