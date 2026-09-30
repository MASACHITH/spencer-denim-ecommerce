using System.Text.Json.Serialization;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Endpoints;
using SpencerDenim.Api.Endpoints.Admin;
using SpencerDenim.Api.Endpoints.Catalog;
using SpencerDenim.Api.Endpoints.Content;
using SpencerDenim.Api.Endpoints.Media;
using SpencerDenim.Api.Health;
using SpencerDenim.Api.Http;
using SpencerDenim.Api.Logging;
using SpencerDenim.Api.OpenApi;
using SpencerDenim.Application;
using SpencerDenim.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddSerilogLogging();

builder.Services.AddProblemDetails();

// Enums travel as their names ("Sale", "LowStock", "OutOfStock"), so a client never has to keep a
// copy of our numeric values in sync.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddApiOpenApi();
builder.Services.AddApiHealthChecks();
builder.Services.AddApiForwardedHeaders();
builder.Services.AddNewsletterSignupThrottle();

// Where a media upload is spooled while it streams in (doc 03 §7: not the host's temp directory by
// assumption). Api is the composition root, and this needs IHostEnvironment.
builder.Services.AddSingleton<SpencerDenim.Api.Http.UploadSpool>();

builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddAdminAuthorizationResponses();

var app = builder.Build();

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

app.MapApiHealthChecks();
app.MapPingEndpoints();
app.MapAdminAuthEndpoints();

app.MapAdminTwoFactorEndpoints();
app.MapAuditLogEndpoints();

app.MapCatalogEndpoints();
app.MapContentEndpoints();
app.MapAdminMediaEndpoints();
app.MapMediaFileEndpoints();

await app.RunAsync();
