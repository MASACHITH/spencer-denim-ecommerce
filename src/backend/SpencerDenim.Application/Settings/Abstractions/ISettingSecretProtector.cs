namespace SpencerDenim.Application.Settings.Abstractions;

/// <summary>
/// Encrypts and decrypts a <see cref="SpencerDenim.Domain.Settings.SettingValueType.Secret"/> value.
/// Doc 08 §3.8: "Secret = Data Protection ciphertext (purpose <c>SpencerDenim.Settings.v1</c>)".
/// <para>
/// <b>Why this exists with nothing to encrypt.</b> No key in <see cref="SpencerDenim.Domain.Settings.SettingCatalog"/>
/// is a secret today — PayPal's client secret and the SMTP password arrive with #53 and W3. The machinery
/// is built now because the cost of getting it wrong later is asymmetric: the task that adds the first
/// secret is a task about PayPal, under its own deadline, and "where does the ciphertext come from" is the
/// question that gets answered by storing plaintext "just for staging". Building the port and the purpose
/// string first means that task has nowhere to put a plaintext credential.
/// </para>
/// <para>
/// <b>The purpose string is part of the contract, not an implementation detail.</b> Data Protection derives
/// the key from it, so changing it makes every stored secret undecryptable — which is why doc 08 names it
/// and why it carries a version suffix. A rotation is a new purpose plus a migration that re-protects, not
/// an edit to a string.
/// </para>
/// </summary>
public interface ISettingSecretProtector
{
    /// <summary>
    /// The purpose doc 08 §3.8 specifies. Exposed so a test can assert it rather than trust a comment, and
    /// so <c>ops.EmailOutbox.TransportOverrideProtected</c>'s own purpose can be proved to differ (it does:
    /// <c>SpencerDenim.EmailOutbox.SmtpOverride.v1</c>). Two purposes mean a value protected for one cannot
    /// be unprotected by the other, which is the containment the two columns are supposed to have.
    /// </summary>
    const string Purpose = "SpencerDenim.Settings.v1";

    /// <summary>Protects a plaintext value for storage in <c>ops.Settings.Value</c>.</summary>
    string Protect(string plaintext);

    /// <summary>
    /// Unprotects a stored value, or <see langword="false"/> when it cannot be read — a value protected
    /// with a key that has been lost, or a column somebody put plaintext in.
    /// <para>
    /// A <see langword="bool"/> rather than an exception because every caller has the same sensible answer
    /// ("treat it as not configured") and because an exception carrying a cryptographic failure tends to
    /// end up in a log, which is the one place a secret must never be. For the same reason the
    /// <see langword="out"/> value is the only way the plaintext leaves this call.
    /// </para>
    /// </summary>
    bool TryUnprotect(string? protectedValue, out string plaintext);
}
