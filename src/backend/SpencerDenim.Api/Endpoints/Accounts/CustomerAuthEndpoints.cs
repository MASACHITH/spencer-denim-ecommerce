using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Accounts;
using SpencerDenim.Application.Accounts.Contracts;
using SpencerDenim.Application.Sales.Cart;
using SpencerDenim.Application.Sales.Cart.Abstractions;
using SpencerDenim.Infrastructure.Identity.Customers;

namespace SpencerDenim.Api.Endpoints.Accounts;

/// <summary>
/// Customer sign-in, sign-out, registration, email confirmation and password reset (ACC-01, doc 11 P11).
/// </summary>
/// <remarks>
/// <b>Three properties hold across every route here, and each one is a defect this project has
/// shipped.</b>
/// <list type="number">
/// <item><description>
/// <b>No reply says whether an address has an account.</b> Doc 08 §3.1 DB10: "Responses are generic
/// whether the email is unknown, a customer or staff." Registration and forgot-password both answer one
/// sentence with one status code; sign-in answers one refusal for a wrong password, an unknown address, a
/// staff address and a deactivated account alike. The <i>difference</i> is visible only in the inbox of
/// whoever owns the address.
/// </description></item>
/// <item><description>
/// <b>No reply promises an email.</b> Doc 08 §3.8's transport is unconfigured until the client's mail
/// credentials arrive, so an enlisted row is a <i>queued</i> message and nothing more. W3-02 removed "we
/// will email your order number" from checkout for this reason and <c>checkout-page.spec.ts</c> pins its
/// absence; two Majors on this project came from copy that promised what the transport could not do. The
/// wording here says what the server did — queued a link — and what the shopper can do next, which is
/// sign in with the password they just chose.
/// </description></item>
/// <item><description>
/// <b>Every refusal costs what a success costs.</b> #101 is open on the staff routes.
/// <c>CustomerUserLookup.SpendPasswordHashCost</c> runs the configured hasher against a fixed dummy hash
/// on every early-return path, so the cheap answers are not a timing oracle.
/// </description></item>
/// </list>
/// <para>
/// <b>Why there is no antiforgery token on these routes, unlike the admin API.</b> The admin app
/// registers a single <c>AddAntiforgery</c> with one cookie name and one header, and a second
/// configuration is not something this task may add to a shared startup path. What protects these routes
/// instead is two things together: the session cookie is <c>SameSite=Lax</c>, so a browser withholds it
/// from every cross-site <c>POST</c> (the whole CSRF surface for a write), and every route here reads a
/// JSON body, which a cross-site HTML form cannot send without a preflight the API does not allow. The
/// one thing <c>Lax</c> does permit is a top-level cross-site <b>GET</b> navigation, and no route here
/// changes anything on a GET. <b>This is reasoned, not inherited, and the task report asks for a second
/// opinion on it.</b>
/// </para>
/// </remarks>
internal static class CustomerAuthEndpoints
{
    /// <summary>
    /// The one sentence registration and forgot-password both answer with.
    /// </summary>
    /// <remarks>
    /// <b>Two clauses, and each is there for a different reason.</b> The first is doc 08 §3.1 DB10's
    /// generic response, worded so it is true of all three cases — a new account, an address that already
    /// has one, and a staff address — and it says "queued" rather than "sent", because the shop may have
    /// no mail transport. The second gives the shopper something to do that does not depend on an email
    /// arriving at all, which is what doc 11 §7 G-24 makes possible: an unconfirmed customer can sign in,
    /// shop and check out.
    /// </remarks>
    private const string RegistrationReply =
        "If that email address can be used, we have queued a link to it. You can sign in now with the "
        + "password you chose.";

    /// <summary>The one sentence a forgot-password request answers with.</summary>
    /// <remarks>
    /// The same shape and the same reasoning as <see cref="RegistrationReply"/>. It does not say "check
    /// your inbox", because that is a promise about delivery; it says what was queued and leaves the
    /// shopper a way to ask the shop if nothing arrives.
    /// </remarks>
    private const string ForgotPasswordReply =
        "If that email address can be used to sign in, we have queued a reset link to it. If nothing "
        + "arrives, please get in touch with us.";

    public static IEndpointRouteBuilder MapCustomerAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var api = endpoints.MapGroup("/api/account").WithTags("Customer Account");

        api.MapPost("/register", RegisterAsync)
            .WithName("CustomerRegister")
            .WithSummary("Create a customer account (ACC-01, doc 11 P11 step A).")
            .WithDescription(
                "Answers **one sentence and 202 for every address**, whether it is free, already a "
                + "customer's or a staff member's (doc 08 §3.1 DB10). A free address gets an account, a "
                + "confirmation link and a welcome message; an address that already has a customer "
                + "account gets an `AccountExists` message and no new account; a staff address gets "
                + "nothing at all. Field problems are a 400 naming each field. **No session is issued** "
                + "— a response that sometimes carried one would itself say whether the address was "
                + "free. Sign in afterwards: confirmation gates only the linking of earlier guest orders "
                + "(doc 11 §7 G-24).")
            .Produces<AccountMessageResponse>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        api.MapPost("/login", SignInAsync)
            .WithName("CustomerSignIn")
            .WithSummary("Sign in (ACC-01, doc 11 P11 step B).")
            .WithDescription(
                "One 401 for a wrong password, an unknown address, a staff address and a deactivated "
                + "account alike, and each costs the same password-hash work (doc 08 §3.1 DB10, #101). "
                + "423 with the end time once an account is locked after "
                + "`CustomerSecurity:LockoutMaxFailedAttempts` failures. On success the guest bag is "
                + "merged into the account bag (doc 11 P11 step B.6) and the session cookie is issued.")
            .Produces<CustomerSessionResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status423Locked)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        api.MapPost("/logout", (Delegate)SignOutEndpointAsync)
            .RequireAuthorization(CustomerAuthorization.PolicyName)
            .WithName("CustomerSignOut")
            .WithSummary("Sign out (ACC-01, doc 11 P11 step B.7).")
            .WithDescription(
                "Removes the session cookie. **The browser's guest bag is not restored** — doc 11 P11 "
                + "step B.7 says so, and the guest cart was deleted by the merge at sign-in.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        api.MapGet("/me", WhoAmIAsync)
            .RequireAuthorization(CustomerAuthorization.PolicyName)
            .WithName("CustomerWhoAmI")
            .WithSummary("The signed-in customer, read fresh from the database.")
            .WithDescription(
                "Answers 401 when there is no session. The storefront shell calls this once per load to "
                + "decide whether to greet the customer and whether to show the \"confirm your email\" "
                + "banner (doc 11 §7 G-24). Read fresh rather than from the cookie's claims, so a "
                + "customer who has just confirmed in another tab is not told to confirm again.")
            .Produces<CustomerSessionResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        api.MapPost("/confirm-email", ConfirmEmailAsync)
            .WithName("CustomerConfirmEmail")
            .WithSummary("Redeem a confirmation link (ACC-01, doc 11 P11 step A.4).")
            .WithDescription(
                "Confirms the address the token was minted for and links the guest orders placed with "
                + "**that** address which have no account yet (doc 08 §3.1 D5), in one transaction, with "
                + "the linked order ids written to `ops.AuditLogs` as `LinkGuestOrders`. The response "
                + "names the orders, because \"all 3 appear in My account\" is the thing worth telling "
                + "the customer. A link that is already used, expired, wrong, or for an address the "
                + "account no longer uses all answer **400 with one opaque sentence** — saying \"expired\" "
                + "rather than \"wrong\" would tell a holder it was once real. **A session is issued on "
                + "success**, because the holder of a live confirmation link for an account is that "
                + "account's owner, and asking them to sign in immediately afterwards is a step with no "
                + "security value.")
            .Produces<CustomerEmailConfirmedResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        api.MapPost("/resend-confirmation", ResendConfirmationAsync)
            .RequireAuthorization(CustomerAuthorization.PolicyName)
            .WithName("CustomerResendConfirmation")
            .WithSummary("Ask for a new confirmation link.")
            .WithDescription(
                "Mints a fresh single-use link, which **invalidates any link already in the inbox** — "
                + "one account has one pending token, so the newest message is always the one that "
                + "works. Answers the same 202 whether anything was queued: nothing is when the address "
                + "is already confirmed or when the shop has no configured storefront origin, and "
                + "neither is something a shopper can act on.")
            .Produces<AccountMessageResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        api.MapPost("/forgot-password", ForgotPasswordAsync)
            .WithName("CustomerForgotPassword")
            .WithSummary("Ask for a password-reset link (ACC-01, doc 11 P11 step C.8).")
            .WithDescription(
                "**One sentence and 202 for every address** (doc 08 §3.1 DB10). Only an active customer "
                + "match mints a token and queues a message; a staff address and an unknown address send "
                + "nothing. A badly-shaped address is accepted and ignored rather than refused: "
                + "\"that is not a valid address\" tells a caller which strings are worth trying.")
            .Produces<AccountMessageResponse>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        api.MapPost("/reset-password", ResetPasswordAsync)
            .WithName("CustomerResetPassword")
            .WithSummary("Redeem a reset link and set a new password (ACC-01, doc 11 P11 step C.9).")
            .WithDescription(
                "Spends the token, sets the password and bumps the security stamp, which signs every "
                + "other browser out. **No session is issued**: the link was opened by whatever browser "
                + "read the email, and the cost of asking for one sign-in is smaller than the cost of "
                + "handing a session to a mailbox reader. A refused password does **not** spend the "
                + "token, so a shopper who typed a short one can try again with the same link.")
            .Produces<AccountMessageResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        api.MapPost("/change-password", ChangePasswordAsync)
            .RequireAuthorization(CustomerAuthorization.PolicyName)
            .WithName("CustomerChangePassword")
            .WithSummary("Change your own password (ACC-02, doc 11 P11 step 10).")
            .WithDescription(
                "A wrong current password counts towards the same lockout as a wrong sign-in (#146 "
                + "extended its guard to change-password for exactly this reason: an endpoint that "
                + "verifies a password is a password oracle whether or not it is called login). On "
                + "success the stamp is bumped and **this** browser's cookie is reissued with the new "
                + "one, so the caller stays signed in and every other browser does not.")
            .Produces<CustomerSessionResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status423Locked);

        return endpoints;
    }

    private static async Task<Results<Accepted<AccountMessageResponse>, ValidationProblem, ProblemHttpResult>>
        RegisterAsync(
            [FromBody] CustomerRegisterRequest? request,
            ICommandHandler<RegisterCustomerCommand, Result<RegisterCustomerResult>> handler,
            AccountRequestThrottle throttle,
            HttpContext httpContext,
            CancellationToken cancellationToken)
    {
        CustomerSessionCookie.DoNotStore(httpContext.Response);

        if (!throttle.TryAcquire(AccountRequestKind.Register, ClientAddress(httpContext)))
        {
            return TooMany(httpContext, "Too many accounts have been created from here recently.");
        }

        var result = await handler.HandleAsync(
            new RegisterCustomerCommand(
                request?.FirstName,
                request?.LastName,
                request?.Email,
                request?.Password,
                request?.Phone,
                ClientAddress(httpContext),
                httpContext.Request.Headers.UserAgent.ToString()),
            cancellationToken);

        return result.IsSuccess
            ? TypedResults.Accepted((string?)null, new AccountMessageResponse(RegistrationReply))
            : ToValidationProblem(result.Errors);
    }

    private static async Task<IResult> SignInAsync(
        [FromBody] CustomerSignInRequest? request,
        ICommandHandler<CustomerSignInCommand, Result<CustomerSignInResult>> handler,
        ICartStore carts,
        AccountRequestThrottle throttle,
        HttpContext httpContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        CustomerSessionCookie.DoNotStore(httpContext.Response);

        if (!throttle.TryAcquire(AccountRequestKind.SignIn, ClientAddress(httpContext)))
        {
            return TooMany(httpContext, "Too many sign-in attempts have been made from here recently.");
        }

        var result = await handler.HandleAsync(
            new CustomerSignInCommand(
                request?.Email,
                request?.Password,
                ClientAddress(httpContext),
                httpContext.Request.Headers.UserAgent.ToString()),
            cancellationToken);

        if (!result.IsSuccess)
        {
            return ToValidationProblem(result.Errors);
        }

        switch (result.Value)
        {
            case CustomerSignInResult.Success success:
                await CustomerSessionCookie.SignInAsync(httpContext, success.Session);
                await MergeGuestBagAsync(httpContext, carts, success.Session.UserId, clock, cancellationToken);
                return TypedResults.Ok(CustomerSessionResponse.From(success.Session));

            case CustomerSignInResult.LockedOut lockedOut:
                return TypedResults.Problem(
                    title: "This account is temporarily locked",
                    detail:
                        "Too many sign-in attempts. Try again in a few minutes — the lock clears by "
                        + "itself.",
                    statusCode: StatusCodes.Status423Locked,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = "locked_out",
                        ["lockoutEndUtc"] = lockedOut.LockoutEndUtc.ToString("o", CultureInfo.InvariantCulture),
                    });

            default:
                return TypedResults.Problem(
                    title: "We could not sign you in",
                    detail: "That email address and password do not match an account.",
                    statusCode: StatusCodes.Status401Unauthorized,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = "invalid_credentials",
                    });
        }
    }

    /// <summary>
    /// Doc 11 P11 step B.7: the cookie is removed and the guest bag is not restored.
    /// </summary>
    /// <remarks>
    /// <b>Cast to <c>Delegate</c> at the map site, and that is not cosmetic.</b> A method whose only
    /// parameter is <see cref="HttpContext"/> has the shape of a <c>RequestDelegate</c>, so minimal APIs
    /// bind it as one — and a <c>RequestDelegate</c>'s return value is <i>discarded</i>, which would make
    /// this endpoint answer 200 with an empty body instead of 204 and would silently drop the
    /// <c>Set-Cookie</c> deletion if that ever moved into the result. <c>ASP0016</c> catches it; the cast
    /// is the fix the analyser prescribes.
    /// </remarks>
    private static async Task<NoContent> SignOutEndpointAsync(HttpContext httpContext)
    {
        CustomerSessionCookie.DoNotStore(httpContext.Response);
        await CustomerSessionCookie.SignOutAsync(httpContext);

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<CustomerSessionResponse>, UnauthorizedHttpResult>> WhoAmIAsync(
        IQueryHandler<GetCustomerProfileQuery, CustomerProfile?> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        CustomerSessionCookie.DoNotStore(httpContext.Response);

        if (!CustomerSessionCookie.TryGetUserId(httpContext.User, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var profile = await handler.HandleAsync(new GetCustomerProfileQuery(userId), cancellationToken);

        // Null means the session names an account that is no longer an active customer. The cookie's own
        // revalidation ends such a session within its interval; answering 401 here rather than 200 with
        // an empty body means the shell does not have to guess what an empty answer meant.
        return profile is null
            ? TypedResults.Unauthorized()
            : TypedResults.Ok(new CustomerSessionResponse(
                profile.UserId,
                profile.Email,
                profile.FirstName,
                profile.LastName,
                profile.EmailConfirmed));
    }

    private static async Task<IResult> ConfirmEmailAsync(
        [FromBody] CustomerTokenRequest? request,
        ICommandHandler<ConfirmCustomerEmailCommand, CustomerEmailConfirmationResult> handler,
        AccountRequestThrottle throttle,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        CustomerSessionCookie.DoNotStore(httpContext.Response);

        if (!throttle.TryAcquire(AccountRequestKind.RedeemToken, ClientAddress(httpContext)))
        {
            return TooMany(httpContext, "Too many links have been opened from here recently.");
        }

        var result = await handler.HandleAsync(
            new ConfirmCustomerEmailCommand(request?.Token),
            cancellationToken);

        if (result is not CustomerEmailConfirmationResult.Confirmed confirmed)
        {
            return InvalidLink("We could not confirm your email address");
        }

        await CustomerSessionCookie.SignInAsync(httpContext, confirmed.Session);

        return TypedResults.Ok(new CustomerEmailConfirmedResponse(
            CustomerSessionResponse.From(confirmed.Session),
            confirmed.LinkedOrderNumbers));
    }

    private static async Task<Results<Accepted<AccountMessageResponse>, ProblemHttpResult, UnauthorizedHttpResult>>
        ResendConfirmationAsync(
            ICommandHandler<ResendCustomerConfirmationCommand, bool> handler,
            AccountRequestThrottle throttle,
            HttpContext httpContext,
            CancellationToken cancellationToken)
    {
        CustomerSessionCookie.DoNotStore(httpContext.Response);

        if (!CustomerSessionCookie.TryGetUserId(httpContext.User, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        if (!throttle.TryAcquire(AccountRequestKind.ResendConfirmation, ClientAddress(httpContext)))
        {
            return TooMany(httpContext, "Too many links have been asked for from here recently.");
        }

        // The answer does not depend on this: see the endpoint's description and the handler's remarks.
        _ = await handler.HandleAsync(new ResendCustomerConfirmationCommand(userId), cancellationToken);

        return TypedResults.Accepted(
            (string?)null,
            new AccountMessageResponse(
                "We have queued a new confirmation link to your email address. Only the newest link "
                + "works."));
    }

    private static async Task<Results<Accepted<AccountMessageResponse>, ValidationProblem, ProblemHttpResult>>
        ForgotPasswordAsync(
            [FromBody] CustomerForgotPasswordRequest? request,
            ICommandHandler<RequestCustomerPasswordResetCommand, Result<bool>> handler,
            AccountRequestThrottle throttle,
            HttpContext httpContext,
            CancellationToken cancellationToken)
    {
        CustomerSessionCookie.DoNotStore(httpContext.Response);

        if (!throttle.TryAcquire(AccountRequestKind.ForgotPassword, ClientAddress(httpContext)))
        {
            return TooMany(httpContext, "Too many reset links have been asked for from here recently.");
        }

        var result = await handler.HandleAsync(
            new RequestCustomerPasswordResetCommand(request?.Email),
            cancellationToken);

        return result.IsSuccess
            ? TypedResults.Accepted((string?)null, new AccountMessageResponse(ForgotPasswordReply))
            : ToValidationProblem(result.Errors);
    }

    private static async Task<IResult> ResetPasswordAsync(
        [FromBody] CustomerResetPasswordRequest? request,
        ICommandHandler<ResetCustomerPasswordCommand, Result<CustomerPasswordResetResult>> handler,
        AccountRequestThrottle throttle,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        CustomerSessionCookie.DoNotStore(httpContext.Response);

        if (!throttle.TryAcquire(AccountRequestKind.RedeemToken, ClientAddress(httpContext)))
        {
            return TooMany(httpContext, "Too many links have been opened from here recently.");
        }

        var result = await handler.HandleAsync(
            new ResetCustomerPasswordCommand(request?.Token, request?.Password),
            cancellationToken);

        if (!result.IsSuccess)
        {
            return ToValidationProblem(result.Errors);
        }

        return result.Value switch
        {
            CustomerPasswordResetResult.Success => TypedResults.Ok(new AccountMessageResponse(
                "Your password is set. Sign in with it, and any other browser that was signed in has "
                + "been signed out.")),

            CustomerPasswordResetResult.WeakPassword weak => TypedResults.ValidationProblem(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["password"] = [.. weak.Problems],
                },
                title: "That password cannot be used"),

            _ => InvalidLink("That link cannot be used"),
        };
    }

    private static async Task<IResult> ChangePasswordAsync(
        [FromBody] CustomerChangePasswordRequest? request,
        ICommandHandler<ChangeCustomerPasswordCommand, Result<CustomerPasswordChangeResult>> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        CustomerSessionCookie.DoNotStore(httpContext.Response);

        if (!CustomerSessionCookie.TryGetUserId(httpContext.User, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var result = await handler.HandleAsync(
            new ChangeCustomerPasswordCommand(
                userId,
                request?.CurrentPassword,
                request?.NewPassword,
                ClientAddress(httpContext),
                httpContext.Request.Headers.UserAgent.ToString()),
            cancellationToken);

        if (!result.IsSuccess)
        {
            return ToValidationProblem(result.Errors);
        }

        switch (result.Value)
        {
            case CustomerPasswordChangeResult.Success success:
                // The stamp has moved, so this browser's cookie has to be reissued with the new one or
                // the caller signs *themselves* out at the next revalidation. Every other browser does,
                // which is the point.
                await CustomerSessionCookie.SignInAsync(httpContext, success.Session);
                return TypedResults.Ok(CustomerSessionResponse.From(success.Session));

            case CustomerPasswordChangeResult.WeakPassword weak:
                return TypedResults.ValidationProblem(
                    new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        ["newPassword"] = [.. weak.Problems],
                    },
                    title: "That password cannot be used");

            case CustomerPasswordChangeResult.LockedOut lockedOut:
                return TypedResults.Problem(
                    title: "This account is temporarily locked",
                    detail: "Too many wrong passwords. Try again in a few minutes.",
                    statusCode: StatusCodes.Status423Locked,
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = "locked_out",
                        ["lockoutEndUtc"] = lockedOut.LockoutEndUtc.ToString("o", CultureInfo.InvariantCulture),
                    });

            default:
                return TypedResults.ValidationProblem(
                    new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        ["currentPassword"] = ["That is not your current password."],
                    },
                    title: "We could not change your password");
        }
    }

    /// <summary>
    /// Doc 11 P11 step B.6: "merges the guest bag into the account bag (P5)".
    /// </summary>
    /// <remarks>
    /// <b>Only the bag.</b> The same step also says "and the browser wishlist into the account wishlist
    /// (P4)", and that half is <b>not built</b>: a guest wishlist lives in the browser (doc 08 §1.7), so
    /// the merge needs the storefront to post its list and needs slugs resolved to product and colour
    /// ids — a read and a write this task has no port for. <c>WishlistItem</c>'s own remark names W3-01 as
    /// its owner, and the task report says it is outstanding. WISH-01 is not in this task's scope line.
    /// <para>
    /// <b>A failure here does not fail the sign-in.</b> The customer is signed in by the time this runs,
    /// and losing a guest bag is a smaller harm than a 500 on a sign-in that actually succeeded — the
    /// cookie is already in the response. The merge is one transaction of its own (<c>ICartStore</c>), so
    /// a failure leaves the guest cart intact rather than losing the lines.
    /// </para>
    /// <para>
    /// <b>The guest cookie is cleared either way.</b> <c>CartCookieAction.Clear</c>'s own remark: a
    /// signed-in customer uses their account cart and the guest token must not be left lying in the
    /// browser. Doc 11 P11 step B.7 then says signing out does not restore it.
    /// </para>
    /// </remarks>
    private static async Task MergeGuestBagAsync(
        HttpContext httpContext,
        ICartStore carts,
        int customerUserId,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var guestToken = CartCookie.Read(httpContext.Request);

        if (guestToken is { } token)
        {
            await carts.MergeGuestIntoCustomerAsync(token, customerUserId, cancellationToken);
        }

        CartCookie.Apply(httpContext.Response, CartCookieAction.Clear, guestToken, clock);
    }

    /// <summary>
    /// The visitor's address: the socket peer, corrected from <c>X-Forwarded-For</c> only when that peer
    /// is the trusted web gateway (<c>ForwardedHeadersSetup</c>, W0-15).
    /// </summary>
    /// <remarks>
    /// A caller cannot write somebody else's address into a rate-limit partition or an audit row by
    /// sending the header themselves. That is the hole W1-01 round 3 closed, and it matters more here
    /// than anywhere: a forgeable partition key is no rate limit at all.
    /// </remarks>
    private static string? ClientAddress(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString();

    private static ProblemHttpResult TooMany(HttpContext httpContext, string detail)
    {
        httpContext.Response.Headers.RetryAfter =
            ((int)AccountRequestThrottle.Window.TotalSeconds).ToString(CultureInfo.InvariantCulture);

        return TypedResults.Problem(
            title: "Too many attempts",
            detail: detail + " Please try again shortly.",
            statusCode: StatusCodes.Status429TooManyRequests,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "rate_limited",
            });
    }

    /// <summary>
    /// The one opaque refusal for every bad or spent link.
    /// </summary>
    /// <remarks>
    /// <b>The project's convention for the guest order link, applied to these.</b> One sentence for every
    /// refusal: already used, expired, wrong, or for an address the account no longer uses. Saying
    /// "expired" rather than "wrong" would tell a holder the token was once real.
    /// </remarks>
    private static ProblemHttpResult InvalidLink(string title) =>
        TypedResults.Problem(
            title: title,
            detail:
                "This link has already been used, or it has expired. Ask for a new one and use the "
                + "newest message.",
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "invalid_link",
            });

    private static ValidationProblem ToValidationProblem(IReadOnlyList<ValidationError> errors) =>
        TypedResults.ValidationProblem(
            errors
                .GroupBy(error => error.Field, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.Message).ToArray(),
                    StringComparer.Ordinal));
}
