using SpencerDenim.Application.Catalog.Admin;

namespace SpencerDenim.UnitTests.Application.Catalog.Admin;

/// <summary>
/// The guard that keeps two admins reordering at once from producing an order neither of them chose
/// (ADM-11, doc 11 P1 step 4).
/// </summary>
public sealed class ReorderListTests
{
    private static readonly int[] MissingThree = [3];

    private static readonly int[] UnknownNinetyNine = [99];

    [Fact]
    public void Exactly_the_right_ids_in_any_order_is_accepted() =>
        Assert.Null(ReorderList.Check([3, 1, 2], [1, 2, 3], "orderedCategoryIds", "category"));

    /// <summary>
    /// The case that makes a subset dangerous rather than merely incomplete: somebody else created a
    /// sibling while this page was open, and applying the list as sent would leave the new category
    /// numbered from the old scheme and sitting somewhere nobody chose.
    /// </summary>
    [Fact]
    public void A_list_that_leaves_a_sibling_out_is_a_stale_order_conflict()
    {
        var result = ReorderList.Check([1, 2], [1, 2, 3], "orderedCategoryIds", "category");

        var conflict = Assert.IsType<CatalogWriteResult.Conflict>(result);
        Assert.Equal(CatalogConflictCodes.StaleOrder, conflict.Code);
        Assert.Contains("leaves out category 3", conflict.Message, StringComparison.Ordinal);
        Assert.Equal(MissingThree, Assert.IsType<List<int>>(conflict.Details!["missingIds"]));
    }

    [Fact]
    public void A_list_naming_something_that_has_moved_away_is_a_stale_order_conflict()
    {
        var result = ReorderList.Check([1, 2, 99], [1, 2], "orderedCategoryIds", "category");

        var conflict = Assert.IsType<CatalogWriteResult.Conflict>(result);
        Assert.Equal(CatalogConflictCodes.StaleOrder, conflict.Code);
        Assert.Equal(UnknownNinetyNine, Assert.IsType<List<int>>(conflict.Details!["unknownIds"]));
    }

    [Fact]
    public void Both_problems_at_once_are_both_reported()
    {
        var result = ReorderList.Check([1, 99], [1, 2], "orderedCategoryIds", "category");

        var conflict = Assert.IsType<CatalogWriteResult.Conflict>(result);
        Assert.Contains("leaves out category 2", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("category 99", conflict.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A repeat is a client bug rather than a race, so it is a 400 naming the field and not a 409.
    /// Without this check the duplicate would silently win the later position.
    /// </summary>
    [Fact]
    public void A_repeated_id_is_a_field_error()
    {
        var result = ReorderList.Check([1, 2, 2], [1, 2], "orderedCategoryIds", "category");

        var invalid = Assert.IsType<CatalogWriteResult.Invalid>(result);
        Assert.Equal("orderedCategoryIds", Assert.Single(invalid.Errors).Field);
    }

    [Fact]
    public void An_absent_or_empty_list_is_a_field_error()
    {
        Assert.IsType<CatalogWriteResult.Invalid>(
            ReorderList.Check(null, [1], "orderedCategoryIds", "category"));
        Assert.IsType<CatalogWriteResult.Invalid>(
            ReorderList.Check([], [1], "orderedCategoryIds", "category"));
    }
}
