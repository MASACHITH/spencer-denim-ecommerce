using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Ops.Email.Abstractions;

namespace SpencerDenim.Infrastructure.Ops.Email;

/// <summary>
/// Data Protection for <c>ops.EmailOutbox.TransportOverrideProtected</c> (doc 08 §3.8 N2), purpose
/// <see cref="ISmtpOverrideProtector.Purpose"/>: the previous mail account, as JSON, encrypted.
/// <para>
/// The JSON shape is private to this class and versioned by the purpose string: a new shape is a new purpose, not an
/// edit, so an old row is never misread as a new one.
/// </para>
/// </summary>
internal sealed class SmtpOverrideProtector : ISmtpOverrideProtector
{
    private readonly IDataProtector _protector;

    public SmtpOverrideProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _protector = provider.CreateProtector(ISmtpOverrideProtector.Purpose);
    }

    public string Protect(SmtpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var payload = new Payload(
            settings.Host,
            settings.Port,
            settings.Username,
            settings.Password,
            settings.FromAddress,
            settings.FromName,
            settings.UseStartTls);

        return _protector.Protect(JsonSerializer.Serialize(payload));
    }

    public bool TryUnprotect(string? protectedValue, out SmtpSettings? settings)
    {
        settings = null;

        if (string.IsNullOrEmpty(protectedValue))
        {
            return false;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(protectedValue));

            if (payload is null
                || string.IsNullOrWhiteSpace(payload.Host)
                || string.IsNullOrWhiteSpace(payload.FromAddress)
                || payload.Port is < 1 or > 65535)
            {
                return false;
            }

            settings = new SmtpSettings(
                payload.Host,
                payload.Port,
                payload.Username,
                payload.Password,
                payload.FromAddress,
                payload.FromName,
                payload.UseStartTls);

            return true;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or JsonException)
        {
            // Swallowed and not logged, for SettingSecretProtector's reason: the payload holds a password.
            return false;
        }
    }

    private sealed record Payload(
        string Host,
        int Port,
        string? Username,
        string? Password,
        string FromAddress,
        string? FromName,
        bool UseStartTls);
}
