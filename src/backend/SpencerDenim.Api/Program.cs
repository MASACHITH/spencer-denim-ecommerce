using SpencerDenim.Api.Endpoints;
using SpencerDenim.Api.Endpoints.Admin;
using SpencerDenim.Api.Health;
using SpencerDenim.Api.Http;
using SpencerDenim.Api.Logging;
using SpencerDenim.Api.OpenApi;
using SpencerDenim.Application;
using SpencerDenim.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddSerilogLogging();

builder.Services.AddProblemDetails();
builder.Services.AddApiOpenApi();
builder.Services.AddApiHealthChecks();
builder.Services.AddApiForwardedHeaders();

builder.Services.AddApplication();
builder.Services.AddInfrastructure();

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

await app.RunAsync();
