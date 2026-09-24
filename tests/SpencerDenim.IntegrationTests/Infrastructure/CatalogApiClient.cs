using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// A thin wrapper over the catalogue endpoints, so each test reads as a request and an assertion
/// rather than as HTTP plumbing.
/// </summary>
public sealed class CatalogApiClient(HttpClient client)
{
    /// <summary>The API writes enums as names, so the tests must read them the same way.</summary>
    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<IReadOnlyList<DepartmentDto>> GetDepartmentsAsync(CancellationToken cancellationToken)
    {
        var departments = await GetAsync<List<DepartmentDto>>("/api/catalog/departments", cancellationToken);
        return departments;
    }

    public Task<PagedResult<ProductCardDto>> ListAsync(string query, CancellationToken cancellationToken) =>
        GetAsync<PagedResult<ProductCardDto>>("/api/catalog/products" + query, cancellationToken);

    public Task<ProductDetailDto> GetProductAsync(string slug, CancellationToken cancellationToken) =>
        GetAsync<ProductDetailDto>($"/api/catalog/products/{slug}", cancellationToken);

    public Task<SearchResponse> SearchAsync(string query, CancellationToken cancellationToken) =>
        GetAsync<SearchResponse>("/api/catalog/search" + query, cancellationToken);

    /// <summary>Sends a HEAD request and returns the status and the headers, for the monitoring case.</summary>
    public async Task<HttpResponseMessage> HeadAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, new Uri(path, UriKind.Relative));
        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>Sends a request and returns the raw response, for the 400 and 404 cases.</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> GetRawAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, cancellationToken);
        return (response.StatusCode, body);
    }

    private async Task<TResult> GetAsync<TResult>(string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"GET {path} returned {(int)response.StatusCode}: {payload}");

        var result = JsonSerializer.Deserialize<TResult>(payload, JsonOptions);
        Assert.NotNull(result);
        return result;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>Mirrors the API's search response shape.</summary>
    public sealed record SearchResponse(string Term, IReadOnlyList<string> Tokens, PagedResult<ProductCardDto> Results);
}
