using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api;

/// <summary>
/// Issue #96: the Data Protection key ring survives a restart. Data Protection encrypts the admin cookie, the
/// antiforgery tokens and every protected setting, so "a second host reads what the first one wrote" is the same
/// statement as "a container restart signs nobody out". No SQL Server is needed.
/// </summary>
public sealed class DataProtectionKeyRingTests : IDisposable
{
    private const string Purpose = "SpencerDenim.Tests.KeyRing";

    private readonly DirectoryInfo _scratch = Directory.CreateTempSubdirectory("sd-dp-");

    public void Dispose() => _scratch.Delete(recursive: true);

    [Fact]
    public async Task A_payload_protected_by_one_host_is_readable_by_the_next_one_on_the_same_key_ring()
    {
        var keys = Path.Combine(_scratch.FullName, "keys");
        string protectedPayload;

        await using (var first = CreateFactory(new() { ["DataProtection:KeysPath"] = keys }))
        {
            protectedPayload = Protector(first).Protect("still signed in");
        }

        Assert.NotEmpty(Directory.GetFiles(keys, "key-*.xml"));

        await using var restarted = CreateFactory(new() { ["DataProtection:KeysPath"] = keys });

        Assert.Equal("still signed in", Protector(restarted).Unprotect(protectedPayload));
    }

    [Fact]
    public async Task The_application_name_is_pinned_rather_than_derived_from_the_content_root()
    {
        await using var factory = CreateFactory(new() { ["DataProtection:KeysPath"] = Path.Combine(_scratch.FullName, "keys") });

        var options = factory.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value;

        Assert.Equal("SpencerDenim", options.ApplicationDiscriminator);
    }

    [Fact]
    public async Task Production_refuses_to_start_without_a_persisted_key_ring()
    {
        await using var factory = CreateFactory(new(), environment: "Production");

        var exception = Assert.ThrowsAny<Exception>(() => factory.Services);

        Assert.Contains("DataProtection__KeysPath", Flatten(exception), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_a_certificate_the_keys_are_encrypted_at_rest_and_still_survive_a_restart()
    {
        var keys = Path.Combine(_scratch.FullName, "keys");
        var certificatePath = Path.Combine(_scratch.FullName, "dp.pfx");
        const string password = "test-only-pfx-password";
        WriteSelfSignedCertificate(certificatePath, password);

        var configuration = new Dictionary<string, string?>
        {
            ["DataProtection:KeysPath"] = keys,
            ["DataProtection:CertificatePath"] = certificatePath,
            ["DataProtection:CertificatePassword"] = password,
        };

        string protectedPayload;
        await using (var first = CreateFactory(configuration))
        {
            protectedPayload = Protector(first).Protect("encrypted at rest");
        }

        var keyXml = await File.ReadAllTextAsync(
            Directory.GetFiles(keys, "key-*.xml").Single(), TestContext.Current.CancellationToken);
        Assert.Contains("encryptedSecret", keyXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<masterKey", keyXml, StringComparison.Ordinal);

        await using var restarted = CreateFactory(configuration);

        Assert.Equal("encrypted at rest", Protector(restarted).Unprotect(protectedPayload));
    }

    [Fact]
    public async Task A_certificate_path_that_does_not_exist_stops_the_api_from_starting()
    {
        await using var factory = CreateFactory(new()
        {
            ["DataProtection:KeysPath"] = Path.Combine(_scratch.FullName, "keys"),
            ["DataProtection:CertificatePath"] = Path.Combine(_scratch.FullName, "missing.pfx"),
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.Services);

        Assert.Contains("DataProtection__CertificatePath", Flatten(exception), StringComparison.Ordinal);
    }

    /// <summary>
    /// The key ring settings go in through <c>UseSetting</c>, not <c>additionalConfiguration</c>: the API reads
    /// them while it registers its services, which is when a deployment's environment variables are already
    /// there but before <see cref="ApiFactory"/>'s <c>ConfigureAppConfiguration</c> sources are applied. A host
    /// setting is applied as early as the environment variables it stands in for.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(
        Dictionary<string, string?> configuration, string environment = "Testing") =>
        new ApiFactory(ApiFactory.UnreachableConnectionString, environment).WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in configuration)
            {
                builder.UseSetting(key, value);
            }
        });

    private static IDataProtector Protector(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    private static void WriteSelfSignedCertificate(string path, string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=SpencerDenim Data Protection (test)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
    }

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }
}
