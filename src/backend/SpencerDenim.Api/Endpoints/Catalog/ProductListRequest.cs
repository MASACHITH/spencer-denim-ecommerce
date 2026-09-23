using Microsoft.AspNetCore.Mvc;
using SpencerDenim.Application.Catalog.Products;

namespace SpencerDenim.Api.Endpoints.Catalog;

/// <summary>
/// The query string of the listing and search endpoints, bound straight from the request. It carries
/// no rules: <see cref="ProductListQuery.Create"/> validates and normalises it, so the same checks
/// apply however the query is built.
/// <para>
/// Numbers and flags bind as <see cref="string"/>. Binding them as <c>int?</c>, <c>decimal?</c> or
/// <c>bool?</c> makes the framework throw on <c>?page=abc</c> before the handler runs, which is a 500
/// and an ERROR log on a public storefront endpoint; parsed in <see cref="ProductListQuery.Create"/>
/// they become the same 400 that every other bad value gets.
/// </para>
/// </summary>
internal sealed record ProductListRequest(
    [property: FromQuery(Name = "department")] string? Department,
    [property: FromQuery(Name = "category")] string? Category,
    [property: FromQuery(Name = "ageGroup")] string[]? AgeGroup,
    [property: FromQuery(Name = "minPrice")] string? MinPrice,
    [property: FromQuery(Name = "maxPrice")] string? MaxPrice,
    [property: FromQuery(Name = "size")] string[]? Size,
    [property: FromQuery(Name = "colour")] string[]? Colour,
    [property: FromQuery(Name = "material")] string[]? Material,
    [property: FromQuery(Name = "fit")] string[]? Fit,
    [property: FromQuery(Name = "occasion")] string[]? Occasion,
    [property: FromQuery(Name = "minRating")] string? MinRating,
    [property: FromQuery(Name = "inStock")] string? InStock,
    [property: FromQuery(Name = "onSale")] string? OnSale,
    [property: FromQuery(Name = "minDiscountPercent")] string? MinDiscountPercent,
    [property: FromQuery(Name = "newArrivals")] string? NewArrivals,
    [property: FromQuery(Name = "sort")] string? Sort,
    [property: FromQuery(Name = "page")] string? Page,
    [property: FromQuery(Name = "pageSize")] string? PageSize)
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
