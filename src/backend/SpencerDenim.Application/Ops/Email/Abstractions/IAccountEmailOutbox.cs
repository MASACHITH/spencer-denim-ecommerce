using SpencerDenim.Application.Ops.Email.Contracts;

namespace SpencerDenim.Application.Ops.Email.Abstractions;

/// <summary>
/// Doc 11 P11's four account emails, each enlisted into the caller's own transaction (ACC-01).
/// </summary>
/// <remarks>
/// <b>It sits beside <see cref="IOrderEmailOutbox"/> for the reason that interface gives</b>: the writer
/// takes a finished row, deciding what it says is composition, and a use case in the middle of writing a
/// user row should not be assembling HTML.
/// <para>
/// <b>Two of the four may decline to queue, and that is the one behaviour a caller has to know about.</b>
/// <see cref="EnlistEmailConfirmation"/> and <see cref="EnlistPasswordReset"/> are nothing but a link,
/// and the link is built from <c>Email:StorefrontBaseUrl</c>, which a deployment may legitimately not
/// have set. They return <see langword="false"/> in that case and queue nothing, rather than sending a
/// message whose instructions are missing. The two tokenless messages always queue.
/// </para>
/// <para>
/// <b>Nothing here promises delivery, and no caller may say that it does.</b> Doc 08 §3.8's transport is
/// unconfigured until the client's mail credentials arrive: an enlisted row is a <i>queued</i> message,
/// and whether it is ever sent is <c>SendEmails</c>' business. Two Majors on this project came from copy
/// that promised an email a transport could not send, so the storefront's wording says "queued" and the
/// return value here says only "composed and enlisted".
/// </para>
/// </remarks>
public interface IAccountEmailOutbox
{
    /// <summary>
    /// Doc 11 P11 step A.3 / step 10: the confirmation link, for a new account or a changed address.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a row was enlisted; <see langword="false"/> when there is no
    /// configured storefront origin and therefore no link to send.
    /// </returns>
    bool EnlistEmailConfirmation(AccountEmailFacts facts, string token);

    /// <summary>Doc 11 P11 step A.3: the "your account is ready" message. Always enlists.</summary>
    void EnlistWelcome(AccountEmailFacts facts);

    /// <summary>
    /// Doc 11 P11 step A.2: sent to an address that already has a <b>customer</b> account when somebody
    /// tries to register with it again. Always enlists — it carries no one-time link.
    /// </summary>
    void EnlistAccountExists(AccountEmailFacts facts);

    /// <summary>
    /// Doc 11 P11 step C.8: the password-reset link. Body destroyed on send (doc 08 §3.8 G-03).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a row was enlisted; <see langword="false"/> when there is no
    /// configured storefront origin and therefore no link to send.
    /// </returns>
    bool EnlistPasswordReset(AccountEmailFacts facts, string token);
}
