using System.Text.Json.Serialization;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Cli;
using SpencerDenim.Api.Endpoints;
using SpencerDenim.Api.Endpoints.Admin;
using SpencerDenim.Api.Endpoints.Catalog;
using SpencerDenim.Api.Endpoints.Content;
using SpencerDenim.Api.Endpoints.Media;
using SpencerDenim.Api.Endpoints.Sales;
using SpencerDenim.Api.Health;
using SpencerDenim.Api.Http;
using SpencerDenim.Api.Logging;
using SpencerDenim.Api.OpenApi;
using SpencerDenim.Application;
using SpencerDenim.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddSerilogLogging();

// The key ring behind the admin cookie, antiforgery tokens and encrypted settings: persisted to a volume,
// so a container restart signs nobody out (#96, docs/15 §4).
builder.AddApiDataProtection();

builder.Services.AddProblemDetails();

// Enums travel as their names ("Sale", "LowStock", "OutOfStock"), so a client never has to keep a
// copy of our numeric values in sync.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddApiOpenApi();
builder.Services.AddApiHealthChecks();
builder.Services.AddApiForwardedHeaders();
builder.Services.AddNewsletterSignupThrottle();
builder.Services.AddPaymentSlipUploadThrottle();
builder.Services.AddContactEnquiryThrottle();
// Output caching, with no base policy: only the sitemap feed opts in (see SitemapOutputCache for the
// window and why it is a minute rather than matching the storefront's fifteen).
builder.Services.AddSitemapOutputCache();

// Where a media upload is spooled while it streams in (doc 03 §7: not the host's temp directory by
// assumption). Api is the composition root, and this needs IHostEnvironment.
builder.Services.AddSingleton<SpencerDenim.Api.Http.UploadSpool>();

builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddAdminAuthorizationResponses();

var app = builder.Build();

// A command-line verb runs instead of the web host: it reuses this exact composition root — the
// configured media volumes, the real IMediaService, the database — and never starts Kestrel, so it
// binds no port. See ImportClientAssetsCommand for why the import lives here rather than in a tool
// project or behind a seeding flag.
if (ImportClientAssetsCommand.Matches(args))
{
    // Ctrl+C asks the import to stop rather than killing it: the run is one transaction, so a
    // cancellation rolls it back instead of leaving the catalogue half rewired.
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    return await ImportClientAssetsCommand.RunAsync(app.Services, args, cancellation.Token);
}

// Before anything that reads the request scheme/remote IP (cookie auth, antiforgery): restores the client's
// real scheme/address behind Caddy (doc 03 §4).
app.UseApiForwardedHeaders();

app.UseSerilogRequestLoggingWithHealthFilter();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseApiOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

// After authentication and authorization, so a policy can see them, and before the endpoints it serves.
app.UseOutputCache();

app.MapApiHealthChecks();
app.MapPingEndpoints();
app.MapAdminAuthEndpoints();

app.MapAdminTwoFactorEndpoints();
app.MapAuditLogEndpoints();
app.MapAdminAlertEndpoints();

app.MapUserEndpoints();
app.MapRoleEndpoints();
app.MapCatalogEndpoints();
app.MapAdminCatalogEndpoints();
app.MapAdminProductEndpoints();
app.MapContentEndpoints();
app.MapSiteSettingsEndpoints();
app.MapAdminSettingsEndpoints();
app.MapAdminBankTransferEndpoints();
app.MapAdminOrderEndpoints();
app.MapPaymentSlipEndpoints();
app.MapAdminContentEndpoints();
app.MapAdminPromotionEndpoints();
app.MapCartEndpoints();
app.MapWishlistEndpoints();
app.MapCheckoutEndpoints();
app.MapAdminMediaEndpoints();
app.MapMediaFileEndpoints();

await app.RunAsync();
return 0;
