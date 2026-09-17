using SpencerDenim.Api.Endpoints;
using SpencerDenim.Api.Health;
using SpencerDenim.Api.Logging;
using SpencerDenim.Api.OpenApi;
using SpencerDenim.Application;
using SpencerDenim.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddSerilogLogging();

builder.Services.AddProblemDetails();
builder.Services.AddApiOpenApi();
builder.Services.AddApiHealthChecks();

builder.Services.AddApplication();
builder.Services.AddInfrastructure();

var app = builder.Build();

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
