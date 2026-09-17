using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api;

public sealed class PingAndOpenApiTests(SqlServerContainerFixture sqlServer)
{
    [Fact]
    public async Task Ping_returns_ok_payload()
    {
        await using var factory = new ApiFactory(sqlServer.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/ping", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal("SpencerDenim.Api", body.GetProperty("service").GetString());
        Assert.True(body.TryGetProperty("serverTimeUtc", out _));
    }

    [Fact]
    public async Task Swagger_ui_and_openapi_document_are_served_in_development()
    {
        await using var factory = new ApiFactory(sqlServer.ConnectionString, environment: "Development");
        using var client = factory.CreateClient();

        using var ui = await client.GetAsync(new Uri("/api/swagger/index.html", UriKind.Relative), TestContext.Current.CancellationToken);
        using var document = await client.GetAsync(new Uri("/api/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        var json = await document.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        Assert.True(json.GetProperty("paths").TryGetProperty("/api/ping", out _));
    }

    [Fact]
    public async Task Swagger_ui_is_not_served_outside_development()
    {
        await using var factory = new ApiFactory(sqlServer.ConnectionString, environment: "Production");
        using var client = factory.CreateClient();

        using var ui = await client.GetAsync(new Uri("/api/swagger/index.html", UriKind.Relative), TestContext.Current.CancellationToken);
        using var document = await client.GetAsync(new Uri("/api/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, ui.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);
    }
}
