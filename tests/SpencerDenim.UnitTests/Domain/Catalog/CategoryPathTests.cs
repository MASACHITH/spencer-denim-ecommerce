using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.UnitTests.Domain.Catalog;

/// <summary>
/// The arithmetic of the materialised path (doc 08 §3.2), which W1-12's move use case rewrites for a whole
/// subtree.
/// <para>
/// It is tested here, without a database, because a wrong path is the one defect in ADM-11 that shows up on
/// no screen: the rows read correctly everywhere, and the storefront's <c>LIKE path + '%'</c> subtree
/// predicate quietly answers with the wrong products.
/// </para>
/// </summary>
public sealed class CategoryPathTests
{
    [Fact]
    public void A_top_level_category_path_is_its_own_id()
    {
        Assert.Equal("/7/", CategoryPath.Build(null, 7));
        Assert.Equal("/7/", CategoryPath.Build(string.Empty, 7));
    }

    [Fact]
    public void A_child_path_extends_its_parents()
    {
        Assert.Equal("/7/19/", CategoryPath.Build("/7/", 19));
        Assert.Equal("/7/19/23/", CategoryPath.Build("/7/19/", 23));
    }

    [Theory]
    [InlineData("/7/", 1)]
    [InlineData("/7/19/", 2)]
    [InlineData("/7/19/23/", 3)]
    [InlineData("", 0)]
    public void Depth_counts_the_segments(string path, int expected) =>
        Assert.Equal((byte)expected, CategoryPath.DepthOf(path));

    /// <summary>
    /// The trailing slash earns its place here. Without it <c>/1/</c> would "contain" <c>/12/</c>, and the
    /// move use case's cycle guard would refuse a legal move — or, worse, the subtree query would collect a
    /// sibling's children and re-path them.
    /// </summary>
    [Fact]
    public void A_sibling_whose_id_starts_with_another_is_not_inside_it()
    {
        Assert.False(CategoryPath.IsWithin("/12/", "/1/"));
        Assert.False(CategoryPath.IsWithin("/12/34/", "/1/"));
        Assert.True(CategoryPath.IsWithin("/1/2/", "/1/"));
        Assert.True(CategoryPath.IsWithin("/1/", "/1/"), "a category is inside its own subtree");
    }

    [Fact]
    public void Rebasing_moves_a_whole_subtree_without_touching_the_ids_inside_it()
    {
        // /3/17/ moves under /3/40/, so it becomes /3/40/17/ and its own children follow.
        Assert.Equal("/3/40/17/", CategoryPath.Rebase("/3/17/", "/3/17/", "/3/40/17/"));
        Assert.Equal("/3/40/17/55/", CategoryPath.Rebase("/3/17/55/", "/3/17/", "/3/40/17/"));
    }

    [Fact]
    public void Rebasing_a_path_from_another_subtree_throws_rather_than_writing_a_wrong_one()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CategoryPath.Rebase("/3/18/", "/3/17/", "/3/40/17/"));

        Assert.Contains("/3/18/", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Depth_after_a_rebase_is_the_new_path_s_own_depth()
    {
        var moved = CategoryPath.Rebase("/3/17/55/", "/3/17/", "/3/40/17/");

        Assert.Equal((byte)4, CategoryPath.DepthOf(moved));
    }

    /// <summary>
    /// <c>Category.BuildPath</c> is what the seeder calls; it must stay the same function, or a seeded tree
    /// and an admin-built one would disagree about the format.
    /// </summary>
    [Fact]
    public void The_entity_helper_and_the_path_type_agree()
    {
        Assert.Equal(CategoryPath.Build("/3/", 17), Category.BuildPath("/3/", 17));
        Assert.Equal(CategoryPath.Build(null, 3), Category.BuildPath(null, 3));
    }
}
