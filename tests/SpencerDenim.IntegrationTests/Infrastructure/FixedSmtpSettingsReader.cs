using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Ops.Email.Abstractions;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// An account reader that always answers with one fixed account, for a test that drives <c>SendEmails</c> against a
/// capturing transport and is not about where the account comes from. The address is on a reserved test domain and
/// the transport it is used with never connects.
/// </summary>
internal sealed class FixedSmtpSettingsReader(SmtpSettings account) : ISmtpSettingsReader
{
    /// <summary>A placeholder account on RFC 2606's reserved <c>.invalid</c> domain.</summary>
    public static SmtpSettings TestAccount { get; } =
        new("smtp.test.invalid", 587, null, null, "shop@test.invalid", null, true);

    public Task<EffectiveSmtpSettings> ReadEffectiveAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new EffectiveSmtpSettings(account, SmtpSettingsSource.Configuration));

    public Task<string?> ReadStoredPasswordAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}
