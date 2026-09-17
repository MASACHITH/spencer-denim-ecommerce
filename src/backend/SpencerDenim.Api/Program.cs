using Microsoft.AspNetCore.HttpOverrides;
using SpencerDenim.Api.Endpoints;
using SpencerDenim.Api.Health;
using SpencerDenim.Api.Logging;
using SpencerDenim.Api.OpenApi;
using SpencerDenim.Application;
using SpencerDenim.Infrastructure;

// Container HEALTHCHECK mode: the runtime image has no curl/wget, so the app probes itself.
// Usage: dotnet SpencerDenim.Api.dll --healthcheck [url]
if (HealthProbe.IsRequested(args))
{
    return await HealthProbe.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

builder.AddSerilogLogging();

builder.Services.AddProblemDetails();
builder.Services.AddApiOpenApi();
builder.Services.AddApiHealthChecks();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // The API is only reachable through the reverse proxy (Caddy) on the internal Docker network.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddApplication();
builder.Services.AddInfrastructure();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseSerilogRequestLoggingWithHealthFilter();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseApiOpenApi();
}

app.MapApiHealthChecks();
app.MapPingEndpoints();

await app.RunAsync();
return 0;
