using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpencerDenim.Api.Endpoints.Sales;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// A <b>real Kestrel</b> on a loopback port the kernel picks, serving a named set of the API's own
/// endpoints.
/// <para>
/// <b>Why this exists, when <see cref="ApiFactory"/> already hosts the API.</b> That factory uses
/// <c>WebApplicationFactory</c>'s in-memory <c>TestServer</c>, which has <b>no transport at all</b>: no
/// request line, no header buffer, no size limits of any kind. Every limit in
/// <c>KestrelServerLimits</c> is therefore unenforceable there, so a test written against it cannot
/// fail on one — which is exactly how W2-08's <c>GET /api/wishlist?slug=…</c> shipped with a request
/// line more than five times Kestrel's default ceiling while a test named "a list over the cap is
/// truncated rather than refused" passed over it. It is the W1-12 shape: a fixture that could not
/// express the defect.
/// </para>
/// <para>
/// <b>Why it is not <see cref="ApiFactory"/> with Kestrel swapped in.</b> That was tried and does not
/// work: <c>WebApplicationFactory</c> casts the resolved <c>IServer</c> to <c>TestServer</c> the moment
/// anything touches <c>Services</c>, so replacing the server is an <c>InvalidCastException</c> rather
/// than a configuration choice. This host is therefore built with <c>WebApplication.CreateSlimBuilder</c>
/// and maps the endpoint modules a test names — the application's **own** mapping extensions, so routing,
/// model binding and the endpoint's real metadata are all under test. What it deliberately does not bring
/// is the rest of the application: no database, no seeders, no authentication. A transport question needs
/// none of them, and not needing a database means these tests run on a machine with no SQL Server.
/// </para>
/// <para>
/// <b>Its limits are Kestrel's defaults, untouched</b>, which is the whole point — nothing here
/// configures <c>Limits</c>, exactly as <c>SpencerDenim.Api</c> does not.
/// <c>WishlistTransportTests</c> holds that second half: a source scan proving the real application
/// raises no limit either, so this host's defaults are the deployed ones.
/// </para>
/// <para>
/// <b>Port 0</b>, never a fixed number: doc 14 is explicit that ports on this machine are shared and
/// that a port which answers is not proof that it is yours. The kernel picks a free one and
/// <see cref="BaseAddress"/> reports what it picked, so two suites can run at once and neither can
/// serve the other's requests.
/// </para>
/// </summary>
public sealed class KestrelApiHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private KestrelApiHost(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    /// <summary>Where the server really is listening, including the port the kernel chose.</summary>
    public Uri BaseAddress { get; }

    /// <summary>
    /// Starts a real Kestrel serving the wishlist endpoints, with <paramref name="handler"/> standing in
    /// for the query handler.
    /// <para>
    /// The handler is injected rather than resolved from the real composition root because a transport
    /// test must be able to fail on the transport and on nothing else: a catalogue read would drag in a
    /// database, and a database that is missing or slow would then look like a transport failure.
    /// </para>
    /// </summary>
    public static async Task<KestrelApiHost> StartWishlistAsync(
        Application.Abstractions.Messaging.IQueryHandler<
            Application.Sales.Wishlist.GetWishlistQuery,
            Application.Sales.Wishlist.Contracts.WishlistDto> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var builder = WebApplication.CreateSlimBuilder();

        // Quiet: a transport test that prints a request log per assertion drowns the suite's output.
        builder.Logging.ClearProviders();

        // `127.0.0.1` rather than `localhost`, so there is no chance of binding v6 and connecting v4.
        // Port 0: the kernel picks, and `BaseAddress` reports what it picked.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSingleton(handler);

        var app = builder.Build();
        app.MapWishlistEndpoints();

        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel reported no addresses, so there is nothing to talk to.");

        var address = addresses.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel bound no address.");

        return new KestrelApiHost(app, new Uri(address));
    }

    /// <summary>A client addressing the real listener. Nothing is intercepted and nothing is in memory.</summary>
    public HttpClient CreateClient() => new() { BaseAddress = BaseAddress };

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
