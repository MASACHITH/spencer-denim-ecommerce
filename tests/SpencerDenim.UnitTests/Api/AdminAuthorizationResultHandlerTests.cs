using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Infrastructure.Identity.Authorization;

namespace SpencerDenim.UnitTests.Api;

/// <summary>
/// The account gates' refusals become problem-details codes the admin app acts on. When both gates refuse, the
/// forced password change is reported first (doc 11 P14 step 3), then 2FA enrolment (W1-10).
/// </summary>
public sealed class AdminAuthorizationResultHandlerTests
{
    private sealed class StubHandler : IAuthorizationHandler
    {
        public Task HandleAsync(AuthorizationHandlerContext context) => Task.CompletedTask;
    }

    private static readonly StubHandler Handler = new();

    private static async Task<(int Status, string? Error)> RunAsync(params AuthorizationFailureReason[] reasons)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        httpContext.Response.Body = new MemoryStream();

        var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        var result = PolicyAuthorizationResult.Forbid(AuthorizationFailure.Failed(reasons));

        await new AdminAuthorizationResultHandler().HandleAsync(_ => Task.CompletedTask, httpContext, policy, result);

        httpContext.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(httpContext.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        return (httpContext.Response.StatusCode, body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_missing_enrolment_is_a_403_with_its_own_code()
    {
        var (status, error) = await RunAsync(new TwoFactorEnrolmentRequiredFailureReason(Handler));

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal(TwoFactorEnrolmentRequiredFailureReason.Code, error);
    }

    [Fact]
    public async Task When_both_gates_refuse_the_password_change_is_reported()
    {
        var (status, error) = await RunAsync(
            new TwoFactorEnrolmentRequiredFailureReason(Handler),
            new PasswordChangeRequiredFailureReason(Handler));

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal(PasswordChangeRequiredFailureReason.Code, error);
    }
}
