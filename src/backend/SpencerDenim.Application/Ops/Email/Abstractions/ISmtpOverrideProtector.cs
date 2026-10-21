namespace SpencerDenim.Application.Ops.Email.Abstractions;

/// <summary>
/// Protects a whole mail account for <c>ops.EmailOutbox.TransportOverrideProtected</c> (doc 08 §3.8 N2) under its own
/// Data Protection purpose, <see cref="Purpose"/>. Implemented in Infrastructure.
/// <para>
/// <b>A different purpose from <c>ops.Settings</c>' secrets</b> (<c>SpencerDenim.Settings.v1</c>), so ciphertext from
/// one column can never be unprotected as the other: an outbox row cannot be made to yield a settings secret and a
/// settings value cannot be smuggled in as a transport override.
/// </para>
/// </summary>
public interface ISmtpOverrideProtector
{
    /// <summary>Doc 08 §3.8's purpose for the column.</summary>
    const string Purpose = "SpencerDenim.EmailOutbox.SmtpOverride.v1";

    /// <summary>The account as ciphertext — password included, which is why it lives only until the row is terminal.</summary>
    string Protect(SmtpSettings settings);

    /// <summary>
    /// The account back, or <see langword="false"/> when the value cannot be read (a lost key, or tampering). A
    /// <see langword="bool"/>, never an exception, so a cryptographic failure carrying the payload cannot reach a log.
    /// </summary>
    bool TryUnprotect(string? protectedValue, out SmtpSettings? settings);
}
