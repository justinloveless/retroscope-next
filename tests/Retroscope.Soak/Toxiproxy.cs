using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Retroscope.Soak;

/// <summary>
/// The client side of the network (§j): 150 ± 100 ms latency, 0.5 % of connections reset at a time, and partitions
/// in which no client reaches the server. Toxiproxy's HTTP API (github.com/Shopify/toxiproxy, README "HTTP API").
/// </summary>
internal sealed class Toxiproxy(int apiPort) : IDisposable {
    private const string Proxy = "edge";
    private readonly HttpClient _http = new() { BaseAddress = new Uri($"http://127.0.0.1:{apiPort}"), Timeout = TimeSpan.FromSeconds(10) };

    public async Task CreateAsync() {
        for (var attempt = 0; ; attempt++) {
            try {
                var response = await _http.PostAsJsonAsync("/proxies", new JsonObject {
                    ["name"] = Proxy,
                    ["listen"] = "0.0.0.0:8443",
                    ["upstream"] = "proxy:8443",
                    ["enabled"] = true,
                });
                response.EnsureSuccessStatusCode();
                return;
            } catch (HttpRequestException) when (attempt < 30) {
                await Task.Delay(1000);
            }
        }
    }

    public Task AddLatencyAsync(int latencyMs, int jitterMs) =>
        AddToxicAsync("latency", "latency", 1, new JsonObject { ["latency"] = latencyMs, ["jitter"] = jitterMs });

    /// <summary>
    /// Resets each live connection (and each one opened meanwhile) with probability <paramref name="share"/>.
    /// </summary>
    public async Task ResetSomeAsync(double share) {
        await AddToxicAsync("reset", "reset_peer", share, new JsonObject { ["timeout"] = 0 });
        await Task.Delay(300);
        await RemoveToxicAsync("reset");
    }

    /// <summary>
    /// A disabled proxy closes every client connection and refuses new ones until it is enabled again.
    /// </summary>
    public async Task SetEnabledAsync(bool enabled) {
        var response = await _http.PostAsJsonAsync($"/proxies/{Proxy}", new JsonObject { ["enabled"] = enabled });
        response.EnsureSuccessStatusCode();
    }

    private async Task AddToxicAsync(string name, string type, double toxicity, JsonObject attributes) {
        var response = await _http.PostAsJsonAsync($"/proxies/{Proxy}/toxics", new JsonObject {
            ["name"] = name,
            ["type"] = type,
            ["stream"] = "downstream",
            ["toxicity"] = toxicity,
            ["attributes"] = attributes,
        });
        response.EnsureSuccessStatusCode();
    }

    private async Task RemoveToxicAsync(string name) {
        using var response = await _http.DeleteAsync($"/proxies/{Proxy}/toxics/{name}");
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}
