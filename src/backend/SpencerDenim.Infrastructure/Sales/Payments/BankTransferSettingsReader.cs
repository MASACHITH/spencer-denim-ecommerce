using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Payments;

/// <summary>
/// The five <c>Bank.*</c> rows of <c>ops.Settings</c>, in one query (doc 08 §3.8, doc 11 P7).
/// <para>
/// <b>Five keys by name, in the shape <c>SiteSettingsReader</c> established</b>, so a key added to the
/// table tomorrow is not merely filtered out but never selected — and so a <c>Secret</c> row can never be
/// published through a customer-facing path. None of the <c>Bank.*</c> keys is secret (an account number
/// is printed on every order page by design), and the <c>IsSecret</c> clause is here anyway for the reason
/// <c>SiteSettingsReader</c>'s round-1 review gave: if one of these keys were ever flipped, the value
/// would otherwise be published with nothing standing in the way.
/// </para>
/// <para>
/// <b>Not cached.</b> Doc 08 §3.8 says options classes are cached for 60 s, and this one deliberately is
/// not: it is read when a shopper opens one order page, so the query is rare rather than per-request, and
/// the failure mode a cache would add is the expensive direction. A client who corrects a mistyped account
/// number must not have it served to shoppers for another minute — <c>SiteSettingsCache</c>'s own remarks
/// make the same argument about a logo, where being wrong only looks bad.
/// </para>
/// </summary>
internal sealed class BankTransferSettingsReader(AppDbContext dbContext) : IBankTransferSettingsReader
{
    private static readonly string[] Keys =
    [
        SettingKeys.Bank.BankName,
        SettingKeys.Bank.Branch,
        SettingKeys.Bank.AccountName,
        SettingKeys.Bank.AccountNumber,
        SettingKeys.Bank.Instructions,
    ];

    public async Task<BankTransferDetails?> ReadAsync(CancellationToken cancellationToken)
    {
        var rows = await dbContext.Settings
            .AsNoTracking()
            .Where(setting => Keys.Contains(setting.Key) && !setting.IsSecret)
            .Select(setting => new { setting.Key, setting.Value })
            .ToListAsync(cancellationToken);

        // Case-insensitively, to match Latin1_General_100_CI_AS_SC: a row scripted in as `bank.accountnumber`
        // is the same row to SQL Server, and an ordinal dictionary here would report no bank details at all
        // while the admin screen showed them filled in. SettingCatalog.ByKey records what that costs.
        var values = rows.ToDictionary(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase);

        return BankTransferDetails.TryCreate(
            values.GetValueOrDefault(SettingKeys.Bank.BankName),
            values.GetValueOrDefault(SettingKeys.Bank.AccountName),
            values.GetValueOrDefault(SettingKeys.Bank.AccountNumber),
            values.GetValueOrDefault(SettingKeys.Bank.Branch),
            values.GetValueOrDefault(SettingKeys.Bank.Instructions));
    }
}
