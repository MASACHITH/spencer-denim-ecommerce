using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Catalog.Reviews;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Catalog.Reviews;

/// <summary>
/// <c>Reviews.RequireApproval</c>, read from <c>ops.Settings</c> on every submission (no cache, so a change on the
/// Settings screen applies to the next review written).
/// </summary>
/// <remarks>
/// <b>Anything other than a readable <c>false</c> means "wait for approval".</b> A missing row (a database the
/// seeder has not reached), an empty one, or a value typed in by hand that is not true or false all fall back to
/// doc 08 §3.8's default, because a review that waits for a moderator is a recoverable mistake and one published
/// unseen is not.
/// </remarks>
internal sealed class ReviewSettingsReader(AppDbContext dbContext) : IReviewSettingsReader
{
    public async Task<bool> RequireApprovalAsync(CancellationToken cancellationToken)
    {
        var value = await dbContext.Settings
            .AsNoTracking()
            .Where(setting => setting.Key == SettingKeys.Reviews.RequireApproval && !setting.IsSecret)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return Interpret(value);
    }

    /// <summary>The rule above, on its own so it can be tested without a database.</summary>
    internal static bool Interpret(string? value) =>
        !bool.TryParse(value?.Trim(), out var requireApproval) || requireApproval;
}
