using SpencerDenim.Application.Accounts.Contracts;

namespace SpencerDenim.Api.Endpoints.Accounts;

/// <summary>
/// The bodies the customer account endpoints read.
/// </summary>
/// <remarks>
/// <b>Every field is nullable and so is every record — issue #47.</b> A well-formed JSON object that
/// omits a required string binds <see langword="null"/> into a non-nullable reference property, because
/// <c>System.Text.Json</c> ignores nullable annotations; <c>POST /api/admin/auth/login</c> answered
/// <b>500</b> to <c>{}</c> for exactly that reason until #144. So the type system here says what JSON can
/// actually send, and each use case decides what a missing field means — which on these routes is a 400
/// naming the field, never a 500 and never a silent default.
/// </remarks>
internal sealed record CustomerRegisterRequest(
    string? FirstName,
    string? LastName,
    string? Email,
    string? Password,
    string? Phone);

/// <inheritdoc cref="CustomerRegisterRequest"/>
internal sealed record CustomerSignInRequest(string? Email, string? Password);

/// <inheritdoc cref="CustomerRegisterRequest"/>
internal sealed record CustomerForgotPasswordRequest(string? Email);

/// <summary>
/// A one-time link's token, as the storefront read it out of the query string.
/// </summary>
/// <remarks>
/// <b>In the body and not in the URL of this request</b>, although it arrived in the URL of the page. A
/// token in a request URL lands in the API's access log and in any proxy's; the page's own address is the
/// browser's business and the shop's log does not see it. The storefront strips it from the address bar
/// after reading it, so it does not sit in history either.
/// </remarks>
internal sealed record CustomerTokenRequest(string? Token);

/// <inheritdoc cref="CustomerRegisterRequest"/>
internal sealed record CustomerResetPasswordRequest(string? Token, string? Password);

/// <inheritdoc cref="CustomerRegisterRequest"/>
internal sealed record CustomerChangePasswordRequest(string? CurrentPassword, string? NewPassword);

/// <inheritdoc cref="CustomerRegisterRequest"/>
internal sealed record CustomerProfileRequest(
    string? FirstName,
    string? LastName,
    string? Phone,
    int? DefaultDistrictId);

/// <inheritdoc cref="CustomerRegisterRequest"/>
/// <remarks>
/// <c>DistrictId</c> is a nullable <c>int</c> for the same reason the strings are nullable: an omitted
/// number binds <c>0</c> on a non-nullable property, and <c>0</c> is indistinguishable from "they sent
/// zero" — so the validator could not tell a missing district from an invalid one and would give the
/// wrong message. <c>IsDefault</c> is a nullable <c>bool</c> and an omitted one means false, which is the
/// right default for "make this my default address".
/// </remarks>
internal sealed record CustomerAddressRequest(
    string? Label,
    string? RecipientName,
    string? Phone,
    string? Line1,
    string? Line2,
    string? City,
    int? DistrictId,
    string? PostalCode,
    string? Landmark,
    bool? IsDefault,
    string? CountryCode = null,
    string? Region = null);

/// <summary>One sentence, for the endpoints whose whole answer is one sentence.</summary>
/// <remarks>
/// A record rather than a bare string so the response is a JSON object: a client reading
/// <c>response.message</c> keeps working when a field is added, where one reading a bare string breaks.
/// </remarks>
internal sealed record AccountMessageResponse(string Message);

/// <summary>Who is signed in, as the storefront shell needs them.</summary>
/// <param name="EmailConfirmed">
/// Doc 11 §7 G-24: never a gate — an unconfirmed customer may sign in, shop and check out. It is what the
/// "confirm your email" banner is shown from, and what explains why a customer's earlier guest orders are
/// not in their history yet (doc 08 §3.1 D5).
/// </param>
internal sealed record CustomerSessionResponse(
    int UserId,
    string Email,
    string FirstName,
    string? LastName,
    bool EmailConfirmed)
{
    public static CustomerSessionResponse From(CustomerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new CustomerSessionResponse(
            session.UserId,
            session.Email,
            session.FirstName,
            session.LastName,
            session.EmailConfirmed);
    }
}

/// <summary>
/// What confirming an address did (doc 11 P11 step A.4, doc 08 §3.1 D5).
/// </summary>
/// <param name="LinkedOrderNumbers">
/// The guest orders that became this customer's, newest first, and often empty. Doc 11 P11's own example
/// is the reason it is here: "Customer registers with the email used for 3 guest orders → after
/// confirming, all 3 appear in My account." A confirmation that said nothing would leave a shopper
/// wondering whether it had worked.
/// </param>
internal sealed record CustomerEmailConfirmedResponse(
    CustomerSessionResponse Customer,
    IReadOnlyList<string> LinkedOrderNumbers);

/// <summary>
/// <c>GET /api/account/session</c>: the signed-in customer, or <c>null</c> for a visitor who is not signed in.
/// </summary>
/// <remarks>
/// <b>A 200 either way</b>, because the storefront header asks on every page view and being signed out
/// is the ordinary state of most visitors, not an error. <c>/me</c>'s 401 for the same visitor is a
/// failed resource load in the browser's console on every page — the home page's "no console errors"
/// browser test caught it once the header started asking (W3-01 round 2).
/// </remarks>
internal sealed record CustomerSessionStateResponse(CustomerSessionResponse? Customer);
