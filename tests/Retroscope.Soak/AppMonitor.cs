using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Retroscope.Soak;

internal sealed record MemorySample(DateTimeOffset At, long RssBytes);

/// <summary>
/// Samples the app's own RSS once a second through <c>GET /api/admin/stats</c> (<c>Environment.WorkingSet</c>, U27) as
/// an app admin made for the run. A sample fails while the app or the client network is down; those seconds have no
/// value.
/// </summary>
internal sealed class AppMonitor : IDisposable {
    private const string Password = "soak monitor password 1";
    private readonly HttpClient _http;
    private readonly List<MemorySample> _samples = [];
    private readonly Lock _gate = new();

    public AppMonitor(string baseUrl) {
        var handler = new HttpClientHandler {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("X-RS", "1");
        _http.DefaultRequestHeaders.Add("Origin", baseUrl);
        _http.DefaultRequestHeaders.Add("X-Test-Client-Ip", "10.250.0.1");
    }

    public IReadOnlyList<MemorySample> Samples {
        get {
            lock (_gate) {
                return [.. _samples];
            }
        }
    }

    /// <summary>
    /// Signs a user up, makes it an app admin in the database, then signs in (the flag is read at sign-in).
    /// </summary>
    public async Task SignInAsAdminAsync(Stack stack) {
        var email = $"soak-admin-{Guid.NewGuid():N}@example.test";
        await SendGatedAsync(() => _http.PostAsJsonAsync("/api/auth/signup", new JsonObject {
            ["email"] = email, ["password"] = Password, ["fullName"] = "Soak Monitor",
        }), HttpStatusCode.Accepted);
        if (await stack.PsqlAsync($"update users set is_admin = true where email = '{email}' returning id") == "") {
            throw new InvalidOperationException("the monitor account was not created");
        }
        await SendGatedAsync(() => _http.PostAsJsonAsync("/api/auth/login", new JsonObject {
            ["email"] = email, ["password"] = Password,
        }), HttpStatusCode.OK);
    }

    public async Task RunAsync(CancellationToken cancellationToken) {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken)) {
            try {
                using var sample = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sample.CancelAfter(TimeSpan.FromSeconds(3));
                using var stats = await _http.GetFromJsonAsync<JsonDocument>("/api/admin/stats", sample.Token);
                var rss = stats!.RootElement.GetProperty("rssBytes").GetInt64();
                lock (_gate) {
                    _samples.Add(new MemorySample(DateTimeOffset.UtcNow, rss));
                }
            } catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or JsonException
                && !cancellationToken.IsCancellationRequested) {
                // The app is restarting or the client network is partitioned. A kill can also cut a response short
                // (HttpIOException), which the first nightly run let escape and lost its verdict to.
            }
        }
    }

    // The bcrypt gate answers 503 busy while hashes queue up (§e).
    private static async Task SendGatedAsync(Func<Task<HttpResponseMessage>> send, HttpStatusCode expected) {
        for (var attempt = 0; ; attempt++) {
            using var response = await send();
            if (response.StatusCode == expected) {
                return;
            }
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable || attempt == 10) {
                throw new InvalidOperationException($"{response.RequestMessage?.RequestUri} answered {(int)response.StatusCode}");
            }
            await Task.Delay(2000);
        }
    }

    public void Dispose() => _http.Dispose();
}
