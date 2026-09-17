using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>Hosts the real API in memory with a given connection string and environment.</summary>
public sealed class ApiFactory(string connectionString, string environment = "Testing") : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("ConnectionStrings:Database", connectionString);
        builder.UseSetting("Logging:ConsoleFormat", "text");
    }
}
