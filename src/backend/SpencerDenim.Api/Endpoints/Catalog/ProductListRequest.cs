using Microsoft.AspNetCore.Mvc;
using SpencerDenim.Application.Catalog.Products;

namespace SpencerDenim.Api.Endpoints.Catalog;

/// <summary>
/// The query string of the listing and search endpoints, bound straight from the request. It carries
/// no rules: <see cref="ProductListQuery.Create"/> validates and normalises it, so the same checks
/// apply however the query is built.
/// </summary>
internal sealed record ProductListRequest(
    [property: FromQuery(Name = "department")] string? Department,
    [property: FromQuery(Name = "category")] string? Category,
    [property: FromQuery(Name = "ageGroup")] string[]? AgeGroup,
    [property: FromQuery(Name = "minPrice")] decimal? MinPrice,
    [property: FromQuery(Name = "maxPrice")] decimal? MaxPrice,
    [property: FromQuery(Name = "size")] string[]? Size,
    [property: FromQuery(Name = "colour")] string[]? Colour,
    [property: FromQuery(Name = "material")] string[]? Material,
    [property: FromQuery(Name = "fit")] string[]? Fit,
    [property: FromQuery(Name = "occasion")] string[]? Occasion,
    [property: FromQuery(Name = "minRating")] decimal? MinRating,
    [property: FromQuery(Name = "inStock")] bool? InStock,
    [property: FromQuery(Name = "onSale")] bool? OnSale,
    [property: FromQuery(Name = "minDiscountPercent")] decimal? MinDiscountPercent,
    [property: FromQuery(Name = "newArrivals")] bool? NewArrivals,
    [property: FromQuery(Name = "sort")] string? Sort,
    [property: FromQuery(Name = "page")] int? Page,
    [property: FromQuery(Name = "pageSize")] int? PageSize)
{
    public ProductListCriteria ToCriteria() => new(
        Department,
        Category,
        AgeGroup,
        MinPrice,
        MaxPrice,
        Size,
        Colour,
        Material,
        Fit,
        Occasion,
        MinRating,
        InStock,
        OnSale,
        MinDiscountPercent,
        NewArrivals,
        Sort,
        Page,
        PageSize);
}
