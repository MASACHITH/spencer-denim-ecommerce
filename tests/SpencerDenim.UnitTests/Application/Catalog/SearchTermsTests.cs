using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Catalog.Products;
using SpencerDenim.Application.Catalog.Search;

namespace SpencerDenim.UnitTests.Application.Catalog;

/// <summary>Doc 08 §7.6: tokenising and escaping for the LIKE search.</summary>
public sealed class SearchTermsTests
{
    [Fact]
    public void Tokens_are_lower_cased_split_on_whitespace_and_de_duplicated()
    {
        Assert.Equal(["linen", "shirt"], SearchTerms.Tokenise("  Linen   SHIRT linen "));
    }

    [Fact]
    public void At_most_five_tokens_are_kept()
    {
        var tokens = SearchTerms.Tokenise("one two three four five six seven");

        Assert.Equal(SearchTerms.MaxTokens, tokens.Count);
        Assert.DoesNotContain("six", tokens);
    }

    [Fact]
    public void Tokens_shorter_than_the_minimum_are_dropped()
    {
        Assert.Equal(["shirt"], SearchTerms.Tokenise("a shirt"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("x")]
    public void Nothing_useful_produces_no_tokens(string? raw)
    {
        Assert.Empty(SearchTerms.Tokenise(raw));
    }

    [Fact]
    public void Non_latin_scripts_survive_tokenising()
    {
        Assert.Equal(["ඔසරිය", "காஞ்சிபுரம்"], SearchTerms.Tokenise("ඔසරිය காஞ்சிபுரம்"));
    }

    [Theory]
    [InlineData("50%", @"50\%")]
    [InlineData("a_b", @"a\_b")]
    [InlineData("[wow]", @"\[wow]")]
    [InlineData(@"back\slash", @"back\\slash")]
    [InlineData("plain", "plain")]
    public void Like_wildcards_and_the_escape_character_are_escaped(string value, string expected)
    {
        Assert.Equal(expected, SearchTerms.EscapeLikeValue(value));
    }

    [Fact]
    public void Patterns_wrap_the_escaped_value()
    {
        Assert.Equal(@"%50\%%", SearchTerms.ToContainsPattern("50%"));
        Assert.Equal(@"50\%%", SearchTerms.ToPrefixPattern("50%"));
        Assert.Equal(@"% 50\%%", SearchTerms.ToWordPrefixPattern("50%"));
    }

    [Fact]
    public void Escaping_a_null_value_throws()
    {
        Assert.Throws<ArgumentNullException>(() => SearchTerms.EscapeLikeValue(null!));
    }

    /// <summary>
    /// Issue #78 tester pass: <see cref="SearchTerms.Tokenise"/> is allowed to stop at five, but
    /// <see cref="SearchTerms.HasUncheckedTokens"/> has to see past that cap to answer "did the search look at
    /// everything this term claims to be".
    /// </summary>
    [Theory]
    [InlineData("one two three four five")]
    [InlineData("one two three four five one two")]
    [InlineData("a one two three four five")]
    [InlineData(null)]
    [InlineData("")]
    public void Five_or_fewer_real_tokens_are_not_unchecked(string? raw) =>
        Assert.False(SearchTerms.HasUncheckedTokens(raw));

    [Theory]
    [InlineData("one two three four five six")]
    [InlineData("li in ne en lin idiot")]
    public void More_than_five_real_tokens_are_unchecked(string raw) =>
        Assert.True(SearchTerms.HasUncheckedTokens(raw));
}

/// <summary>SRCH-02: the search request carries the listing filters on top of the term.</summary>
public sealed class ProductSearchQueryTests
{
    private static readonly CatalogOptions Options = new();

    [Fact]
    public void A_valid_term_produces_tokens_and_keeps_the_listing_filters()
    {
        var result = ProductSearchQuery.Create(
            "  Linen Shirt ",
            new ProductListCriteria(Department: "women", Sort: "price-asc", PageSize: "12"),
            Options);

        Assert.True(result.IsSuccess);
        Assert.Equal("Linen Shirt", result.Value.Term);
        Assert.Equal(["linen", "shirt"], result.Value.Tokens);
        Assert.Equal("women", result.Value.ListQuery.DepartmentSlug);
        Assert.Equal(ProductSortOption.PriceAscending, result.Value.ListQuery.Sort);
        Assert.Equal(12, result.Value.ListQuery.PageSize);
        Assert.False(result.Value.SortByRelevance);
    }

    [Fact]
    public void Recommended_means_relevance_for_a_search()
    {
        var result = ProductSearchQuery.Create("linen", new ProductListCriteria(), Options);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.SortByRelevance);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" a ")]
    public void A_term_shorter_than_the_minimum_is_rejected(string? term)
    {
        var result = ProductSearchQuery.Create(term, new ProductListCriteria(), Options);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Field == "q");
    }

    [Fact]
    public void A_term_longer_than_the_maximum_is_rejected()
    {
        var result = ProductSearchQuery.Create(new string('a', SearchTerms.MaxTermLength + 1), new ProductListCriteria(), Options);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Field == "q");
    }

    [Fact]
    public void Listing_errors_are_reported_together_with_term_errors()
    {
        var result = ProductSearchQuery.Create("a", new ProductListCriteria(Sort: "nope"), Options);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Field == "q");
        Assert.Contains(result.Errors, error => error.Field == "sort");
    }
}
