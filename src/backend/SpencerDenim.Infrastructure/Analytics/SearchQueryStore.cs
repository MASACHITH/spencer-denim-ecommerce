using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Catalog.Abstractions;
using SpencerDenim.Application.Catalog.Products;
using SpencerDenim.Application.Catalog.Search;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Analytics;

/// <summary>
/// Writes one flush of search counts into <c>analytics.SearchQueriesDaily</c> with doc 08 DB25's race-safe upsert.
/// <para>
/// <b>The upsert, exactly as DB25 specifies it</b> (<see cref="UpsertSql"/>): <c>UPDATE … SET Searches += @n</c>; if
/// that touched no row, <c>INSERT</c>; if the insert loses a race to another writer (2627/2601, a duplicate key),
/// run the <c>UPDATE</c> again. Each statement is its own autocommit transaction at the connection's default
/// isolation — READ COMMITTED SNAPSHOT on this database (doc 08 §1.6) — and that is enough:
/// </para>
/// <list type="bullet">
/// <item><description>An <c>UPDATE</c> that reads <c>Searches</c> and writes <c>Searches + @n</c> is one statement:
/// under RCSI it takes the row's update lock, and if another writer changed the row first it waits for that commit
/// and applies its increment to the <i>committed</i> value. No increment is lost however many hosts flush at
/// once.</description></item>
/// <item><description>Two writers that both find no row both try the <c>INSERT</c>; the key's unique index lets one
/// in and makes the other wait, then fail with 2627 once the first commits. The loser's retried <c>UPDATE</c> then
/// finds the committed row. The <c>TRY…CATCH</c> handles it inside the batch, so the error never reaches the
/// client and no lock outlives the statement that took it.</description></item>
/// </list>
/// <para>
/// <b>Which searches found nothing is decided here, by the server</b>, not reported by the browser: each distinct
/// term in the flush is run once through the real search (<see cref="IProductCatalogReader.SearchAsync"/>, one row)
/// and its total decides <c>ZeroResultSearches</c> for that flush. So a term that matches no product can never
/// reach the public list (which shows only searches with results), whatever a caller of the beacon claims — the
/// strongest of the abuse controls, because a term has to describe something the shop sells.
/// </para>
/// <para>
/// No isolation level is set and no explicit transaction is opened: the connection goes back to the pool as it came
/// (the <c>CartStore</c> lesson).
/// </para>
/// </summary>
internal sealed class SearchQueryStore(AppDbContext dbContext, IProductCatalogReader catalog, CatalogOptions catalogOptions)
{
    /// <summary>
    /// DB25's upsert for one (day, term). <c>XACT_ABORT OFF</c> so a duplicate-key error is catchable rather than
    /// fatal to the batch; <c>THROW</c> re-raises anything else.
    /// </summary>
    internal const string UpsertSql = """
        SET XACT_ABORT OFF;
        UPDATE [analytics].[SearchQueriesDaily]
           SET [Searches] = [Searches] + @n, [ZeroResultSearches] = [ZeroResultSearches] + @zero
         WHERE [Date] = @date AND [QueryNormalized] = @term;
        IF @@ROWCOUNT = 0
        BEGIN
            BEGIN TRY
                INSERT INTO [analytics].[SearchQueriesDaily] ([Date], [QueryNormalized], [Searches], [ZeroResultSearches])
                VALUES (@date, @term, @n, @zero);
            END TRY
            BEGIN CATCH
                IF ERROR_NUMBER() NOT IN (2627, 2601) THROW;
                UPDATE [analytics].[SearchQueriesDaily]
                   SET [Searches] = [Searches] + @n, [ZeroResultSearches] = [ZeroResultSearches] + @zero
                 WHERE [Date] = @date AND [QueryNormalized] = @term;
            END CATCH
        END
        """;

    /// <summary>Writes every count and answers the number of searches written.</summary>
    public async Task<int> UpsertAsync(IReadOnlyDictionary<SearchQueryKey, int> counts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(counts);

        if (counts.Count == 0)
        {
            return 0;
        }

        var hasResults = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var term in counts.Keys.Select(key => key.Term).Distinct(StringComparer.Ordinal))
        {
            hasResults[term] = await HasResultsAsync(term, cancellationToken);
        }

        var written = 0;

        // Oldest day first, then by term: a fixed order, so two hosts flushing overlapping terms take the rows' locks
        // in the same order and cannot deadlock on each other.
        foreach (var (key, searches) in counts.OrderBy(pair => pair.Key.Date).ThenBy(pair => pair.Key.Term, StringComparer.Ordinal))
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                UpsertSql,
                [
                    new SqlParameter("@date", System.Data.SqlDbType.Date) { Value = key.Date.ToDateTime(TimeOnly.MinValue) },
                    new SqlParameter("@term", System.Data.SqlDbType.NVarChar, Domain.Analytics.SearchQueryDaily.QueryMaxLength) { Value = key.Term },
                    new SqlParameter("@n", System.Data.SqlDbType.Int) { Value = searches },
                    new SqlParameter("@zero", System.Data.SqlDbType.Int) { Value = hasResults[key.Term] ? 0 : searches },
                ],
                cancellationToken);

            written += searches;
        }

        return written;
    }

    /// <summary>Whether the storefront's own search finds anything for <paramref name="term"/> right now.</summary>
    private async Task<bool> HasResultsAsync(string term, CancellationToken cancellationToken)
    {
        var query = ProductSearchQuery.Create(term, new ProductListCriteria(PageSize: "1"), catalogOptions);
        if (!query.IsSuccess)
        {
            return false;
        }

        var page = await catalog.SearchAsync(query.Value, cancellationToken);
        return page.IsSuccess && page.Value.TotalCount > 0;
    }
}
