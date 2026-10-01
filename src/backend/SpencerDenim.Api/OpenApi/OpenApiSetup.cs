using SpencerDenim.Domain.Branding;

namespace SpencerDenim.Api.OpenApi;

internal static class OpenApiSetup
{
    public const string DocumentName = "v1";
    public const string DocumentRoute = "/api/openapi/{documentName}.json";
    public const string SwaggerUiRoutePrefix = "api/swagger";

    public static IServiceCollection AddApiOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = $"{StoreBrand.Name} API";
                document.Info.Version = DocumentName;
                return Task.CompletedTask;
            });
        });

        return services;
    }

    /// <summary>OpenAPI JSON at /api/openapi/v1.json and Swagger UI at /api/swagger (Development only).</summary>
    public static WebApplication UseApiOpenApi(this WebApplication app)
    {
        app.MapOpenApi(DocumentRoute);
        app.UseSwaggerUI(options =>
        {
            options.RoutePrefix = SwaggerUiRoutePrefix;
            options.DocumentTitle = $"{StoreBrand.Name} API";
            options.SwaggerEndpoint($"/api/openapi/{DocumentName}.json", $"{StoreBrand.Name} API {DocumentName}");
        });

        return app;
    }
}
