using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Retroscope.Soak;

/// <summary>
/// The client process (client/soak.mjs, built on deploy/drills/tab.mjs) at <c>nice 10</c>, so the load generator yields
/// to everything else on the workstation. Operations go out as JSON lines; events come back the same way.
/// </summary>
internal sealed class ClientHost : IAsyncDisposable {
    private readonly Process _process;
    private readonly Channel<JsonElement> _replies = Channel.CreateUnbounded<JsonElement>();
    private readonly List<JsonElement> _reconnects = [];
    private readonly Lock _gate = new();
    private readonly Task _reader;
    private int _nextId;

    private ClientHost(Process process) {
        _process = process;
        _reader = Task.Run(ReadAsync);
    }

    public static ClientHost Start(string root, string baseUrl, JsonObject config) {
        var start = new ProcessStartInfo("nice") {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            WorkingDirectory = root,
        };
        foreach (var arg in new[] { "-n", "10", "node", Path.Combine(root, "tests/Retroscope.Soak/client/soak.mjs"), config.ToJsonString() }) {
            start.ArgumentList.Add(arg);
        }
        start.Environment["RS_E2E_BASE_URL"] = baseUrl;
        return new ClientHost(Process.Start(start)!);
    }

    /// <summary>Every reconnect the client reported so far: <c>{tab, code, closedAt, ms}</c>.</summary>
    public IReadOnlyList<JsonElement> Reconnects {
        get {
            lock (_gate) {
                return [.. _reconnects];
            }
        }
    }

    public Task<JsonElement> WaitForAsync(string ev, TimeSpan timeout) => NextAsync(e => e.GetProperty("ev").GetString() == ev, timeout);

    /// <summary>Sends an operation and waits for the event that answers it.</summary>
    public async Task<JsonElement> AskAsync(string op, TimeSpan timeout) {
        var id = ++_nextId;
        await _process.StandardInput.WriteLineAsync(new JsonObject { ["op"] = op, ["id"] = id }.ToJsonString());
        await _process.StandardInput.FlushAsync();
        return await NextAsync(e => e.TryGetProperty("id", out var x) && x.GetInt32() == id, timeout);
    }

    private async Task<JsonElement> NextAsync(Func<JsonElement, bool> match, TimeSpan timeout) {
        using var cancel = new CancellationTokenSource(timeout);
        while (true) {
            var element = await _replies.Reader.ReadAsync(cancel.Token);
            if (element.GetProperty("ev").GetString() == "error") {
                throw new InvalidOperationException($"soak client failed: {element}");
            }
            if (match(element)) {
                return element;
            }
        }
    }

    private async Task ReadAsync() {
        while (await _process.StandardOutput.ReadLineAsync() is { } line) {
            JsonElement element;
            try {
                element = JsonDocument.Parse(line).RootElement;
            } catch (JsonException) {
                Console.Error.WriteLine(line);
                continue;
            }
            switch (element.GetProperty("ev").GetString()) {
                case "reconnect":
                    lock (_gate) {
                        _reconnects.Add(element);
                    }
                    break;
                case "violation":
                    Console.Error.WriteLine($"violation: {element}");
                    break;
                default:
                    await _replies.Writer.WriteAsync(element);
                    break;
            }
        }
        _replies.Writer.TryWrite(JsonDocument.Parse("""{"ev":"error","message":"the client process exited"}""").RootElement);
    }

    public async ValueTask DisposeAsync() {
        if (!_process.HasExited) {
            _process.Kill(entireProcessTree: true);
        }
        await _process.WaitForExitAsync();
        await _reader;
        _process.Dispose();
    }
}
