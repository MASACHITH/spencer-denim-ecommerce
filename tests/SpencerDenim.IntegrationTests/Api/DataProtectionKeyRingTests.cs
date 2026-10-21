using System.Diagnostics;
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
        // ApiFactory gives every host a key ring directory by default; this test is about its absence.
        await using var factory = CreateFactory(new() { ["DataProtection:KeysPath"] = string.Empty }, environment: "Production");

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

    /// <summary>
    /// #195, the reason <c>deploy/scripts/make-dp-cert.sh</c> must run <b>before the first deploy</b> and refuses once a key
    /// ring exists (docs/16 B7): switching a certificate on later does not touch the keys already on disk. They stay in clear
    /// text and stay in use until they expire, so "encrypted at rest" is not true of a ring that existed first.
    /// </summary>
    [Fact]
    public async Task A_certificate_switched_on_later_does_not_encrypt_the_keys_that_already_exist()
    {
        var keys = Path.Combine(_scratch.FullName, "keys");
        var certificatePath = Path.Combine(_scratch.FullName, "dp.pfx");
        const string password = "test-only-pfx-password";
        WriteSelfSignedCertificate(certificatePath, password);

        string protectedPayload;
        await using (var first = CreateFactory(new() { ["DataProtection:KeysPath"] = keys }))
        {
            protectedPayload = Protector(first).Protect("written before the certificate existed");
        }

        var keyFile = Directory.GetFiles(keys, "key-*.xml").Single();
        var before = await File.ReadAllTextAsync(keyFile, TestContext.Current.CancellationToken);
        Assert.Contains("<masterKey", before, StringComparison.Ordinal);
        Assert.DoesNotContain("encryptedSecret", before, StringComparison.Ordinal);

        await using var withCertificate = CreateFactory(new()
        {
            ["DataProtection:KeysPath"] = keys,
            ["DataProtection:CertificatePath"] = certificatePath,
            ["DataProtection:CertificatePassword"] = password,
        });

        // Everything written before is still readable, and the old key file was not rewritten.
        Assert.Equal("written before the certificate existed", Protector(withCertificate).Unprotect(protectedPayload));
        Assert.Equal(before, await File.ReadAllTextAsync(keyFile, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(
            "encryptedSecret",
            await File.ReadAllTextAsync(keyFile, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// #195: the certificate the DEPLOYMENT makes, not one this test builds itself. <c>deploy/scripts/make-dp-cert.sh</c>
    /// makes the .pfx with OpenSSL (a different encoder from <see cref="X509Certificate2"/>, and the one the server will
    /// really use), so "the API still starts with it, and the key ring it writes is encrypted" is proved against the real
    /// file, not against a stand-in. Skipped (not failed) where bash or openssl is missing: both are on every Linux runner
    /// and on a developer machine with Git for Windows.
    /// </summary>
    [Fact]
    public async Task A_certificate_made_by_make_dp_cert_sh_is_accepted_and_encrypts_the_key_ring()
    {
        var script = Path.Combine(FindRepositoryRoot(), "deploy", "scripts", "make-dp-cert.sh");
        Assert.True(File.Exists(script), $"deploy script not found at {script}");

        var appsBase = Path.Combine(_scratch.FullName, "apps");
        Directory.CreateDirectory(Path.Combine(appsBase, "staging", "spencer-denim-app", "config"));
        const string password = "Test0nlyPassw0rd.NotASecret";

        var (exitCode, output) = RunBash(
            $"'{script.Replace('\\', '/')}' staging --password-stdin",
            stdin: password + "\n",
            new Dictionary<string, string>
            {
                ["SD_APPS_BASE"] = appsBase.Replace('\\', '/'),
                ["SD_DP_CERT_ALLOW_NONROOT"] = "1",
            });
        Assert.SkipWhen(
            exitCode != 0 && output.Contains("openssl is not installed", StringComparison.Ordinal),
            "openssl is not available to the shell that runs the script.");
        Assert.True(exitCode == 0, "make-dp-cert.sh failed:" + Environment.NewLine + output);
        Assert.DoesNotContain(password, output, StringComparison.Ordinal);   // the script never echoes a password it was given

        var certificatePath = Path.Combine(
            appsBase, "staging", "spencer-denim-app", "config", "dataprotection", "dataprotection.pfx");
        Assert.True(File.Exists(certificatePath), "the script did not write the .pfx");

        var keys = Path.Combine(_scratch.FullName, "keys");
        var configuration = new Dictionary<string, string?>
        {
            ["DataProtection:KeysPath"] = keys,
            ["DataProtection:CertificatePath"] = certificatePath,
            ["DataProtection:CertificatePassword"] = password,
        };

        string protectedPayload;
        await using (var first = CreateFactory(configuration))
        {
            protectedPayload = Protector(first).Protect("encrypted with the deployment's certificate");
        }

        var keyXml = await File.ReadAllTextAsync(
            Directory.GetFiles(keys, "key-*.xml").Single(), TestContext.Current.CancellationToken);
        Assert.Contains("encryptedSecret", keyXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<masterKey", keyXml, StringComparison.Ordinal);

        await using var restarted = CreateFactory(configuration);

        Assert.Equal("encrypted with the deployment's certificate", Protector(restarted).Unprotect(protectedPayload));

        // And the wrong password is refused at start-up, not discovered at the next restore.
        await using var wrongPassword = CreateFactory(new()
        {
            ["DataProtection:KeysPath"] = keys,
            ["DataProtection:CertificatePath"] = certificatePath,
            ["DataProtection:CertificatePassword"] = password + "x",
        });
        Assert.ThrowsAny<Exception>(() => wrongPassword.Services);
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

    private static readonly string[] GitBashCandidates =
    [
        @"C:\Program Files\Git\bin\bash.exe",
        @"C:\Program Files (x86)\Git\bin\bash.exe",
    ];

    /// <summary>Runs a command line in bash (a login shell, so Git for Windows' mingw64 tools such as openssl are on PATH).</summary>
    private static (int ExitCode, string Output) RunBash(string commandLine, string stdin, Dictionary<string, string> environment)
    {
        var bash = OperatingSystem.IsWindows() ? GitBashCandidates.FirstOrDefault(File.Exists) : "bash";
        Assert.SkipWhen(bash is null, "Git for Windows' bash is not installed, so the deploy script cannot be driven.");

        var info = new ProcessStartInfo(bash!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-lc");
        info.ArgumentList.Add(commandLine);
        info.Environment["CHERE_INVOKING"] = "1";
        foreach (var (key, value) in environment)
        {
            info.Environment[key] = value;
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("bash did not start.");
        }
        catch (Exception exception)
        {
            Assert.Skip($"bash cannot be started, so the deploy script cannot be driven: {exception.Message}");
            throw;
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
            Assert.True(process.WaitForExit(120_000), "make-dp-cert.sh did not finish in two minutes.");
            return (process.ExitCode, stdout.Result + stderr.Result);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SpencerDenim.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate SpencerDenim.sln above the test output directory.");
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
