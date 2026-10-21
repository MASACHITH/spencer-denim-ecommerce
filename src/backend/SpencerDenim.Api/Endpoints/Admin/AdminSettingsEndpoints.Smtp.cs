using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Settings;
using SpencerDenim.Application.Settings.Admin;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The email account panel (doc 08 §3.8 <c>Email.Smtp</c>; ADM-22, 2026-10-21): <c>PUT /api/admin/settings/email-smtp</c>
/// and <c>POST /api/admin/settings/email-smtp/test</c>.
/// <para>
/// <b><c>Settings.Security</c> on both</b>, which only the GrantsAll role holds (<c>CK_RoleClaims_NoSecurity</c>), so
/// only a Super Admin reaches either. The read is the shared <c>GET /api/admin/settings</c>, which filters each row by
/// its own permission and <b>never returns the password</b> — only that one is set.
/// </para>
/// </summary>
internal static partial class AdminSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAdminSmtpSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/settings").WithTags("Admin Settings");

        api.MapPut("/email-smtp", UpdateSmtpAsync)
            .RequirePermission(Permissions.Settings.Security)
            .WithName("AdminUpdateSmtpSettings")
            .WithSummary("The mail account every email is sent through (doc 08 §3.8 Email.Smtp).")
            .WithDescription(
                "Settings.Security only (Super Admin). Text fields host, port, username, password, fromAddress, "
                + "fromName, and useStartTls as true or false. A field the body leaves out is not written; null or an "
                + "empty string clears it. password is write-only: never returned, and empty or missing keeps the "
                + "saved one. A server needs a port and a send-from address, and a user name is refused with "
                + "encryption off. The saved account is used from the next send when it is complete and valid; "
                + "otherwise the server's Email__Smtp__* configuration is used. Every change is audited (the password "
                + "as [secret changed]), raises a SensitiveSettingsChanged banner, and emails every Super Admin "
                + "through both the new and the previous account.")
            .Accepts<UpdateSmtpSettingsRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        api.MapPost("/email-smtp/test", TestSmtpAsync)
            .RequirePermission(Permissions.Settings.Security)
            .WithName("AdminTestSmtpSettings")
            .WithSummary("Send a test email to yourself through the account as entered, without saving it.")
            .WithDescription(
                "Settings.Security only. The same fields as the save; rowVersion is not needed and nothing is "
                + "written. An empty password uses the saved one. The message goes to the signed-in user's own "
                + "address. 200 { sentTo } when the server accepted it; 422 with a plain sentence when it did not. "
                + "The server's own reply is never passed back.")
            .Accepts<UpdateSmtpSettingsRequest>("application/json")
            .Produces<SmtpTestResponse>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> UpdateSmtpAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateSmtpSettingsCommand, SettingsWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<JsonObject>(httpContext.Request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (ReadSmtpFields(body!, out var fields, out var useStartTls) is { } bad)
        {
            return bad;
        }

        if (ReadString(body!, "rowVersion", out var rowVersion, out _) is { } badVersion)
        {
            return badVersion;
        }

        var result = await handler.HandleAsync(
            new UpdateSmtpSettingsCommand(
                fields,
                useStartTls,
                AdminWriteRequest.RowVersion(httpContext.Request, rowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result);
    }

    private static async Task<IResult> TestSmtpAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<SendSmtpTestEmailCommand, SmtpTestResult> handler,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<JsonObject>(httpContext.Request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (ReadSmtpFields(body!, out var fields, out var useStartTls) is { } bad)
        {
            return bad;
        }

        var actor = AdminWriteRequest.Actor(httpContext);
        var result = await handler.HandleAsync(
            new SendSmtpTestEmailCommand(fields, useStartTls, actor),
            cancellationToken);

        // The user id, the outcome, the exception's type and the SMTP status: never a field of the form, never the
        // server's reply, never the password.
        var logger = loggers.CreateLogger("SpencerDenim.Api.Endpoints.Admin.SmtpTest");

        switch (result)
        {
            case SmtpTestResult.Sent:
                LogSmtpTestSent(logger, actor.UserId);
                break;
            case SmtpTestResult.Failed failed:
                LogSmtpTestFailed(logger, actor.UserId, failed.FailureType, failed.SmtpStatus);
                break;
        }

        return result switch
        {
            SmtpTestResult.Sent sent => TypedResults.Ok(new SmtpTestResponse(sent.ToEmail)),
            SmtpTestResult.Invalid invalid => ValidationProblem(invalid.Errors),
            SmtpTestResult.Forbidden => TypedResults.Problem(
                title: "You cannot use this form",
                detail: "Only a Super Admin can test the email account.",
                statusCode: StatusCodes.Status403Forbidden),
            SmtpTestResult.Failed failed => AdminWriteRequest.Problem(
                StatusCodes.Status422UnprocessableEntity,
                "The test email was not sent",
                failed.Message,
                "smtp_test_failed"),
            _ => throw new InvalidOperationException($"Unhandled test result {result.GetType().Name}."),
        };
    }

    /// <summary>
    /// Reads the text fields (presence-aware, as every settings write) and the switch. A non-string text field and a
    /// non-boolean switch are named field errors.
    /// </summary>
    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem? ReadSmtpFields(
        JsonObject body,
        out Dictionary<string, string?> fields,
        out bool? useStartTls)
    {
        fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        useStartTls = null;

        foreach (var field in UpdateSmtpSettingsCommandHandler.TextFields)
        {
            if (ReadString(body, field, out var value, out var present) is { } badField)
            {
                return badField;
            }

            if (present)
            {
                fields[field] = value;
            }
        }

        const string switchField = UpdateSmtpSettingsCommandHandler.UseStartTlsField;

        if (body.ContainsKey(switchField))
        {
            if (AdminJsonBody.ReadBool(body, switchField, out useStartTls) is { } badFlag)
            {
                return badFlag;
            }

            if (useStartTls is null)
            {
                return ValidationProblem([new ValidationError(switchField, "Choose on or off; this cannot be left empty.")]);
            }
        }

        return null;
    }

    [LoggerMessage(
        EventId = 3420,
        Level = LogLevel.Information,
        Message = "Staff user {UserId} sent a test email through the email account form.")]
    private static partial void LogSmtpTestSent(ILogger logger, int? userId);

    [LoggerMessage(
        EventId = 3421,
        Level = LogLevel.Warning,
        Message = "Staff user {UserId}'s test email failed ({FailureType}, SMTP status {SmtpStatus}). They were shown a "
            + "fixed sentence; the server's own reply is not recorded.")]
    private static partial void LogSmtpTestFailed(ILogger logger, int? userId, string? failureType, int? smtpStatus);
}

/// <summary>For the OpenAPI document only: the body is read as a <c>JsonObject</c>, so absent can be told from null.</summary>
/// <param name="Host">The mail server's name.</param>
/// <param name="Port">The port, as digits, 1 to 65535.</param>
/// <param name="Username">The account's user name.</param>
/// <param name="Password">Write-only. Empty or missing keeps the saved one.</param>
/// <param name="FromAddress">The address emails are sent from.</param>
/// <param name="FromName">The name shown beside it.</param>
/// <param name="UseStartTls">Encrypt the connection with STARTTLS.</param>
/// <param name="RowVersion">The group's token from the read model (save only).</param>
public sealed record UpdateSmtpSettingsRequest(
    string? Host,
    string? Port,
    string? Username,
    string? Password,
    string? FromAddress,
    string? FromName,
    bool? UseStartTls,
    string? RowVersion);

/// <summary>The test was accepted by the server for this address — the signed-in user's own.</summary>
/// <param name="SentTo">Where the test went.</param>
public sealed record SmtpTestResponse(string SentTo);
