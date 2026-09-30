using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Media.Abstractions;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Media;
using SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// An API instance with its own pair of media volumes, plus the bits every media test needs: a staff user
/// holding exactly the permissions under test, a signed-in client and an antiforgery token.
/// <para>
/// The volumes are two directories under a name nobody else would pick, created per instance and deleted on
/// dispose — doc 14 "Scratch scripts and ports are shared too": a shared path is not yours just because it
/// looks like it. Tests can read the directories directly, which is how the private-volume proof is made:
/// it asserts on what is on disk, not on what the code says it did.
/// </para>
/// </summary>
public sealed class MediaTestHost : IAsyncDisposable
{
    public const string AntiforgeryHeader = "X-XSRF-TOKEN";

    private readonly ApiFactory _factory;

    private MediaTestHost(ApiFactory factory, string root, string publicRoot, string privateRoot)
    {
        _factory = factory;
        Root = root;
        PublicRoot = publicRoot;
        PrivateRoot = privateRoot;
    }

    /// <summary>The temporary directory holding both volumes.</summary>
    public string Root { get; }

    /// <summary>The public uploads volume, which the reverse proxy would serve at <c>/media</c>.</summary>
    public string PublicRoot { get; }

    /// <summary>The private uploads volume, which it would not.</summary>
    public string PrivateRoot { get; }

    /// <summary>The API under test.</summary>
    public ApiFactory Factory => _factory;

    /// <summary>Starts an API with its own volumes.</summary>
    /// <param name="connectionString">The test database.</param>
    /// <param name="configure">Extra configuration, e.g. a lower size ceiling.</param>
    public static MediaTestHost Create(
        string connectionString,
        IReadOnlyDictionary<string, string?>? configure = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "sd-media-it-" + Guid.NewGuid().ToString("N"));
        var publicRoot = Path.Combine(root, "public-volume");
        var privateRoot = Path.Combine(root, "private-volume");
        Directory.CreateDirectory(publicRoot);
        Directory.CreateDirectory(privateRoot);

        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Media:PublicRoot"] = publicRoot,
            ["Media:PrivateRoot"] = privateRoot,
        };

        if (configure is not null)
        {
            foreach (var (key, value) in configure)
            {
                configuration[key] = value;
            }
        }

        return new MediaTestHost(
            new ApiFactory(connectionString, additionalConfiguration: configuration),
            root,
            publicRoot,
            privateRoot);
    }

    /// <summary>
    /// The same, but with no label font at all — which is what the API's own Linux container has
    /// (doc 03 §4; <c>SkiaSharp.NativeAssets.Linux.NoDependencies</c> brings no fontconfig and the base
    /// image brings no font file). A Windows machine always has fonts, so this substitution is the only way
    /// to run the deployment path here; the defect this guards against was invisible on Windows and failed
    /// the Linux CI job.
    /// </summary>
    public static MediaTestHost CreateWithoutFonts(string connectionString)
    {
        var (root, publicRoot, privateRoot, configuration) = PrepareVolumes();

        var factory = new ApiFactory(
            connectionString,
            additionalConfiguration: configuration,
            configureServices: services => services.AddSingleton<IPlaceholderLabelFont>(new NoLabelFont()));

        return new MediaTestHost(factory, root, publicRoot, privateRoot);
    }

    /// <summary>
    /// The same, with the real storage adapter wrapped — a test can then make a write fail on the third
    /// file, which is the only honest way to observe the rollback: the files written before the failure
    /// really are on disk.
    /// </summary>
    public static MediaTestHost CreateWithStorage(
        string connectionString,
        Func<IFileStorage, IFileStorage> decorate)
    {
        ArgumentNullException.ThrowIfNull(decorate);

        var root = Path.Combine(Path.GetTempPath(), "sd-media-it-" + Guid.NewGuid().ToString("N"));
        var publicRoot = Path.Combine(root, "public-volume");
        var privateRoot = Path.Combine(root, "private-volume");
        Directory.CreateDirectory(publicRoot);
        Directory.CreateDirectory(privateRoot);

        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Media:PublicRoot"] = publicRoot,
            ["Media:PrivateRoot"] = privateRoot,
        };

        var factory = new ApiFactory(
            connectionString,
            additionalConfiguration: configuration,
            configureServices: services => services.AddScoped<IFileStorage>(provider => decorate(
                new LocalFileStorage(
                    provider.GetRequiredService<MediaStorageRoots>(),
                    provider.GetRequiredService<ILogger<LocalFileStorage>>()))));

        return new MediaTestHost(factory, root, publicRoot, privateRoot);
    }

    /// <summary>
    /// A client signed in as a staff user holding <paramref name="permissions"/>, with an antiforgery token
    /// already on its default headers so a mutating call works out of the box.
    /// </summary>
    public Task<HttpClient> SignInAsync(params string[] permissions) => SignInAsync(_factory, permissions);

    /// <summary>
    /// The same, against any API instance — including one with no media volumes configured, which is how
    /// the "storage is switched off" answer is tested.
    /// </summary>
    public static async Task<HttpClient> SignInAsync(ApiFactory factory, params string[] permissions)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var email = $"media-{Guid.NewGuid():N}@example.com";
        const string password = "Correct-Horse-Battery-Staple-1!";

        await CreateStaffUserAsync(factory, email, password, permissions);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

        using var login = await client.PostAsJsonAsync(
            "/api/admin/auth/login",
            new { email, password },
            TestContext.Current.CancellationToken);

        if (!login.IsSuccessStatusCode)
        {
            client.Dispose();
            throw new InvalidOperationException($"Test sign-in failed with {(int)login.StatusCode}.");
        }

        using var tokenResponse = await client.GetAsync(
            new Uri("/api/admin/auth/antiforgery-token", UriKind.Relative),
            TestContext.Current.CancellationToken);

        var body = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Add(AntiforgeryHeader, body.GetProperty("token").GetString());

        return client;
    }

    /// <summary>An anonymous client, for <c>/media</c> and for the unauthenticated rejection tests.</summary>
    public HttpClient CreateAnonymousClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    /// <summary>A multipart body with one file part and optional text fields.</summary>
    public static MultipartFormDataContent FilePart(
        string fieldName,
        byte[] content,
        string fileName,
        string contentType = "application/octet-stream")
    {
        var form = new MultipartFormDataContent();
        AddFile(form, fieldName, content, fileName, contentType);
        return form;
    }

    /// <summary>Adds another file part to an existing body.</summary>
    public static void AddFile(
        MultipartFormDataContent form,
        string fieldName,
        byte[] content,
        string fileName,
        string contentType = "application/octet-stream")
    {
        ArgumentNullException.ThrowIfNull(form);

        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(part, fieldName, fileName);
    }

    /// <summary>Whether a file exists on the public volume at a storage key.</summary>
    public bool PublicFileExists(string storageKey) =>
        File.Exists(Path.Combine(PublicRoot, storageKey.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Every file on the public volume, as storage keys.</summary>
    public IReadOnlyList<string> PublicFiles() => Files(PublicRoot);

    /// <summary>Every file on the private volume, as storage keys.</summary>
    public IReadOnlyList<string> PrivateFiles() => Files(PrivateRoot);

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A handle the host has not released yet. The temp directory is per test and disposable.
        }
    }

    private static (string Root, string PublicRoot, string PrivateRoot, Dictionary<string, string?> Configuration)
        PrepareVolumes()
    {
        var root = Path.Combine(Path.GetTempPath(), "sd-media-it-" + Guid.NewGuid().ToString("N"));
        var publicRoot = Path.Combine(root, "public-volume");
        var privateRoot = Path.Combine(root, "private-volume");
        Directory.CreateDirectory(publicRoot);
        Directory.CreateDirectory(privateRoot);

        return (root, publicRoot, privateRoot, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Media:PublicRoot"] = publicRoot,
            ["Media:PrivateRoot"] = privateRoot,
        });
    }

    /// <summary>A host with no usable typeface. Not a stub of convenience: it is the production shape.</summary>
    private sealed class NoLabelFont : IPlaceholderLabelFont
    {
        public bool CanDrawText => false;

        public string Source => "none";

        public PlaceholderLabelFont? Font => null;
    }

    private static List<string> Files(string root) =>
        Directory.Exists(root)
            ? Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];

    private static async Task CreateStaffUserAsync(
        ApiFactory factory,
        string email,
        string password,
        string[] permissions)
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
            FirstName = "Media",
            LastName = "Tester",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var created = await userManager.CreateAsync(user, password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));

        if (permissions.Length == 0)
        {
            return;
        }

        // A role per test, holding exactly the permissions the test grants — so a test that means to check
        // a 403 is checking the policy, not a user who happens to have everything.
        var roleName = "media-test-" + Guid.NewGuid().ToString("N");
        var role = new AppRole
        {
            Name = roleName,
            Description = "Created by an integration test.",
            CreatedAtUtc = DateTime.UtcNow,
        };

        var roleCreated = await roleManager.CreateAsync(role);
        Assert.True(roleCreated.Succeeded, string.Join("; ", roleCreated.Errors.Select(error => error.Description)));

        foreach (var permission in permissions)
        {
            await roleManager.AddClaimAsync(
                role,
                new System.Security.Claims.Claim(RoleClaimConfiguration.PermissionClaimType, permission));
        }

        var assigned = await userManager.AddToRoleAsync(user, roleName);
        Assert.True(assigned.Succeeded, string.Join("; ", assigned.Errors.Select(error => error.Description)));
    }

    /// <summary>Formats a byte count for a message, matching what the API reports.</summary>
    public static string Megabytes(long bytes) =>
        (bytes / 1024d / 1024d).ToString("0.#", CultureInfo.InvariantCulture);
}
