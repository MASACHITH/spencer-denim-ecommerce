namespace SpencerDenim.Api.Health;

/// <summary>
/// Minimal HTTP probe for the Docker HEALTHCHECK. The ASP.NET runtime image ships without curl or wget.
/// </summary>
internal static class HealthProbe
{
    private const string Flag = "--healthcheck";
    private const string DefaultUrl = "http://127.0.0.1:8080" + HealthEndpoints.LivenessPath;

    public static bool IsRequested(string[] args) => args.Length > 0 && args[0] == Flag;

    public static async Task<int> RunAsync(string[] args)
    {
        var url = args.Length > 1 ? args[1] : DefaultUrl;

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync(new Uri(url));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            await Console.Error.WriteLineAsync($"Health probe failed: {exception.Message}");
            return 1;
        }
    }
}
