using System.Globalization;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Catalog.Abstractions;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Application.Catalog.Products;
using SpencerDenim.Application.Catalog.Search;

namespace SpencerDenim.UnitTests.Application.Catalog;

/// <summary>PDP: a malformed slug is a 400, an unknown slug is a 404.</summary>
public sealed class GetProductBySlugQueryHandlerTests
{
    private sealed class RecordingReader(ProductDetailDto? product) : IProductCatalogReader
    {
        public string? RequestedSlug { get; private set; }

        public Task<ProductDetailDto?> GetBySlugAsync(string slug, CancellationToken cancellationToken)
        {
            RequestedSlug = slug;
            return Task.FromResult(product);
        }

        public Task<Result<PagedResult<ProductCardDto>>> ListAsync(ProductListQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<PagedResult<ProductCardDto>>> SearchAsync(ProductSearchQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static GetProductBySlugQuery Accepted(string slug)
    {
        var result = GetProductBySlugQuery.Create(slug);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors.Select(error => error.Message)));
        return result.Value;
    }

    [Fact]
    public async Task The_slug_is_trimmed_and_lower_cased_before_it_reaches_the_store()
    {
        var reader = new RecordingReader(null);
        var handler = new GetProductBySlugQueryHandler(reader);

        await handler.HandleAsync(Accepted("  Relaxed-Linen-Blend-Shirt "), TestContext.Current.CancellationToken);

        Assert.Equal("relaxed-linen-blend-shirt", reader.RequestedSlug);
    }

    [Fact]
    public async Task An_unknown_slug_reaches_the_store_and_comes_back_null_so_the_endpoint_can_return_404()
    {
        var handler = new GetProductBySlugQueryHandler(new RecordingReader(null));

        var product = await handler.HandleAsync(Accepted("nothing-here"), TestContext.Current.CancellationToken);

        Assert.Null(product);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_empty_slug_fails_validation(string? slug)
    {
        var result = GetProductBySlugQuery.Create(slug);

        Assert.False(result.IsSuccess);
        Assert.Equal("slug", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public void A_slug_longer_than_the_column_fails_validation()
    {
        var result = GetProductBySlugQuery.Create(new string('a', GetProductBySlugQuery.MaxSlugLength + 1));

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("slug", error.Field);
        Assert.Contains(GetProductBySlugQuery.MaxSlugLength.ToString(CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The doc comment always promised a 400 for a malformed slug, but only length and emptiness were
    /// checked, so <c>&lt;script&gt;</c> reached the store and came back inside the 404 body.
    /// </summary>
    [Theory]
    [InlineData("<script>")]
    [InlineData("has space")]
    [InlineData("under_score")]
    [InlineData("double--hyphen")]
    [InlineData("trailing-")]
    [InlineData("../../etc/passwd")]
    [InlineData("100%")]
    public void A_malformed_slug_fails_validation(string slug)
    {
        var result = GetProductBySlugQuery.Create(slug);

        Assert.False(result.IsSuccess);
        Assert.Equal("slug", Assert.Single(result.Errors).Field);
    }

    [Theory]
    [InlineData("relaxed-linen-blend-shirt")]
    [InlineData("baby-ribbed-bodysuit-set")]
    [InlineData("tee2")]
    public void A_well_formed_slug_is_accepted(string slug)
    {
        Assert.Equal(slug, Accepted(slug).Slug);
    }

    [Fact]
    public async Task A_null_query_throws()
    {
        var handler = new GetProductBySlugQueryHandler(new RecordingReader(null));

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(null!, CancellationToken.None));
    }
}
