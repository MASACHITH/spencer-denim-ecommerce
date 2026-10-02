using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Catalog;

namespace SpencerDenim.Api.Endpoints.Catalog;

/// <summary>
/// The one output-cache policy in the API: the sitemap feed (GLB-11).
/// <para>
/// <b>Why it exists.</b> The feed is the heaviest read in the system — three projections over the whole
/// visible catalogue — and before this it ran afresh on every request. The storefront only asks once
/// every fifteen minutes, so in normal operation this is never consulted twice; it is here so that a
/// caller who asks repeatedly is answered from memory instead, which turns an unbounded request rate
/// into one database read per window.
/// </para>
/// <para>
/// <b>Why the window is a minute and not fifteen.</b> The storefront already holds the rendered document
/// for fifteen minutes. A matching window here would stack with it and make the worst-case staleness
/// half an hour; a minute stacks to sixteen, which is no change worth measuring, and still removes the
/// per-request cost. <c>Catalog:SitemapCacheSeconds</c> moves it without new code.
/// </para>
/// <para>
/// <c>AddOutputCache</c> is registered with no base policy, so adding this caches nothing else in the
/// API: an endpoint is cached only where it says <c>CacheOutput</c>.
/// </para>
/// </summary>
internal sealed class SitemapOutputCache : IOutputCachePolicy
{
    public const string PolicyName = "catalog-sitemap";

    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.HttpContext.Request;

        // GET and HEAD only, and never a request carrying an Authorization header or a cookie. There is
        // nothing user-specific in this answer today; a cache that *could* serve one caller's response
        // to another is a defect to make impossible rather than one to remember not to create.
        var cacheable =
            (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            && string.IsNullOrEmpty(request.Headers.Authorization)
            && request.Cookies.Count == 0;

        context.EnableOutputCaching = cacheable;
        context.AllowCacheLookup = cacheable;
        context.AllowCacheStorage = cacheable;
        // One caller refreshes it and the rest wait, rather than every concurrent miss running the
        // query — the same reason the storefront shares one in-flight rebuild.
        context.AllowLocking = true;

        context.ResponseExpirationTimeSpan = TimeSpan.FromSeconds(
            context.HttpContext.RequestServices
                .GetRequiredService<IOptions<CatalogOptions>>()
                .Value.SitemapCacheSeconds);

        // The query string is not part of the key, because the endpoint takes no parameters. Varying on
        // it would let a caller fill the cache with `?x=1`, `?x=2`, … and defeat the point of having one.
        context.CacheVaryByRules.QueryKeys = [];

        return ValueTask.CompletedTask;
    }

    /// <summary>Only a 200 is worth keeping: a failure must not be handed to the next caller.</summary>
    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.HttpContext.Response.StatusCode != StatusCodes.Status200OK)
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

/// <summary>Composition for <see cref="SitemapOutputCache"/>, which cannot host an extension method itself.</summary>
internal static class SitemapOutputCacheRegistration
{
    /// <summary>Registers output caching with the sitemap policy and <b>no base policy</b>.</summary>
    public static IServiceCollection AddSitemapOutputCache(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOutputCache(options =>
            options.AddPolicy(SitemapOutputCache.PolicyName, new SitemapOutputCache()));

        return services;
    }
}
