using Microsoft.AspNetCore.DataProtection;
using SpencerDenim.Application.Settings.Abstractions;

namespace SpencerDenim.Infrastructure.Settings;

/// <summary>
/// Data Protection for <c>ops.Settings</c> secrets (doc 08 §3.8: purpose
/// <c>SpencerDenim.Settings.v1</c>).
/// <para>
/// <b>Built with nothing yet to protect, deliberately.</b> See
/// <see cref="ISettingSecretProtector"/> for why: the task that adds the first secret is a task about
/// PayPal or SMTP, and "where does the ciphertext come from" is the question that otherwise gets answered
/// with plaintext on staging.
/// </para>
/// <para>
/// <b>The key ring is not yet protected at rest.</b> Doc 08 §3.8 DB30 requires
/// <c>ProtectKeysWithCertificate</c> over an X.509 certificate, with <c>ops.DataProtectionKeys</c> as the
/// store — neither the table nor the certificate exists, so today this uses the framework's default
/// provider (a key ring on the local filesystem). That is honest for a build with no secrets in the table
/// and <b>must be closed before the first one goes in</b>: it is written here, and in the journal, rather
/// than left for somebody to discover from a working <c>Protect</c> call.
/// </para>
/// </summary>
internal sealed class SettingSecretProtector : ISettingSecretProtector
{
    private readonly IDataProtector _protector;

    public SettingSecretProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // The purpose comes from the interface's constant, so the string doc 08 specifies is written once
        // and a test can assert it against the same constant the production path uses.
        _protector = provider.CreateProtector(ISettingSecretProtector.Purpose);
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        return _protector.Protect(plaintext);
    }

    public bool TryUnprotect(string? protectedValue, out string plaintext)
    {
        plaintext = string.Empty;

        if (string.IsNullOrEmpty(protectedValue))
        {
            return false;
        }

        try
        {
            plaintext = _protector.Unprotect(protectedValue);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or System.Security.Cryptography.CryptographicException)
        {
            // Swallowed rather than logged, and the exception is not rethrown wrapped. Both are deliberate:
            // a Data Protection failure message can carry the payload it failed on, and ops.Settings.Value
            // is the one column whose contents must never reach a log file (doc 08 §3.8's redaction list).
            // The caller's correct response is identical for every cause — treat the key as not configured.
            return false;
        }
    }
}
