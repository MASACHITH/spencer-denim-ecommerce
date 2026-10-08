using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Sales.Orders.Abstractions;
using SpencerDenim.Application.Sales.Orders.Contracts;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Orders;

/// <summary>
/// The <c>Store.*</c> legal keys and <c>Orders.AutoDeliverAfterDays</c>, read from <c>ops.Settings</c>
/// (doc 08 §3.8).
/// <para>
/// <b>Five keys by name, in the shape <c>BankTransferSettingsReader</c> established</b>, so a key added to
/// the table tomorrow is not merely filtered out but never selected — and so a <c>Secret</c> row can never
/// be published through here. None of these is secret and the <c>IsSecret</c> clause is present anyway, for
/// the reason that reader records: if one were ever flipped, the value would otherwise be published with
/// nothing standing in the way.
/// </para>
/// <para>
/// <b>Not cached.</b> It is read when a staff member prints an invoice and once a day by the auto-deliver
/// job, so the query is rare rather than per-request — and a client who corrects a mistyped registration
/// number must not have the old one printed on invoices for another minute.
/// </para>
/// </summary>
internal sealed class OrderSettingsReader(AppDbContext dbContext) : IOrderSettingsReader
{
    private static readonly string[] SellerKeys =
    [
        SettingKeys.Store.LegalName,
        SettingKeys.Store.Address,
        SettingKeys.Store.BRNumber,
        SettingKeys.Store.VatNumber,
    ];

    public async Task<InvoiceSellerDetails> ReadInvoiceSellerAsync(CancellationToken cancellationToken)
    {
        var values = await ReadAsync(SellerKeys, cancellationToken);

        var legalName = Clean(values.GetValueOrDefault(SettingKeys.Store.LegalName));
        var addressLines = SplitLines(values.GetValueOrDefault(SettingKeys.Store.Address));

        return new InvoiceSellerDetails(
            legalName,
            addressLines,
            Clean(values.GetValueOrDefault(SettingKeys.Store.BRNumber)),
            Clean(values.GetValueOrDefault(SettingKeys.Store.VatNumber)),

            // "Complete" means the two a printed invoice is unusable without. The registration numbers are
            // not in the test: a company that has no VAT registration has nothing to print there, and
            // treating that as incomplete would show a warning nobody could ever clear.
            legalName is not null && addressLines.Count > 0);
    }

    public async Task<int?> ReadAutoDeliverAfterDaysAsync(CancellationToken cancellationToken)
    {
        var values = await ReadAsync([SettingKeys.Orders.AutoDeliverAfterDays], cancellationToken);

        var raw = Clean(values.GetValueOrDefault(SettingKeys.Orders.AutoDeliverAfterDays));

        // **Null rather than a hard-coded fallback.** The key is seeded with doc 08 §3.8's 7, so reaching
        // here with nothing means the row was deleted or holds something that is not a number — and a job
        // that quietly assumed 7 would be moving orders to Delivered on a figure no screen could show.
        // IOrderSettingsReader's own remarks say what the caller does instead.
        if (raw is null
            || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
        {
            return null;
        }

        // Out of range is also null. A 0 would mean "mark it delivered the moment it ships", which is
        // exactly the "Verified buyer" badge defect G-18 is about; a negative would mark everything
        // delivered on the next pass. Clamping instead would silently honour a value the staff member can
        // see is wrong on the settings screen.
        return days is >= SettingKeys.Orders.AutoDeliverAfterDaysMin
            and <= SettingKeys.Orders.AutoDeliverAfterDaysMax
            ? days
            : null;
    }

    private async Task<Dictionary<string, string?>> ReadAsync(
        string[] keys,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Settings
            .AsNoTracking()
            .Where(setting => keys.Contains(setting.Key) && !setting.IsSecret)
            .Select(setting => new { setting.Key, setting.Value })
            .ToListAsync(cancellationToken);

        // Case-insensitively, to match Latin1_General_100_CI_AS_SC: a row scripted in as
        // `store.legalname` is the same row to SQL Server, and an ordinal dictionary here would report no
        // legal details at all while the settings screen showed them filled in. SettingCatalog.ByKey
        // records in full what that costs.
        return rows.ToDictionary(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Splits a multi-line settings value into its lines, dropping blank ones.
    /// </summary>
    /// <remarks>
    /// The same treatment <c>Contact.Address</c> gets on the storefront: the value is plain text and the
    /// renderer interpolates each line, so there is no markup surface. Blank lines are dropped because a
    /// trailing newline in a textarea is the normal way a value ends and an empty line on an invoice reads
    /// as a missing field.
    /// </remarks>
    private static List<string> SplitLines(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

    /// <summary>
    /// Trims, and treats whitespace as absent — <c>" "</c> in a settings row is a client who cleared the
    /// box, not a registered name made of a space.
    /// </summary>
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
