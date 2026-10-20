namespace SpencerDenim.Infrastructure.Catalog.Reviews;

/// <summary>
/// The rating half of <c>catalog.ProductStats</c> (doc 08 §7.4: "RatingAverage/Count — approved reviews — same
/// transaction as moderation or edit"), as SQL every review write shares.
/// <para>
/// <b>The concurrency protocol, in two rules.</b>
/// <list type="number">
/// <item><description>
/// <b>Every transaction that changes a product's reviews takes that product's <c>ProductStats</c> row with
/// <c>UPDLOCK</c> first</b> (<see cref="LockOne"/>), before it touches <c>catalog.ProductReviews</c>. Two
/// moderators approving two reviews of one product therefore run one after the other: the second waits on the
/// stats row until the first commits.
/// </description></item>
/// <item><description>
/// <b>The recompute is one <c>UPDATE … FROM</c> that counts straight from the source rows</b> (never "count + 1"
/// and never a figure read earlier), reading them <c>WITH (READCOMMITTEDLOCK)</c>. Under RCSI (#183) a plain read
/// would see the last committed version without waiting; with rule 1 the second transaction's statements all
/// start after the first committed, so they see its review — and <c>READCOMMITTEDLOCK</c> is the backstop that
/// makes the count wait for any in-flight writer of those rows rather than read around it.
/// </description></item>
/// </list>
/// Together: no lost update (the second count includes the first approval), and no deadlock between review
/// writers, because each takes the stats row before any review row. A multi-product write (the PDPA erasure)
/// locks its stats rows in one statement before deleting anything.
/// </para>
/// <para>
/// <b>Only the two rating columns move.</b> Pricing is the product save's and the campaign job's; sold quantities
/// are <c>NightlyStats</c>'; <c>StatsRefreshedAtUtc</c> is that job's timestamp and is left alone.
/// </para>
/// </summary>
internal static class ProductRatingSql
{
    /// <summary>Takes one product's stats row for the rest of the transaction. Parameter <c>@productId</c>.</summary>
    public const string LockOne =
        """
        SELECT COUNT(*)
          FROM catalog.ProductStats WITH (UPDLOCK, ROWLOCK)
         WHERE ProductId = @productId;
        """;

    /// <summary>
    /// Recomputes the rating of the products the <c>WHERE</c> clause appended to it selects. Average to two decimal
    /// places, halves away from zero (<c>ReviewRules.AverageOf</c>), NULL with no approved review.
    /// </summary>
    public const string RecomputeWhere =
        """
        UPDATE stats
           SET RatingCount = agg.ApprovedCount,
               RatingAverage = CASE
                   WHEN agg.ApprovedCount = 0 THEN NULL
                   ELSE CAST(ROUND(CAST(agg.RatingSum AS decimal(19, 6)) / agg.ApprovedCount, 2) AS decimal(3, 2))
               END
          FROM catalog.ProductStats AS stats
         CROSS APPLY (
               SELECT COUNT(*) AS ApprovedCount,
                      ISNULL(SUM(CAST(review.Rating AS int)), 0) AS RatingSum
                 FROM catalog.ProductReviews AS review WITH (READCOMMITTEDLOCK)
                WHERE review.ProductId = stats.ProductId
                  AND review.Status = 1
               ) AS agg
         WHERE
        """;

    /// <summary>Recomputes one product. Parameter <c>@productId</c>.</summary>
    public const string RecomputeOne = RecomputeWhere + " stats.ProductId = @productId;";
}
