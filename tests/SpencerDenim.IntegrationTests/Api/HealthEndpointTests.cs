using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api;

public sealed class HealthEndpointTests(SqlServerContainerFixture sqlServer)
{
    [Fact]
    public async Task Readiness_is_healthy_against_a_real_sql_server()
    {
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        var check = Assert.Single(body.GetProperty("checks").EnumerateArray());
        Assert.Equal("sqlserver", check.GetProperty("name").GetString());
        Assert.Equal("Healthy", check.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readiness_is_unhealthy_when_sql_server_is_unreachable()
    {
        // Port 1 on loopback refuses connections immediately; retries are bounded by the 5 s check timeout.
        const string unreachable = "Server=127.0.0.1,1;Database=SpencerDenim;User Id=sa;Password=Unused-Passw0rd;Connect Timeout=2;TrustServerCertificate=True";
        await using var factory = new ApiFactory(unreachable);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Liveness_does_not_depend_on_sql_server()
    {
        const string unreachable = "Server=127.0.0.1,1;Database=SpencerDenim;User Id=sa;Password=Unused-Passw0rd;Connect Timeout=2;TrustServerCertificate=True";
        await using var factory = new ApiFactory(unreachable);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }
}
