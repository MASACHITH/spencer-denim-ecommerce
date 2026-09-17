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

        public Task<PagedResult<ProductCardDto>> ListAsync(ProductListQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PagedResult<ProductCardDto>> SearchAsync(ProductSearchQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task The_slug_is_trimmed_and_lower_cased_before_it_reaches_the_store()
    {
        var reader = new RecordingReader(null);
        var handler = new GetProductBySlugQueryHandler(reader);

        await handler.HandleAsync(new GetProductBySlugQuery("  Relaxed-Linen-Blend-Shirt "), TestContext.Current.CancellationToken);

        Assert.Equal("relaxed-linen-blend-shirt", reader.RequestedSlug);
    }

    [Fact]
    public async Task An_unknown_slug_succeeds_with_a_null_value_so_the_endpoint_can_return_404()
    {
        var handler = new GetProductBySlugQueryHandler(new RecordingReader(null));

        var result = await handler.HandleAsync(new GetProductBySlugQuery("nothing-here"), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_slug_fails_validation(string slug)
    {
        var handler = new GetProductBySlugQueryHandler(new RecordingReader(null));

        var result = await handler.HandleAsync(new GetProductBySlugQuery(slug), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("slug", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public async Task A_slug_longer_than_the_column_fails_validation()
    {
        var handler = new GetProductBySlugQueryHandler(new RecordingReader(null));
        var slug = new string('a', GetProductBySlugQuery.MaxSlugLength + 1);

        var result = await handler.HandleAsync(new GetProductBySlugQuery(slug), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("slug", Assert.Single(result.Errors).Field);
    }

    [Fact]
    public async Task A_null_query_throws()
    {
        var handler = new GetProductBySlugQueryHandler(new RecordingReader(null));

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(null!, CancellationToken.None));
    }
}
