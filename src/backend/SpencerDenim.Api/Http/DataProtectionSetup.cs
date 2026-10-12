using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;

namespace SpencerDenim.Api.Http;

/// <summary>
/// Where the ASP.NET Data Protection key ring lives (issue #96, W0-15, docs/15 §4).
/// <para>
/// <b>Why this is load-bearing before any secret exists.</b> Data Protection encrypts the admin
/// authentication cookie and every antiforgery token, and it is what <c>SettingSecretProtector</c> and the
/// email outbox's SMTP override encrypt with. The framework default keeps the key ring in a per-machine
/// directory inside the container, so without this every container restart would sign every member of
/// staff out, fail every form they had open, and make every protected setting unreadable — a failure that
/// looks like data corruption rather than configuration.
/// </para>
/// <para>
/// Bound from configuration section <c>DataProtection</c>:
/// <list type="bullet">
///   <item><c>DataProtection__KeysPath</c>: directory the key ring is persisted to (a volume in Docker).
///   A relative value is resolved against the content root. <b>Required in Production</b>: the API refuses
///   to start without it, because the failure it prevents is silent until the first restart.</item>
///   <item><c>DataProtection__CertificatePath</c> / <c>DataProtection__CertificatePassword</c>: an optional
///   PKCS#12 (.pfx) certificate that encrypts the keys at rest (doc 03 §4, doc 08 §3.8). Without it the
///   keys are stored unencrypted on the volume, which only root on the host and the API can read.</item>
/// </list>
/// </para>
/// <para>
/// The application name is pinned, because the default is derived from the content root and differs
/// between a local run and a container: two hosts sharing one key ring would otherwise still be unable to
/// read each other's cookies.
/// </para>
/// </summary>
internal static class DataProtectionSetup
{
    public const string SectionName = "DataProtection";

    /// <summary>An identifier, not a display name: changing it makes every existing cookie and secret unreadable.</summary>
    public const string ApplicationName = "SpencerDenim";

    public static WebApplicationBuilder AddApiDataProtection(this WebApplicationBuilder builder)
    {
        var settings = new DataProtectionSettings();
        builder.Configuration.GetSection(SectionName).Bind(settings);

        var dataProtection = builder.Services.AddDataProtection().SetApplicationName(ApplicationName);

        if (string.IsNullOrWhiteSpace(settings.KeysPath))
        {
            if (builder.Environment.IsProduction())
            {
                throw new InvalidOperationException(
                    "DataProtection__KeysPath is not set. In Production the Data Protection key ring must be "
                    + "persisted to a volume, or every restart signs every admin out and makes encrypted "
                    + "settings unreadable (issue #96, docs/15 §4).");
            }

            return builder;
        }

        var keysDirectory = new DirectoryInfo(
            Path.GetFullPath(settings.KeysPath, builder.Environment.ContentRootPath));
        dataProtection.PersistKeysToFileSystem(keysDirectory);

        if (!string.IsNullOrWhiteSpace(settings.CertificatePath))
        {
            var certificatePath = Path.GetFullPath(settings.CertificatePath, builder.Environment.ContentRootPath);
            if (!File.Exists(certificatePath))
            {
                throw new InvalidOperationException(
                    $"DataProtection__CertificatePath names a file that does not exist: {certificatePath}");
            }

            // The private key is needed to unprotect, so a certificate without one is a configuration
            // mistake that would otherwise surface as "every admin signed out" on the next restart.
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                certificatePath, settings.CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey)
            {
                throw new InvalidOperationException(
                    "DataProtection__CertificatePath names a certificate without a private key; it cannot "
                    + "decrypt the key ring it would encrypt.");
            }

            dataProtection.ProtectKeysWithCertificate(certificate);
        }

        return builder;
    }
}

/// <summary>Configuration section <c>DataProtection</c>; see <see cref="DataProtectionSetup"/>.</summary>
internal sealed class DataProtectionSettings
{
    public string? KeysPath { get; set; }

    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }
}
