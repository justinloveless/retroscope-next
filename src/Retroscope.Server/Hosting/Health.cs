namespace Retroscope.Server.Hosting;

public static class Health {
    public const string Path = "/healthz";

    public static void Map(IEndpointRouteBuilder app) {
        app.MapGet(Path, () => Results.Text("ok"));
    }

    // `retroscope healthcheck`: the chiseled image has no shell or curl, so the binary probes itself.
    public static async Task<int> ProbeAsync(string[] args) {
        var port = Environment.GetEnvironmentVariable("HEALTHCHECK_PORT") ?? "8080";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try {
            using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}{Path}"));
            return response.IsSuccessStatusCode ? 0 : 1;
        } catch (HttpRequestException) {
            return 1;
        } catch (TaskCanceledException) {
            return 1;
        }
    }
}
