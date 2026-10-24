using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// Drives <c>/api/admin/catalog</c> as a signed-in staff user: creates the user with exactly the
/// permissions a test names, signs in, and carries the antiforgery token on every mutating call.
/// <para>
/// The permissions are a parameter rather than "give it everything", because a route guarded by the wrong
/// permission is the kind of defect a suite that always signs in as a Super Admin cannot see.
/// </para>
/// </summary>
public sealed class AdminCatalogApiClient
{
    public const string Password = "Correct-Password-1!";

    private const string LoginUrl = "/api/admin/auth/login";
    private const string AntiforgeryUrl = "/api/admin/auth/antiforgery-token";
    private const string AntiforgeryHeader = "X-XSRF-TOKEN";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;

    private AdminCatalogApiClient(HttpClient client, int userId, string email)
    {
        _client = client;
        UserId = userId;
        Email = email;
    }

    public int UserId { get; }

    public string Email { get; }

    /// <summary>Creates a staff user with the given permissions, signs them in and returns the client.</summary>
    public static async Task<AdminCatalogApiClient> SignInAsync(
        ApiFactory factory,
        string label,
        IReadOnlyList<string> permissions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var email = $"w112-{label}-{Guid.NewGuid():N}@example.com";
        var userId = await CreateStaffUserAsync(factory, email, permissions, cancellationToken);

        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        using var login = await client.PostAsJsonAsync(
            LoginUrl, new { email, password = Password }, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return new AdminCatalogApiClient(client, userId, email);
    }

    /// <summary>An unauthenticated client against the same host, for the 401 cases.</summary>
    public static HttpClient Anonymous(ApiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
    }

    public Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string path, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, path, body: null, rowVersionHeader: null, cancellationToken);

    public Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        string path,
        object body,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, path, JsonSerializer.Serialize(body, JsonOptions), null, cancellationToken);

    public Task<(HttpStatusCode Status, JsonElement Body)> PutAsync(
        string path,
        object body,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, path, JsonSerializer.Serialize(body, JsonOptions), null, cancellationToken);

    /// <summary>A write whose body is whatever a test wants to send, including something that is not JSON.</summary>
    public Task<(HttpStatusCode Status, JsonElement Body)> PutRawAsync(
        string path,
        string body,
        CancellationToken cancellationToken,
        string contentType = "application/json") =>
        SendAsync(HttpMethod.Put, path, body, null, cancellationToken, contentType);

    /// <inheritdoc cref="PutRawAsync"/>
    public Task<(HttpStatusCode Status, JsonElement Body)> PostRawAsync(
        string path,
        string body,
        CancellationToken cancellationToken,
        string contentType = "application/json") =>
        SendAsync(HttpMethod.Post, path, body, null, cancellationToken, contentType);

    /// <summary>
    /// A write whose <c>Content-Type</c> header is sent exactly as given. <see cref="StringContent"/> rebuilds the
    /// header from an <see cref="Encoding"/> and refuses or rewrites a charset it does not know, which is the very
    /// thing the issue #147 tests need to put on the wire.
    /// </summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> PostWithContentTypeHeaderAsync(
        string path,
        byte[] body,
        string contentTypeHeader,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add(AntiforgeryHeader, await AntiforgeryTokenAsync(cancellationToken));
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentTypeHeader);

        using var response = await _client.SendAsync(request, cancellationToken);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var json = string.IsNullOrWhiteSpace(payload)
            ? default
            : JsonSerializer.Deserialize<JsonElement>(payload, JsonOptions);

        return (response.StatusCode, json);
    }

    public Task<(HttpStatusCode Status, JsonElement Body)> DeleteAsync(
        string path,
        string? rowVersion,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, path, body: null, rowVersion, cancellationToken);

    /// <summary>A mutating request sent deliberately without the antiforgery header.</summary>
    public async Task<HttpStatusCode> SendWithoutAntiforgeryAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(request, cancellationToken);
        return response.StatusCode;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpMethod method,
        string path,
        string? body,
        string? rowVersionHeader,
        CancellationToken cancellationToken,
        string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

        if (method != HttpMethod.Get)
        {
            request.Headers.Add(AntiforgeryHeader, await AntiforgeryTokenAsync(cancellationToken));
        }

        if (rowVersionHeader is not null)
        {
            request.Headers.Add("If-Match", $"\"{rowVersionHeader}\"");
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        using var response = await _client.SendAsync(request, cancellationToken);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var json = string.IsNullOrWhiteSpace(payload)
            ? default
            : JsonSerializer.Deserialize<JsonElement>(payload, JsonOptions);

        return (response.StatusCode, json);
    }

    private async Task<string> AntiforgeryTokenAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(new Uri(AntiforgeryUrl, UriKind.Relative), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, cancellationToken);
        return body.GetProperty("token").GetString()!;
    }

    private static async Task<int> CreateStaffUserAsync(
        ApiFactory factory,
        string email,
        IReadOnlyList<string> permissions,
        CancellationToken cancellationToken)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            UserType = UserType.Staff,
            FirstName = "Catalogue",
            LastName = "Tester",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var created = await userManager.CreateAsync(user, Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));

        var roleName = $"w112-role-{Guid.NewGuid():N}";
        var role = new AppRole
        {
            Name = roleName,
            Description = "Test role (W1-12 admin catalogue tests)",
            IsSystem = false,
            GrantsAllPermissions = false,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var roleCreated = await roleManager.CreateAsync(role);
        Assert.True(roleCreated.Succeeded, string.Join("; ", roleCreated.Errors.Select(error => error.Description)));

        foreach (var permission in permissions)
        {
            var claim = await roleManager.AddClaimAsync(
                role, new Claim(RoleClaimConfiguration.PermissionClaimType, permission));
            Assert.True(claim.Succeeded, string.Join("; ", claim.Errors.Select(error => error.Description)));
        }

        var assigned = await userManager.AddToRoleAsync(user, roleName);
        Assert.True(assigned.Succeeded, string.Join("; ", assigned.Errors.Select(error => error.Description)));

        _ = cancellationToken;
        return user.Id;
    }
}
