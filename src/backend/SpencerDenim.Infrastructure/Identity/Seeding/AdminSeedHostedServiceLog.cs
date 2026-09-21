using Microsoft.Extensions.Logging;

namespace SpencerDenim.Infrastructure.Identity.Seeding;

/// <summary>Source-generated log messages for <see cref="AdminSeedHostedService"/> (CA1848).</summary>
internal static partial class AdminSeedHostedServiceLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Admin seeding skipped: {Count} active Super Admin account(s) already exist.")]
    public static partial void SeedingSkippedExistingHolders(this ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Seed:Admin:Enabled is true but Seed:Admin:Email / Seed:Admin:Password are not both set; no admin was seeded.")]
    public static partial void SeedingSkippedMissingCredentials(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to seed the initial Super Admin ({Email}): {Errors}")]
    public static partial void SeedingFailed(this ILogger logger, string email, string errors);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded the initial Super Admin account ({Email}).")]
    public static partial void SeededAdmin(this ILogger logger, string email);
}
