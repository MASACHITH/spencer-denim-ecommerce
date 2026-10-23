namespace SpencerDenim.Application.Ops.Email.Abstractions;

/// <summary>
/// Reads the mail account to send through <b>now</b>: the <c>Email.Smtp</c> rows when they make a complete, valid
/// account, otherwise <c>Email__Smtp__*</c> (<see cref="SmtpSettingsResolver"/>). Implemented in Infrastructure.
/// <para>
/// <b>Read per sweep, never cached across one.</b> Before 2026-10-21 the transport was handed the options bound once
/// at start-up, so a changed account needed a restart. A Super Admin who corrects a password must have the next
/// sweep — thirty seconds — use it, and a hijacked account that has just been put right must not keep sending for
/// another minute. The read is seven rows by primary key every thirty seconds.
/// </para>
/// </summary>
public interface ISmtpSettingsReader
{
    /// <summary>The effective account and its source.</summary>
    Task<EffectiveSmtpSettings> ReadEffectiveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The stored password, decrypted, or <see langword="null"/> when none is stored or it cannot be read. For the
    /// "Send test email" form's "leave blank to keep" box only; it never leaves the server.
    /// </summary>
    Task<string?> ReadStoredPasswordAsync(CancellationToken cancellationToken);
}
