using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SpencerDenim.Application.Content.Contracts;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// A thin wrapper over the storefront content endpoints (HOME-01..10, GLB-03), so each test reads
/// as a request and an assertion.
/// </summary>
public sealed class ContentApiClient(HttpClient client)
{
    public async Task<HomePageDto> GetHomeAsync(CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(new Uri("/api/content/home", UriKind.Relative), cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"GET /api/content/home returned {(int)response.StatusCode}: {payload}");

        var page = JsonSerializer.Deserialize<HomePageDto>(payload, CatalogApiClient.JsonOptions);
        Assert.NotNull(page);
        return page;
    }

    public async Task<IReadOnlyList<AnnouncementDto>> GetAnnouncementsAsync(CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            new Uri("/api/content/announcements", UriKind.Relative),
            cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"GET /api/content/announcements returned {(int)response.StatusCode}: {payload}");

        var messages = JsonSerializer.Deserialize<List<AnnouncementDto>>(payload, CatalogApiClient.JsonOptions);
        Assert.NotNull(messages);
        return messages;
    }

    /// <summary>Posts a newsletter signup and returns the raw status and body, for the 400 case.</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> SubscribeAsync(
        object body,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/content/newsletter", UriKind.Relative),
            body,
            cancellationToken);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(
            CatalogApiClient.JsonOptions,
            cancellationToken);

        return (response.StatusCode, payload);
    }

    /// <summary>Sends a HEAD request, for the monitoring case the catalogue endpoints also answer.</summary>
    public async Task<HttpResponseMessage> HeadAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, new Uri(path, UriKind.Relative));
        return await client.SendAsync(request, cancellationToken);
    }
}
